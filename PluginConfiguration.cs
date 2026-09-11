using MediaBrowser.Model.Plugins;

namespace TmdbStatusBadge
{
    /// <summary>
    /// 插件配置项，对应 Emby 后台"插件"页面的设置表单。
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>TMDB API Key（v3 auth）</summary>
        public string TmdbApiKey { get; set; } = string.Empty;

        /// <summary>是否启用插件逻辑（配置页总开关）</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>是否在新集入库后立即触发单部剧检测</summary>
        public bool EnableEventTrigger { get; set; } = true;

        /// <summary>角标位置：0=左上 1=右上 2=左下 3=右下</summary>
        public int BadgePosition { get; set; } = 3;

        /// <summary>角标宽度占海报宽度的百分比，素材按此等比缩放</summary>
        public int BadgeScalePercent { get; set; } = 26;

        /// <summary>两次请求 TMDB 之间的最小间隔（毫秒），避免限流</summary>
        public int TmdbRequestIntervalMs { get; set; } = 300;

        /// <summary>同一部剧多久之内不重复查询 TMDB（小时）</summary>
        public int CacheHours { get; set; } = 24;

        /// <summary>是否用自定义图片代替内置的"完结"角标</summary>
        public bool UseCustomEndedBadge { get; set; } = false;

        /// <summary>自定义"完结"角标图片，Base64 编码的 PNG 数据（不含 data:image/... 前缀）</summary>
        public string? CustomEndedBadgeBase64 { get; set; }

        /// <summary>是否用自定义图片代替内置的"更新中"角标</summary>
        public bool UseCustomOngoingBadge { get; set; } = false;

        /// <summary>自定义"更新中"角标图片，Base64 编码的 PNG 数据（不含 data:image/... 前缀）</summary>
        public string? CustomOngoingBadgeBase64 { get; set; }

        /// <summary>
        /// 状态发生变化时，是否触发"完整元数据刷新 + 替换图片"（会重新从 Emby 配置的元数据源拉取信息，
        /// 比较吃资源，且如果你本地手动改过标题/简介之类，也可能被覆盖）。
        /// 关闭时（默认）只是轻量地通知 Emby"这个项目的图片变了"，不动其它元数据。
        /// </summary>
        public bool FullMetadataRefreshOnChange { get; set; } = false;
    }
}
