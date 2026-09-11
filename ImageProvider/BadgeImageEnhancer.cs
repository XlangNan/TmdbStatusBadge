using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using SkiaSharp;
using TmdbStatusBadge.Services;

namespace TmdbStatusBadge.ImageProvider
{
    /// <summary>
    /// Emby 官方设计用来"在原图基础上叠加角标"的扩展点。
    /// Emby 请求某个图片时，会依次调用已注册的 IImageEnhancer：
    /// Supports() 判断要不要处理 -> GetConfigurationCacheKey() 决定缓存是否需要重新生成
    /// -> EnhanceImageAsync() 真正做图像合成，输出到 outputFile，Emby 自己管理生成后的缓存文件，
    /// 不会污染 outputFile 对应的原始图片文件。
    /// </summary>
    public class BadgeImageEnhancer : IImageEnhancer
    {
        // 解码一次，常驻内存复用，避免每次叠加都重新解码 PNG
        private static readonly ConcurrentDictionary<string, SKBitmap?> _badgeBitmapCache = new();

        // 越靠前优先级越高；这里没有强依赖，用 First 即可
        public MetadataProviderPriority Priority => MetadataProviderPriority.First;

        public bool Supports(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            if (plugin == null || !plugin.Configuration.Enabled)
            {
                return false;
            }

            // 只处理剧集(Series)的主封面(Primary)
            return item is Series && imageType == ImageType.Primary;
        }

        public string GetConfigurationCacheKey(BaseItem item, ImageType imageType)
        {
            var plugin = Plugin.Instance;
            var status = plugin?.BadgeService.GetStatus(item.Id) ?? BadgeStatus.Unknown;
            var config = plugin?.Configuration;

            var position = config?.BadgePosition ?? 3;
            var scale = config?.BadgeScalePercent ?? 26;

            // 插件版本号也编进 key 里：每次发新版本（哪怕只是换了内置素材图，
            // 代码逻辑完全没变），Emby 也会认为缓存失效、重新调用 EnhanceImageAsync
            // 生成新图，不会一直沿用旧版本缓存下来的老图。
            var pluginVersion = plugin?.Version?.ToString() ?? "0";

            var customPart = string.Empty;
            if (status == BadgeStatus.Ended && (config?.UseCustomEndedBadge ?? false))
            {
                customPart = "_custom_" + ComputeShortHash(config?.CustomEndedBadgeBase64 ?? string.Empty);
            }
            else if (status == BadgeStatus.Ongoing && (config?.UseCustomOngoingBadge ?? false))
            {
                customPart = "_custom_" + ComputeShortHash(config?.CustomOngoingBadgeBase64 ?? string.Empty);
            }

            return $"tmdbstatusbadge_{status}_{position}_{scale}_{pluginVersion}{customPart}";
        }

        public ImageSize GetEnhancedImageSize(BaseItem item, ImageType imageType, int imageIndex, ImageSize originalImageSize)
        {
            // 角标只是叠加，不改变整体图片尺寸
            return originalImageSize;
        }

        public EnhancedImageInfo GetEnhancedImageInfo(BaseItem item, string inputFile, ImageType imageType, int imageIndex)
        {
            return new EnhancedImageInfo
            {
                RequiresTransparency = true
            };
        }

        public Task EnhanceImageAsync(BaseItem item, string inputFile, string outputFile, ImageType imageType, int imageIndex)
        {
            var plugin = Plugin.Instance;
            var status = plugin?.BadgeService.GetStatus(item.Id) ?? BadgeStatus.Unknown;

            if (status == BadgeStatus.Unknown)
            {
                // 还没检测出结果，原样复制，不加角标
                File.Copy(inputFile, outputFile, overwrite: true);
                return Task.CompletedTask;
            }

            var position = plugin?.Configuration.BadgePosition ?? 3;

            var badgeBitmap = GetBadgeBitmap(status, position);
            if (badgeBitmap == null)
            {
                // 素材加载失败，保底：不影响海报正常显示
                File.Copy(inputFile, outputFile, overwrite: true);
                return Task.CompletedTask;
            }

            using var srcBitmap = SKBitmap.Decode(inputFile);
            if (srcBitmap == null)
            {
                File.Copy(inputFile, outputFile, overwrite: true);
                return Task.CompletedTask;
            }

            var scalePercent = plugin?.Configuration.BadgeScalePercent ?? 26;

            using var surface = SKSurface.Create(new SKImageInfo(srcBitmap.Width, srcBitmap.Height));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(srcBitmap, 0, 0);

            DrawBadgeImage(canvas, srcBitmap.Width, srcBitmap.Height, badgeBitmap, position, scalePercent);

            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(outputFile);
            data.SaveTo(stream);

            return Task.CompletedTask;
        }

