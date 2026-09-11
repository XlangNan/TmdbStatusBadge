using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using TmdbStatusBadge.Tmdb;

namespace TmdbStatusBadge.Services
{
    public enum BadgeStatus
    {
        /// <summary>还没检测过，或者检测失败</summary>
        Unknown = 0,
        /// <summary>本地集数已追平 TMDB 且 TMDB 状态为完结</summary>
        Ended = 1,
        /// <summary>还在更新 / 本地集数落后于 TMDB</summary>
        Ongoing = 2
    }

    /// <summary>
    /// 一部剧的状态总览，给"剧集状态管理"页面展示用。
    /// AutoStatus 是插件自动判断出来的结果；Override 是用户手动强制指定的结果（没设置就是 null）；
    /// EffectiveStatus 是最终生效的结果（Override 优先，没设置才用 AutoStatus）。
    /// </summary>
    public record SeriesStatusOverview(Guid SeriesId, string Name, BadgeStatus AutoStatus, BadgeStatus? Override, BadgeStatus EffectiveStatus);

    /// <summary>
    /// 核心业务逻辑：
    /// 1. 根据 Series 的 TMDB Id 查询 TMDB 详情
    /// 2. 对比本地 Emby 库里已入库的集数
    /// 3. 把判定结果缓存到内存字典，供 BadgeImageEnhancer 读取
    ///
    /// 判定结果、以及用户手动设置的强制覆盖，都会各自写一份到磁盘
    /// （Emby 数据目录下 tmdbstatusbadge/status.json 和 overrides.json），
    /// 插件启动时会从这两个文件里把上次的结果读回内存——这样 Emby 重启后，
    /// 角标不会瞬间全部消失、也不会丢掉手动设置过的状态。
    /// </summary>
    public class BadgeService
    {
        private readonly Plugin _plugin;
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;
        private readonly TmdbClient _tmdbClient;
        private readonly IFileSystem _fileSystem;
        private readonly string _statusFilePath;
        private readonly string _overrideFilePath;
        private readonly object _fileLock = new();

        // SeriesId -> 自动判定结果
        private readonly ConcurrentDictionary<Guid, BadgeStatus> _statusMap = new();

        // SeriesId -> 用户手动强制指定的结果（优先级比自动判定高）
        private readonly ConcurrentDictionary<Guid, BadgeStatus> _overrideMap = new();

        public BadgeService(Plugin plugin, ILibraryManager libraryManager, ILogger logger, IApplicationPaths applicationPaths, IFileSystem fileSystem)
        {
            _plugin = plugin;
            _libraryManager = libraryManager;
            _logger = logger;
            _fileSystem = fileSystem;
            _tmdbClient = new TmdbClient(logger);

            var dataDir = Path.Combine(applicationPaths.DataPath, "tmdbstatusbadge");
            Directory.CreateDirectory(dataDir);
            _statusFilePath = Path.Combine(dataDir, "status.json");
            _overrideFilePath = Path.Combine(dataDir, "overrides.json");

            LoadMapFromDisk(_statusFilePath, _statusMap, "自动判定状态");
            LoadMapFromDisk(_overrideFilePath, _overrideMap, "手动覆盖状态");
        }

        /// <summary>
        /// 供 IImageEnhancer 读取的最终生效状态：手动覆盖优先，没设置过覆盖才用自动判定的结果。
        /// </summary>
        public BadgeStatus GetStatus(Guid seriesId)
        {
            if (_overrideMap.TryGetValue(seriesId, out var overrideStatus))
            {
                return overrideStatus;
            }

            return _statusMap.TryGetValue(seriesId, out var status) ? status : BadgeStatus.Unknown;
        }

        /// <summary>
        /// 设置/清除某一部剧的手动覆盖状态。status 传 null 表示"恢复自动判断"，清掉覆盖。
        /// </summary>
        public void SetManualOverride(Guid seriesId, BadgeStatus? status)
        {
            if (status == null)
            {
                _overrideMap.TryRemove(seriesId, out _);
            }
            else
            {
                _overrideMap[seriesId] = status.Value;
            }

            SaveMapToDisk(_overrideFilePath, _overrideMap);

            // 通知 Emby 这个项目的图片变了，已经打开的网格列表/详情页会实时收到推送刷新，
            // 不用用户手动强制刷新浏览器才能看到新角标。手动标成"完结"的时候，
            // 跟自动检测到完结一样，强制走完整刷新。
            NotifyImageChanged(seriesId, forceFullRefresh: status == BadgeStatus.Ended);
        }

