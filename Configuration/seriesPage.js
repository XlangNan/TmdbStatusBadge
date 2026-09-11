define(['loading', 'emby-input', 'emby-button', 'emby-select'], function (loading) {
    'use strict';

    var STATUS_LABEL = {
        Unknown: '尚未判断',
        Ended: '完结',
        Ongoing: '更新中'
    };

    function showError(prefix, err) {
        loading.hide();
        var msg = (err && err.message) ? err.message : '请求出错，请截图这段提示发给开发者';
        Dashboard.alert(prefix + msg);
    }

    function fetchSeriesList() {
        var url = ApiClient.getUrl('TmdbStatusBadge/Series');
        return ApiClient.getJSON(url);
    }

    function postOverride(seriesId, status) {
        var url = ApiClient.getUrl('TmdbStatusBadge/Override');
        return ApiClient.ajax({
            type: 'POST',
            url: url,
            data: JSON.stringify({ SeriesId: seriesId, Status: status }),
            contentType: 'application/json'
        });
    }

    function buildRow(item) {
        var tr = document.createElement('tr');

        var nameTd = document.createElement('td');
        nameTd.textContent = item.Name;
        nameTd.setAttribute('data-series-name', item.Name.toLowerCase());
        tr.appendChild(nameTd);

        var autoTd = document.createElement('td');
        autoTd.textContent = STATUS_LABEL[item.AutoStatus] || item.AutoStatus;
        tr.appendChild(autoTd);

        var overrideTd = document.createElement('td');
        var select = document.createElement('select');
        select.setAttribute('is', 'emby-select');
        select.innerHTML =
            '<option value="Auto">跟随自动判断</option>' +
            '<option value="Ended">强制：完结</option>' +
            '<option value="Ongoing">强制：更新中</option>';
        select.value = item.Override || 'Auto';

        var feedback = document.createElement('span');
        feedback.className = 'fieldDescription';
        feedback.style.marginLeft = '0.8em';

        select.addEventListener('change', function () {
            feedback.textContent = '保存中…';
            postOverride(item.SeriesId, select.value).then(function () {
                feedback.textContent = '已保存';
                setTimeout(function () {
                    feedback.textContent = '';
                }, 2000);
            }).catch(function (err) {
                feedback.textContent = '';
                showError('保存失败：', err);
            });
        });

        overrideTd.appendChild(select);
        overrideTd.appendChild(feedback);
        tr.appendChild(overrideTd);

        return tr;
    }

    function loadList(view) {
        loading.show();
        view.querySelector('#SeriesListLoading').style.display = '';
        view.querySelector('#SeriesListEmpty').style.display = 'none';
        view.querySelector('#SeriesTable').style.display = 'none';

        fetchSeriesList().then(function (list) {
            loading.hide();
            view.querySelector('#SeriesListLoading').style.display = 'none';

            if (!list || list.length === 0) {
                view.querySelector('#SeriesListEmpty').style.display = '';
                return;
            }

            var tbody = view.querySelector('#SeriesTableBody');
            tbody.innerHTML = '';
            list.forEach(function (item) {
                tbody.appendChild(buildRow(item));
            });

            view.querySelector('#SeriesTable').style.display = '';
        }).catch(function (err) {
            view.querySelector('#SeriesListLoading').style.display = 'none';
            showError('加载剧集列表失败：', err);
        });
    }

    return function (view) {
        view.addEventListener('viewshow', function () {
            loadList(view);
        });

        view.querySelector('#SeriesSearchBox').addEventListener('input', function (e) {
            var keyword = e.target.value.trim().toLowerCase();
            var rows = view.querySelectorAll('#SeriesTableBody tr');
            rows.forEach(function (row) {
                var nameCell = row.querySelector('[data-series-name]');
                var match = !keyword || (nameCell && nameCell.getAttribute('data-series-name').indexOf(keyword) >= 0);
                row.style.display = match ? '' : 'none';
            });
        });
    };
});