        /// <summary>
        /// 把角标素材按海报宽度等比缩放后，贴到对应角落。
        /// 不管是内置的丝带素材、还是用户自定义上传的图片，都走这同一套贴图逻辑——
        /// 区别只在于 GetBadgeBitmap 给的是哪张图。
        /// </summary>
        private void DrawBadgeImage(SKCanvas canvas, int posterWidth, int posterHeight, SKBitmap badge, int position, int scalePercent)
        {
            var targetWidth = posterWidth * (scalePercent / 100f);
            var scale = targetWidth / badge.Width;
            var targetHeight = badge.Height * scale;

            float left, top;
            switch (position)
            {
                case 0: // 左上
                    left = 0; top = 0;
                    break;
                case 2: // 左下
                    left = 0; top = posterHeight - targetHeight;
                    break;
                case 3: // 右下
                    left = posterWidth - targetWidth; top = posterHeight - targetHeight;
                    break;
                default: // 右上
                    left = posterWidth - targetWidth; top = 0;
                    break;
            }

            var destRect = new SKRect(left, top, left + targetWidth, top + targetHeight);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(badge, destRect, paint);
        }

        /// <summary>
        /// 优先用用户自定义上传的图片（如果开启了并且确实传了）；
        /// 否则用内置的丝带素材——内置素材是按四个角落各画一张、方向已经画对的，
        /// 不做运行时镜像（镜像会把图里的文字一起变成镜像/倒着的）。
        /// 自定义图片则是用户自己的图，不做方向区分，四个角落都用同一张原图直接贴。
        /// </summary>
        private static SKBitmap? GetBadgeBitmap(BadgeStatus status, int position)
        {
            var plugin = Plugin.Instance;
            var config = plugin?.Configuration;

            var useCustom = status == BadgeStatus.Ended
                ? (config?.UseCustomEndedBadge ?? false)
                : (config?.UseCustomOngoingBadge ?? false);
            var customBase64 = status == BadgeStatus.Ended
                ? config?.CustomEndedBadgeBase64
                : config?.CustomOngoingBadgeBase64;

            if (useCustom && !string.IsNullOrWhiteSpace(customBase64))
            {
                var cacheKey = "custom_" + status + "_" + ComputeShortHash(customBase64);
                return _badgeBitmapCache.GetOrAdd(cacheKey, _ =>
                {
                    try
                    {
                        var bytes = Convert.FromBase64String(customBase64);
                        return SKBitmap.Decode(bytes);
                    }
                    catch
                    {
                        // 自定义图片数据损坏/解码失败时返回 null，
                        // 调用方会保底原样输出海报，不会导致整个插件崩掉
                        return null;
                    }
                });
            }

            var statusSlug = status == BadgeStatus.Ended ? "ended" : "ongoing";
            var cornerSlug = position switch
            {
                0 => "top_left",
                2 => "bottom_left",
                3 => "bottom_right",
                _ => "top_right"
            };
            var resourceName = $"TmdbStatusBadge.Assets.badge_{statusSlug}_{cornerSlug}.png";

            return _badgeBitmapCache.GetOrAdd(resourceName, name =>
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(name);
                if (stream == null)
                {
                    return null;
                }

                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return SKBitmap.Decode(ms.ToArray());
            });
        }

        private static string ComputeShortHash(string input)
        {
            using var md5 = MD5.Create();
            var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder();
            for (var i = 0; i < 6; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