        /// <summary>
        /// 告诉 Emby"这个项目的图片变了"。走两种不同力度：
        /// - 轻量（默认）：只调用 UpdateImages，触发已连接客户端实时刷新
        /// - 完整刷新：真正调用 RefreshMetadata 做一次完整刷新 + 替换图片，比较吃资源，
        ///   但效果更彻底（连 Emby 自己缓存的各种尺寸图片都会一起重新生成）
        ///
        /// 触发条件是"配置里的总开关打开" 或者 "forceFullRefresh 传 true"（新集入库检测到
        /// 这部剧变成完结时，不管总开关有没有开，都会强制走一次完整刷新——这是"追完最后一集，
        /// 自动确认完结，自动刷新"这个场景的核心实现）。
        /// </summary>
        private void NotifyImageChanged(Guid seriesId, bool forceFullRefresh = false)
        {
            try
            {
                if (_libraryManager.GetItemById(seriesId) is not Series series)
                {
                    return;
                }

                if (forceFullRefresh || _plugin.Configuration.FullMetadataRefreshOnChange)
                {
                    // RefreshMetadata 是异步的，这里不阻塞调用方（调用方大多是同步方法），
                    // 丢到后台跑，跑完自己记日志，失败也不影响角标本身已经算对了的结果。
                    _ = Task.Run(() => TriggerFullRefreshAsync(series, CancellationToken.None));
                }
                else
                {
                    _libraryManager.UpdateImages(series);
                }
            }
            catch (Exception ex)
            {
                // 这一步只是锦上添花的"立刻刷新"，就算失败也不影响角标本身已经算对了，
                // 用户手动刷新一下页面照样能看到，所以这里只记日志、不往上抛异常。
                _logger.ErrorException($"[TmdbStatusBadge] 通知 Emby 刷新图片失败 seriesId={seriesId}", ex);
            }
        }

        /// <summary>
        /// 真正触发一次完整的"刷新元数据 + 替换图片"。
        /// ReplaceAllMetadata 故意不开：只重新拉取/校验信息，不强制覆盖你手动改过的标题、简介这些；
        /// ReplaceAllImages 开着：确保 Emby 自己各种尺寸的缓存图（包括网格缩略图）都重新生成。
        /// </summary>
        private async Task TriggerFullRefreshAsync(Series series, CancellationToken cancellationToken)
        {
            try
            {
                var options = new MetadataRefreshOptions(_fileSystem)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = true,
                    ForceSave = true,
                    // 显式关掉，不确定 SDK 默认值是什么，不想依赖不确定的默认行为——
                    // 我们只想刷新这一部剧本身（连带它自己的季/集也算合理），
                    // 不希望意外波及库里其它不相关的内容。
                    Recursive = false
                };

                await series.RefreshMetadata(options, cancellationToken);

                // 双保险：RefreshMetadata 走完之后再补一次通知，确保前端一定收到推送
                _libraryManager.UpdateImages(series);

                _logger.Info($"[TmdbStatusBadge] 已触发 {series.Name} 的完整元数据/图片刷新");
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[TmdbStatusBadge] 触发完整元数据刷新失败 series={series.Name}", ex);
            }
        }

