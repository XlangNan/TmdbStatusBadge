using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Services;
using TmdbStatusBadge.Services;

namespace TmdbStatusBadge.Api
{
    /// <summary>
    /// GET /TmdbStatusBadge/Series
    /// 返回库里所有剧集的当前状态（自动判定 + 手动覆盖 + 最终生效结果），
    /// 供"剧集状态管理"页面渲染列表用。
    /// </summary>
    [Route("/TmdbStatusBadge/Series", "GET", Summary = "列出所有剧集的完结状态")]
    public class GetSeriesStatusList : IReturn<List<SeriesStatusDto>>
    {
    }

    /// <summary>
    /// POST /TmdbStatusBadge/Override
    /// 设置（或清除）某一部剧的手动覆盖状态。
    /// Status 传 "Auto" 表示清除覆盖、恢复自动判断；传 "Ended" / "Ongoing" 表示强制指定。
    /// </summary>
    [Route("/TmdbStatusBadge/Override", "POST", Summary = "设置某部剧的手动覆盖状态")]
    public class SetSeriesOverride : IReturnVoid
    {
        public string SeriesId { get; set; } = string.Empty;

        public string Status { get; set; } = "Auto";
    }

    /// <summary>
    /// 返回给前端页面用的精简 DTO，字符串类型的状态比枚举更方便 JS 那边直接判断/显示。
    /// </summary>
    public class SeriesStatusDto
    {
        public string SeriesId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string AutoStatus { get; set; } = string.Empty;

        /// <summary>null 表示没有手动覆盖过</summary>
        public string? Override { get; set; }

        public string EffectiveStatus { get; set; } = string.Empty;
    }

    /// <summary>
    /// Emby 官方文档认可的自定义接口写法：实现 IService，用 [Route] 标注请求 DTO，
    /// 方法名对应 HTTP 方法（Get/Post/...），Emby 的 Service Stack 风格路由会自动分发过来。
    /// </summary>
    public class SeriesOverrideService : IService
    {
        public object Get(GetSeriesStatusList request)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return new List<SeriesStatusDto>();
            }

            return plugin.BadgeService.GetSeriesOverview()
                .Select(x => new SeriesStatusDto
                {
                    SeriesId = x.SeriesId.ToString("N"),
                    Name = x.Name,
                    AutoStatus = x.AutoStatus.ToString(),
                    Override = x.Override?.ToString(),
                    EffectiveStatus = x.EffectiveStatus.ToString()
                })
                .ToList();
        }

        public void Post(SetSeriesOverride request)
        {
            var plugin = Plugin.Instance;
            if (plugin == null)
            {
                return;
            }

            if (!Guid.TryParse(request.SeriesId, out var seriesId))
            {
                return;
            }

            BadgeStatus? status = request.Status switch
            {
                "Ended" => BadgeStatus.Ended,
                "Ongoing" => BadgeStatus.Ongoing,
                _ => null // "Auto" 或其它任何值都当成"清除覆盖"处理
            };

            plugin.BadgeService.SetManualOverride(seriesId, status);
        }
    }
}
