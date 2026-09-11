# TmdbStatusBadge

> 根据 TMDB 数据自动判断 Emby 剧集是否完结，在海报角落叠加「完结 / 更新中」角标。

![预览](docs/preview.png)

装好之后完全自动运行：新集入库自动检测这一部剧、每天定时全库扫描兜底，
判断结果和图片都会缓存，Emby 重启也不会丢。也支持手动强制指定某部剧的状态。

---

## 目录

- [功能一览](#功能一览)
- [安装](#安装)
  - [方式一：直接下载编译好的版本（推荐）](#方式一直接下载编译好的版本推荐)
  - [方式二：自己编译](#方式二自己编译)
- [配置说明](#配置说明)
- [剧集状态管理页面](#剧集状态管理页面)
- [自定义角标图片](#自定义角标图片)
- [工作原理](#工作原理)
- [目录结构](#目录结构)
- [常见问题](#常见问题)
- [免责声明](#免责声明)
- [License](#license)

---

## 功能一览

- ✅ 根据 [TheMovieDB](https://www.themoviedb.org/) 的剧集状态 + 本地已入库集数，
  自动判断一部剧是「完结」还是「更新中」
- ✅ 在海报角落叠加角标，四个角落任选，大小可调，自带一套黑底红下划线样式
  （也支持[换成自己的图片](#自定义角标图片)）
- ✅ 新集入库后自动检测**这一部剧**（不用等定时任务），也有每天凌晨的全库
  定时扫描兜底
- ✅ 判断结果落盘持久化，Emby / 容器重启不会丢，角标不会瞬间消失
- ✅ 独立的「[剧集状态管理](#剧集状态管理页面)」页面，可以手动强制指定某部剧
  的状态，覆盖自动判断结果
- ✅ 检测到剧集变成「完结」时，自动触发 Emby 的完整元数据刷新，让海报（包括
  网格列表的缩略图）立刻更新，不用手动刷新浏览器
- ✅ 对 TMDB 请求做了限流 + 缓存 + 并发合并，不会因为库大就把 TMDB 打爆

## 安装

### 方式一：直接下载编译好的版本（推荐）

去 [Releases](../../releases) 页面下载最新的 zip，解压后是这样的结构：

```
TmdbStatusBadge/
├── TmdbStatusBadge.dll
├── SkiaSharp.dll
└── runtimes/
```

把整个 `TmdbStatusBadge` 文件夹拷进 Emby 的插件目录：

| 部署方式 | 插件目录 |
|---|---|
| Windows 原生安装 | `C:\ProgramData\Emby-Server\programdata\plugins` |
| Linux 原生安装 | `/var/lib/emby/plugins`（视安装方式而定） |
| Docker | 容器内一般是 `/config/plugins`，对应你 `-v` 映射到宿主机的目录 |

拷贝完重启 Emby Server，后台「插件」页面应该能看到 **TMDB 完结状态角标**。

> **版本兼容性**：Release 里会写明编译时对应的 Emby Server 版本。同一个大版本号
> （比如都是 4.8.x）之间基本通用；跨大版本升级 Emby 后如果插件加载失败，多半是
> 插件用到的某个接口签名变了，需要重新编译，见下方[常见问题](#常见问题)。

### 方式二：自己编译

需要 [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)。

**1. 拿到 Emby 的参考 DLL**（编译时需要，运行时不需要，不会打包进最终产物）

从你自己的 Emby Server 安装目录里拷出这三个文件，放进项目的 `lib/` 目录：

- `MediaBrowser.Controller.dll`
- `MediaBrowser.Common.dll`
- `MediaBrowser.Model.dll`

常见路径：

```bash
# Windows 原生安装
C:\Program Files\Emby-Server\system\

# Linux 原生安装
/opt/emby-server/system/

# Docker（把 emby 换成你的容器名）
docker cp emby:/system/MediaBrowser.Controller.dll ./lib/
docker cp emby:/system/MediaBrowser.Common.dll ./lib/
docker cp emby:/system/MediaBrowser.Model.dll ./lib/
```

**2. 编译**

```bash
cd TmdbStatusBadge
dotnet restore
dotnet build -c Release
```

产物在 `bin/Release/`（因为 `csproj` 里关掉了 `AppendTargetFrameworkToOutputPath`，
不会有 `net6.0` 这层子目录）。

**3. 打包**

```bash
mkdir -p dist/TmdbStatusBadge
cp bin/Release/TmdbStatusBadge.dll dist/TmdbStatusBadge/
cp bin/Release/SkiaSharp.dll dist/TmdbStatusBadge/
cp -r bin/Release/runtimes dist/TmdbStatusBadge/
```

`dist/TmdbStatusBadge/` 就是最终成品，接下来同[方式一](#方式一直接下载编译好的版本推荐)拷进插件目录即可。

## 配置说明

插件装好、重启 Emby 后，后台「插件」页面点开 **TMDB 完结状态角标**：

| 配置项 | 说明 | 默认值 |
|---|---|---|
| 启用插件 | 总开关 | 开 |
| TMDB API Key | 去 [themoviedb.org](https://www.themoviedb.org/) 账号设置 → API 免费申请 | 无，必填 |
| 新集入库后立即检测 | 关掉的话就只靠每天的定时任务 | 开 |
| 角标位置 | 左上 / 右上 / 左下 / 右下 | 右下 |
| 角标宽度占海报宽度的百分比 | 数值越大角标越大 | 26 |
| 状态变化时做完整元数据刷新 | 见下方[工作原理](#工作原理)，「变成完结」这个事件不受此开关影响，总会触发 | 关 |
| TMDB 请求间隔（毫秒） | 两次请求最小间隔，避免触发 TMDB 限流 | 300 |
| 结果缓存时长（小时） | 同一部剧这段时间内不重复请求 TMDB | 24 |
| 自定义完结 / 更新中角标图片 | 见下方[自定义角标图片](#自定义角标图片) | 未设置，用内置样式 |

保存设置后，去「计划任务」页面找到 **扫描剧集完结状态（TMDB）**，点一次
「立即运行」即可对全库做首次检测。

## 剧集状态管理页面

Emby 后台插件菜单里会多一个 **TMDB 角标 - 剧集状态管理** 入口，是一个剧名列表，
每一行可以选择：

- **跟随自动判断**（默认）
- **强制：完结**
- **强制：更新中**

选完立即生效并保存，不用点额外的保存按钮。手动设置过的剧不会再被自动扫描
悄悄改动，除非你把它改回「跟随自动判断」。

## 自定义角标图片

不想用内置的黑底红下划线样式，可以在配置页上传自己的图（PNG/WebP）：

- 勾选「完结 / 更新中角标使用自定义图片」，选择文件即可，保存后生效
- 建议图片体积控制在几百 KB 以内（浏览器端会转成 Base64 存进配置文件，太大
  会让配置文件变得很臃肿）
- 自定义图片**不区分方向**——不管角标位置选哪个角落，都是同一张图直接等比
  缩放贴上去，所以建议传不带方向性的图案（图标、印章、圆形贴纸），传斜着的
  丝带图会导致贴到别的角落时方向不对

## 工作原理

```
新集入库 / 每日定时任务
        │
        ▼
  查该剧的 TMDB Id → 请求 TMDB 详情（带限流 + 24h 缓存 + 并发合并）
        │
        ▼
  对比：TMDB 状态是否 Ended/Canceled  &&  本地入库集数 >= TMDB 常规集数
        │
        ├─ 是 → 状态 = 完结 → 强制触发 Emby 完整元数据刷新（RefreshMetadata + 替换图片）
        │
        └─ 否 → 状态 = 更新中 → 轻量通知 Emby「图片变了」（UpdateImages）
        │
        ▼
  判断结果写入内存 + 落盘（Emby 数据目录 tmdbstatusbadge/*.json）
        │
        ▼
  Emby 请求海报时，插件的 IImageEnhancer 按判断结果选对应角标图叠加上去
```

角标本身不修改原始海报文件，是 Emby 官方的 `IImageEnhancer` 扩展点，在原图基础上
实时合成一份「增强版」，原图完好无损。

## 目录结构

```
TmdbStatusBadge/
├── TmdbStatusBadge.csproj
├── Plugin.cs                          # 插件入口，注册配置页/事件监听
├── PluginConfiguration.cs             # 配置项定义
├── Api/
│   └── SeriesOverrideService.cs       # 自定义 REST 接口：查询/设置手动覆盖状态
├── Configuration/
│   ├── configPage.html / .js          # 主配置页
│   └── seriesPage.html / .js          # 剧集状态管理页
├── ScheduledTasks/
│   └── UpdateStatusTask.cs            # 每日定时全库扫描任务
├── Services/
│   └── BadgeService.cs                # 核心业务逻辑：判定状态、持久化、通知刷新
├── Tmdb/
│   ├── TmdbClient.cs                  # TMDB API 客户端（限流/缓存/并发合并）
│   └── TmdbModels.cs                  # TMDB 返回数据结构
├── ImageProvider/
│   └── BadgeImageEnhancer.cs          # 实现 IImageEnhancer，负责叠加角标
├── Assets/                             # 内置角标素材（8 张：2 状态 × 4 角落）
├── docs/
│   └── preview.png
└── lib/                                # 编译用的 Emby 参考 DLL（不提交到仓库）
```

## 常见问题

**Q: 插件装上后在插件列表里看不到 / Emby 直接不显示插件了？**

大概率是插件的构造函数依赖了某个 Emby 没法自动注入的类型，导致整个插件实例化
失败。检查有没有在构造函数里加了不常见的依赖类型（`ILibraryManager`、
`ILogManager`、`IApplicationPaths`、`IXmlSerializer`、`IFileSystem` 这些比较
稳；一些更底层的类型不一定能被自动解析）。看 Emby 日志（`.../programdata/logs/`
最新那个文件）搜插件名，通常会有具体报错。

**Q: 配置页打开是重叠、错乱的？**

检查 `configPage.html` 有没有被写成完整的 `<html><head><body>` 结构——Emby
把它当 HTML 片段直接注入页面，不是当独立网页加载，外层不能有 `html/head/body`
标签。另外内联 `<script>` 在 Emby 无刷新切页时不保证执行，建议拆成独立 `.js`
文件，用 `data-controller="__plugin/xxx"` 属性 + 对应的 `PluginPageInfo` 条目
显式加载（本项目 `configPage.js` / `seriesPage.js` 就是这么做的）。

**Q: 保存配置后，字段又变回去了 / 存不上？**

检查数字类型的配置项在前端有没有可能读到空值——空字符串 `parseInt` 出来是
`NaN`，`JSON.stringify` 序列化 `NaN` 会变成 `null`，如果服务器那边对应属性是
不可空的 `int`，反序列化整个请求体都可能失败，导致所有字段（包括正常的
字符串/布尔字段）一起保存失败。前端应该对数字输入做兜底默认值。

**Q: 升级插件版本后角标没变化 / 还是旧样式？**

`IImageEnhancer.GetConfigurationCacheKey()` 返回的 key 如果没变，Emby 会一直
用旧缓存，不会重新调用 `EnhanceImageAsync`。建议把插件版本号也编进这个 key 里，
这样每次发新版本都会自动让旧缓存失效。实在不放心，也可以手动跑一次 Emby 自带
的「清理图片缓存目录」计划任务。

## 免责声明

本插件使用了 [TMDB](https://www.themoviedb.org/) 的 API，但未获得 TMDB 官方
认证或背书（This product uses the TMDB API but is not endorsed or certified
by TMDB）。本项目与 Emby LLC 没有从属关系，是基于 Emby 公开的插件 SDK 独立
开发的第三方插件。

## License

[MIT](LICENSE)