        /// <summary>
        /// 给"剧集状态管理"页面用：列出库里所有剧，附带自动判定结果、手动覆盖状态、最终生效状态。
        /// </summary>
        public List<SeriesStatusOverview> GetSeriesOverview()
        {
            var allSeries = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Series) },
                Recursive = true
            }).OfType<Series>();

            return allSeries
                .Select(s =>
                {
                    var auto = _statusMap.TryGetValue(s.Id, out var a) ? a : BadgeStatus.Unknown;
                    BadgeStatus? overrideStatus = _overrideMap.TryGetValue(s.Id, out var o) ? o : null;
                    var effective = overrideStatus ?? auto;
                    return new SeriesStatusOverview(s.Id, s.Name, auto, overrideStatus, effective);
                })
                .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private void LoadMapFromDisk(string path, ConcurrentDictionary<Guid, BadgeStatus> target, string label)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return;
                }

                var json = File.ReadAllText(path);
                var saved = JsonSerializer.Deserialize<Dictionary<Guid, BadgeStatus>>(json);
                if (saved == null)
                {
                    return;
                }

                foreach (var kv in saved)
                {
                    target[kv.Key] = kv.Value;
                }

                _logger.Info($"[TmdbStatusBadge] 从磁盘恢复了 {saved.Count} 条{label}");
            }
            catch (Exception ex)
            {
                // 读取失败（文件损坏/格式不对之类）不应该导致插件加载失败，
                // 忽略掉当成"没有历史数据"处理。
                _logger.ErrorException($"[TmdbStatusBadge] 读取{label}文件失败，忽略并继续", ex);
            }
        }

        private void SaveMapToDisk(string path, ConcurrentDictionary<Guid, BadgeStatus> source)
        {
            try
            {
                var snapshot = source.ToDictionary(kv => kv.Key, kv => kv.Value);
                var json = JsonSerializer.Serialize(snapshot);

                // 简单加个锁，避免多个并发的状态变化同时写文件时互相打架
                lock (_fileLock)
                {
                    File.WriteAllText(path, json);
                }
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[TmdbStatusBadge] 写入状态文件失败：{path}", ex);
            }
        }

        /// <summary>
        /// 事件触发的增量检测：传入 Episode 或 Series，都会定位到所属 Series 再处理。
        /// 用 Task.Run 丢到后台，不阻塞库扫描主线程。
        /// </summary>
        public void EnqueueIncrementalCheck(BaseItem item)
        {
            Series? series = item switch
            {
                Series s => s,
                Episode ep => ep.Series,
                _ => null
            };

            if (series == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await CheckSeriesAsync(series, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.ErrorException($"[TmdbStatusBadge] 增量检测失败 series={series.Name}", ex);
                }
            });
        }

        /// <summary>
        /// 全库扫描入口，由 ScheduledTask 调用。
        /// </summary>
        public async Task RunFullScanAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var config = _plugin.Configuration;

            if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                _logger.Warn("[TmdbStatusBadge] 未配置 TMDB API Key，跳过扫描");
                return;
            }

            var allSeries = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Series) },
                Recursive = true
            }).OfType<Series>().ToList();

            var total = allSeries.Count;
            var done = 0;

            foreach (var series in allSeries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await CheckSeriesAsync(series, cancellationToken);

                done++;
                progress.Report(done * 100.0 / Math.Max(total, 1));
            }

            // 全量扫描结束后兜底存一次盘，保证磁盘文件和内存状态完全一致
            // （单个状态变化时也会存盘，这里是双重保险，防止中途有遗漏）。
            SaveMapToDisk(_statusFilePath, _statusMap);
        }

        private async Task CheckSeriesAsync(Series series, CancellationToken cancellationToken)
        {
            var config = _plugin.Configuration;

            if (!config.Enabled || string.IsNullOrWhiteSpace(config.TmdbApiKey))
            {
                return;
            }

            // 手动覆盖过的剧不再参与自动判断——用户既然手动定了，就不该被下一次扫描悄悄改回去。
            // 想恢复自动判断，去"剧集状态管理"页面把覆盖清掉即可。
            if (_overrideMap.ContainsKey(series.Id))
            {
                return;
            }

            if (!series.ProviderIds.TryGetValue("Tmdb", out var tmdbIdStr) ||
                !int.TryParse(tmdbIdStr, out var tmdbId))
            {
                _logger.Debug($"[TmdbStatusBadge] {series.Name} 没有 TMDB Id，跳过");
                return;
            }

            var detail = await _tmdbClient.GetTvDetailAsync(
                tmdbId, config.TmdbApiKey, config.CacheHours, config.TmdbRequestIntervalMs, cancellationToken);

            if (detail == null)
            {
                return;
            }

            // 本地已入库集数：排除掉特典（Season 0）以贴近 TMDB 的常规集数统计
            // 注意：Episode 的直接父级是 Season，不是 Series，所以不能用 ParentIds 精确匹配到 Series，
            // 要用 AncestorIds 做递归查找（Series 是 Episode 的祖先节点）
            var localEpisodeCount = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Episode) },
                AncestorIds = new[] { series.InternalId },
                Recursive = true
            }).OfType<Episode>().Count(e => (e.ParentIndexNumber ?? 0) > 0);

            // TMDB 侧同样排除 Season 0（Specials）
            var tmdbRegularEpisodeCount = detail.Seasons
                .Where(s => s.SeasonNumber > 0)
                .Sum(s => s.EpisodeCount);

            var isEnded = detail.IsEnded && localEpisodeCount >= tmdbRegularEpisodeCount && tmdbRegularEpisodeCount > 0;

            var newStatus = isEnded ? BadgeStatus.Ended : BadgeStatus.Ongoing;
            var changed = !_statusMap.TryGetValue(series.Id, out var oldStatus) || oldStatus != newStatus;

            // 状态存在内存字典里，BadgeImageEnhancer.GetConfigurationCacheKey() 每次
            // 都会读取这个字典的最新值，Emby 下次请求图片时会重新算一次 cache key，
            // key 变了就会重新调用 EnhanceImageAsync 生成新图。
            // 但"下次请求"可能要等用户刷新页面才会发生——所以下面 changed 分支里
            // 额外调用 NotifyImageChanged，主动推一次，网格列表能立刻跟着变。
            _statusMap[series.Id] = newStatus;

            _logger.Info($"[TmdbStatusBadge] {series.Name} 本地={localEpisodeCount} TMDB={tmdbRegularEpisodeCount} " +
                         $"TMDB状态={detail.Status} => {newStatus}{(changed ? " (变化)" : "")}");

            if (changed)
            {
                // 只在状态真的变化时才写磁盘/推送通知，减少不必要的 I/O——
                // 大部分情况下命中 TMDB 缓存、状态和上次一样，没必要每次都重写文件。
                SaveMapToDisk(_statusFilePath, _statusMap);

                // "变成完结"这个事件本身很稀有（一部剧一辈子也就发生一次），
                // 不管"完整元数据刷新"这个开关有没有开，都强制做一次彻底刷新——
                // 这是新集入库自动检测流程里最终想要的效果：追完最后一集 -> 确认完结 -> 自动刷新。
                // 其它变化（比如刚开播、还在更新）继续走轻量通知，避免每集入库都触发一次重刷。
                var forceFullRefresh = newStatus == BadgeStatus.Ended;
                NotifyImageChanged(series.Id, forceFullRefresh);
            }
        }
    }
}
