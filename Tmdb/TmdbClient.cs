using System;
using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;

namespace TmdbStatusBadge.Tmdb
{
    /// <summary>
    /// 简单的 TMDB v3 客户端，带最小间隔限流 + 内存缓存，
    /// 避免大库全量扫描时打爆 TMDB 请求频率限制。
    /// </summary>
    public class TmdbClient
    {
        private const string BaseUrl = "https://api.themoviedb.org/3";

        private readonly HttpClient _http;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _rateLimitLock = new(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;

        // tmdbId -> (数据, 拉取时间)
        private readonly ConcurrentDictionary<int, (TmdbTvDetail Data, DateTime FetchedUtc)> _cache = new();

        // tmdbId -> 正在进行中的请求。用来解决"同一部剧的好几集几乎同时触发检查"时，
        // 24 小时缓存还没来得及写入，导致好几个并发调用都以为"没查过"、
        // 各自重复发起 HTTP 请求打到 TMDB 的问题——并发请求合并成一次真正的网络请求，
        // 其余调用直接等这一次的结果。
        private readonly ConcurrentDictionary<int, Lazy<Task<TmdbTvDetail?>>> _inFlight = new();

        public TmdbClient(ILogger logger)
        {
            _logger = logger;
            _http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
        }

        public Task<TmdbTvDetail?> GetTvDetailAsync(int tmdbId, string apiKey, int cacheHours, int minIntervalMs, CancellationToken ct)
        {
            if (_cache.TryGetValue(tmdbId, out var cached) &&
                DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromHours(cacheHours))
            {
                return Task.FromResult<TmdbTvDetail?>(cached.Data);
            }

            var lazy = _inFlight.GetOrAdd(tmdbId, id =>
                new Lazy<Task<TmdbTvDetail?>>(() => FetchAndCacheAsync(id, apiKey, minIntervalMs, ct)));

            return AwaitAndCleanupAsync(tmdbId, lazy);
        }

        private async Task<TmdbTvDetail?> AwaitAndCleanupAsync(int tmdbId, Lazy<Task<TmdbTvDetail?>> lazy)
        {
            try
            {
                return await lazy.Value;
            }
            finally
            {
                // 这次请求（不管成功失败）已经结束，把"进行中"标记摘掉，
                // 下次缓存过期后需要重新查询时能正常再次触发。
                _inFlight.TryRemove(tmdbId, out _);
            }
        }

        private async Task<TmdbTvDetail?> FetchAndCacheAsync(int tmdbId, string apiKey, int minIntervalMs, CancellationToken ct)
        {
            await ThrottleAsync(minIntervalMs, ct);

            var url = $"{BaseUrl}/tv/{tmdbId}?api_key={apiKey}&language=zh-CN";

            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.Warn($"[TmdbStatusBadge] TMDB 请求失败 tmdbId={tmdbId} status={(int)resp.StatusCode}");
                    return null;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var detail = JsonSerializer.Deserialize<TmdbTvDetail>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (detail != null)
                {
                    _cache[tmdbId] = (detail, DateTime.UtcNow);
                }

                return detail;
            }
            catch (Exception ex)
            {
                _logger.ErrorException($"[TmdbStatusBadge] 请求 TMDB 异常 tmdbId={tmdbId}", ex);
                return null;
            }
        }

        private async Task ThrottleAsync(int minIntervalMs, CancellationToken ct)
        {
            await _rateLimitLock.WaitAsync(ct);
            try
            {
                var elapsed = DateTime.UtcNow - _lastRequestUtc;
                var wait = TimeSpan.FromMilliseconds(minIntervalMs) - elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct);
                }
                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                _rateLimitLock.Release();
            }
        }

        public void ClearCache() => _cache.Clear();
    }
}
