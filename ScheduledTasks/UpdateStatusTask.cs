using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;

namespace TmdbStatusBadge.ScheduledTasks
{
    /// <summary>
    /// 注册到 Emby 后台"计划任务"页面。默认每天凌晨 3 点自动跑一次，
    /// 用户也可以在 UI 里改触发时间或手动点"立即运行"。
    /// </summary>
    public class UpdateStatusTask : IScheduledTask
    {
        private readonly ILogger _logger;

        public UpdateStatusTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(UpdateStatusTask));
        }

        public string Name => "扫描剧集完结状态（TMDB）";

        public string Key => "TmdbStatusBadgeFullScanTask";

        public string Description => "遍历剧集库，对比 TMDB 集数与本地入库集数，更新完结/更新中角标状态";

        public string Category => "库";

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                _logger.Warn("[TmdbStatusBadge] 插件实例未初始化");
                return;
            }

            await plugin.BadgeService.RunFullScanAsync(progress, cancellationToken);
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // 默认每天凌晨 3 点自动执行一次全量扫描
            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfo.TriggerDaily,
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            };
        }
    }
}
