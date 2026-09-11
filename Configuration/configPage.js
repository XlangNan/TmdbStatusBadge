define(['loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-select'], function (loading) {
    'use strict';

    var TmdbStatusBadgePluginId = '8f2b6b2e-6c2a-4a1a-9d3a-6a1c3b7e9a10';

    // 图片体积给个软上限（约 800KB），太大的图存进 XML 配置文件会很臃肿
    var MAX_IMAGE_BYTES = 800 * 1024;

    function safeInt(value, fallback) {
        var n = parseInt(value, 10);
        return isNaN(n) ? fallback : n;
    }

    function showError(prefix, err) {
        loading.hide();
        var msg = (err && err.message) ? err.message : '请求出错，请截图这段提示发给开发者';
        Dashboard.alert(prefix + msg);
    }

    // 把一个 <input type="file"> 选中的图片读成 Base64（去掉开头 data:image/...;base64, 前缀，
    // 只留纯数据部分，方便服务器直接当图片字节存）
    function readFileAsBase64(file) {
        return new Promise(function (resolve, reject) {
            if (!file) {
                resolve(null);
                return;
            }
            if (file.size > MAX_IMAGE_BYTES) {
                reject(new Error('图片体积超过 800KB，换一张小一点的图（或者用图片压缩工具处理一下）'));
                return;
            }
            var reader = new FileReader();
            reader.onload = function () {
                var result = reader.result || '';
                var commaIndex = result.indexOf(',');
                resolve(commaIndex >= 0 ? result.substring(commaIndex + 1) : result);
            };
            reader.onerror = function () {
                reject(new Error('读取图片文件失败'));
            };
            reader.readAsDataURL(file);
        });
    }

    function loadConfig(view, state) {
        loading.show();
        ApiClient.getPluginConfiguration(TmdbStatusBadgePluginId).then(function (config) {
            view.querySelector('#Enabled').checked = !!config.Enabled;
            view.querySelector('#TmdbApiKey').value = config.TmdbApiKey || '';
            view.querySelector('#EnableEventTrigger').checked = !!config.EnableEventTrigger;
            view.querySelector('#FullMetadataRefreshOnChange').checked = !!config.FullMetadataRefreshOnChange;
            view.querySelector('#BadgePosition').value = config.BadgePosition;
            view.querySelector('#BadgeScalePercent').value = config.BadgeScalePercent;
            view.querySelector('#TmdbRequestIntervalMs').value = config.TmdbRequestIntervalMs;
            view.querySelector('#CacheHours').value = config.CacheHours;

            view.querySelector('#UseCustomEndedBadge').checked = !!config.UseCustomEndedBadge;
            view.querySelector('#UseCustomOngoingBadge').checked = !!config.UseCustomOngoingBadge;

            // 每次重新加载页面，清空"本次会话待上传/待清除"的临时状态，
            // 这些只应该在用户这次停留页面期间、真的选了新文件才生效。
            state.pendingEndedBase64 = null;
            state.pendingOngoingBase64 = null;
            state.clearEnded = false;
            state.clearOngoing = false;

            view.querySelector('#CustomEndedBadgeStatus').textContent =
                config.CustomEndedBadgeBase64 ? '已上传自定义图片（重新选择文件可替换，或点右边按钮清除）' : '尚未上传，当前用内置样式';
            view.querySelector('#CustomOngoingBadgeStatus').textContent =
                config.CustomOngoingBadgeBase64 ? '已上传自定义图片（重新选择文件可替换，或点右边按钮清除）' : '尚未上传，当前用内置样式';

            loading.hide();
        }).catch(function (err) {
            showError('加载配置失败：', err);
        });
    }

    function saveConfig(view, state) {
        loading.show();
        ApiClient.getPluginConfiguration(TmdbStatusBadgePluginId).then(function (config) {
            config.Enabled = view.querySelector('#Enabled').checked;
            config.TmdbApiKey = view.querySelector('#TmdbApiKey').value || '';
            config.EnableEventTrigger = view.querySelector('#EnableEventTrigger').checked;
            config.FullMetadataRefreshOnChange = view.querySelector('#FullMetadataRefreshOnChange').checked;
            // 数字输入框如果是空的，parseInt 会得到 NaN，NaN 传给服务器可能导致
            // 整个保存请求失败，这里兜底一个默认值，避免拖累其它字段存不上。
            config.BadgePosition = safeInt(view.querySelector('#BadgePosition').value, 3);
            config.BadgeScalePercent = safeInt(view.querySelector('#BadgeScalePercent').value, 26);
            config.TmdbRequestIntervalMs = safeInt(view.querySelector('#TmdbRequestIntervalMs').value, 300);
            config.CacheHours = safeInt(view.querySelector('#CacheHours').value, 24);

            config.UseCustomEndedBadge = view.querySelector('#UseCustomEndedBadge').checked;
            config.UseCustomOngoingBadge = view.querySelector('#UseCustomOngoingBadge').checked;

            if (state.clearEnded) {
                config.CustomEndedBadgeBase64 = null;
            } else if (state.pendingEndedBase64) {
                config.CustomEndedBadgeBase64 = state.pendingEndedBase64;
            }
            // 否则（既没清除也没重新选文件）保留 config 里已有的旧值不动

            if (state.clearOngoing) {
                config.CustomOngoingBadgeBase64 = null;
            } else if (state.pendingOngoingBase64) {
                config.CustomOngoingBadgeBase64 = state.pendingOngoingBase64;
            }

            ApiClient.updatePluginConfiguration(TmdbStatusBadgePluginId, config).then(function (result) {
                loading.hide();
                Dashboard.processPluginConfigurationUpdateResult(result);
            }).catch(function (err) {
                showError('保存失败：', err);
            });
        }).catch(function (err) {
            showError('读取当前配置失败：', err);
        });
    }

    // Emby 会调用这个默认导出的函数来初始化页面：
    // view 就是当前这一次显示出来的、id="TmdbStatusBadgeConfigPage" 的那个具体 DOM 节点。
    // 这是官方文档认可的写法，专门用来替代"直接写在 HTML 里的内联 <script>"——
    // 因为 Emby 用 AJAX 无刷新切换页面时，内联 <script> 不保证会被执行，
    // 而通过 data-controller 显式加载的 JS 文件则是可靠的。
    return function (view) {
        // 每个 view 实例自己的一份"本次会话待保存的文件"状态，不同 view 实例互不影响
        var state = {
            pendingEndedBase64: null,
            pendingOngoingBase64: null,
            clearEnded: false,
            clearOngoing: false
        };

        view.addEventListener('viewshow', function () {
            loadConfig(view, state);
        });

        view.querySelector('#CustomEndedBadgeFile').addEventListener('change', function (e) {
            var file = e.target.files && e.target.files[0];
            readFileAsBase64(file).then(function (base64) {
                if (!base64) {
                    return;
                }
                state.pendingEndedBase64 = base64;
                state.clearEnded = false;
                view.querySelector('#CustomEndedBadgeStatus').textContent =
                    '已选择新图片「' + file.name + '」，点下面"保存"才会真正生效';
            }).catch(function (err) {
                Dashboard.alert(err.message);
                e.target.value = '';
            });
        });

        view.querySelector('#CustomOngoingBadgeFile').addEventListener('change', function (e) {
            var file = e.target.files && e.target.files[0];
            readFileAsBase64(file).then(function (base64) {
                if (!base64) {
                    return;
                }
                state.pendingOngoingBase64 = base64;
                state.clearOngoing = false;
                view.querySelector('#CustomOngoingBadgeStatus').textContent =
                    '已选择新图片「' + file.name + '」，点下面"保存"才会真正生效';
            }).catch(function (err) {
                Dashboard.alert(err.message);
                e.target.value = '';
            });
        });

        view.querySelector('#ClearCustomEndedBadge').addEventListener('click', function () {
            state.clearEnded = true;
            state.pendingEndedBase64 = null;
            view.querySelector('#CustomEndedBadgeFile').value = '';
            view.querySelector('#UseCustomEndedBadge').checked = false;
            view.querySelector('#CustomEndedBadgeStatus').textContent = '将在保存后清除，改用内置样式';
        });

        view.querySelector('#ClearCustomOngoingBadge').addEventListener('click', function () {
            state.clearOngoing = true;
            state.pendingOngoingBase64 = null;
            view.querySelector('#CustomOngoingBadgeFile').value = '';
            view.querySelector('#UseCustomOngoingBadge').checked = false;
            view.querySelector('#CustomOngoingBadgeStatus').textContent = '将在保存后清除，改用内置样式';
        });

        view.querySelector('#TmdbStatusBadgeConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            saveConfig(view, state);
            return false;
        });
    };
});
