using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using TmdbStatusBadge.Services;

namespace TmdbStatusBadge
{
    /// <summary>
    /// 插件入口。Emby 加载 DLL 时会反射查找实现 IPlugin 的类型并实例化。
    /// 继承 BasePlugin&lt;T&gt; 可以自动获得配置的读写与持久化。
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static Plugin? Instance { get; private set; }

        public override string Name => "TMDB 完结状态角标";

        public override Guid Id => Guid.Parse("8f2b6b2e-6c2a-4a1a-9d3a-6a1c3b7e9a10");

        public override string Description =>
            "根据 TMDB 数据自动检测剧集是否完结，并在海报上叠加完结/更新中角标。";

        public ILibraryManager LibraryManager { get; }
        public ILogger Logger { get; }
        public BadgeService BadgeService { get; }

        public Plugin(
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer,
            ILibraryManager libraryManager,
            ILogManager logManager,
            IFileSystem fileSystem)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            LibraryManager = libraryManager;
            Logger = logManager.GetLogger(Name);
            BadgeService = new BadgeService(this, libraryManager, Logger, applicationPaths, fileSystem);

            // 事件监听：新集/剧集信息更新后，做增量单剧检测
            libraryManager.ItemAdded += OnItemChanged;
            libraryManager.ItemUpdated += OnItemChanged;
        }

        private void OnItemChanged(object? sender, ItemChangeEventArgs e)
        {
            if (!Configuration.Enabled || !Configuration.EnableEventTrigger)
            {
                return;
            }

            // Episode 或 Series 变化都触发；BadgeService 内部会找到所属 Series 再处理
            BadgeService.EnqueueIncrementalCheck(e.Item);
        }

        /// <summary>
        /// 注册后台"插件"页面里显示的配置页。
        /// 分两个条目注册：HTML 页面本身 + 单独的 JS 控制器文件。
        /// HTML 里通过 data-controller="__plugin/tmdbstatusbadgejs" 引用下面这个
        /// Name="tmdbstatusbadgejs" 的条目。这是 Emby 官方推荐的做法——直接写在
        /// HTML 里的内联 &lt;script&gt; 标签，在 Emby 通过 AJAX 无刷新切换页面时
        /// 不一定会被执行，必须拆成独立 JS 文件通过 data-controller 显式加载。
        /// </summary>
        public IEnumerable<PluginPageInfo> GetPages()
        {
            yield return new PluginPageInfo
            {
                Name = "tmdbstatusbadge",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                EnableInMainMenu = true
            };

            yield return new PluginPageInfo
            {
                Name = "tmdbstatusbadgejs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js"
            };

            yield return new PluginPageInfo
            {
                Name = "tmdbstatusbadgeseries",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.seriesPage.html",
                EnableInMainMenu = true,
                DisplayName = "TMDB 角标 - 剧集状态管理"
            };

            yield return new PluginPageInfo
            {
                Name = "tmdbstatusbadgeseriesjs",
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.seriesPage.js"
            };
        }
    }
}
