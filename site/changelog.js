// VoxLink Changelog — 更新日志页
// 从 GitHub Releases 拉取全部版本，渲染为与首页风格一致的时间线卡片

(function () {
  "use strict";

  var REPO = "znc15/VoxLink";
  var listEl = document.getElementById("changelogList");
  var statusEl = document.getElementById("changelogStatus");

  function formatDate(iso) {
    var d = new Date(iso);
    if (isNaN(d.getTime())) return "";
    return d.getFullYear() + " 年 " + (d.getMonth() + 1) + " 月 " + d.getDate() + " 日";
  }

  // marked 可用则渲染 Markdown，否则回退纯文本
  function renderBody(container, md) {
    var text = md || "本次更新无详细说明。";
    if (window.marked && typeof window.marked.parse === "function") {
      container.innerHTML = window.marked.parse(text, { breaks: true });
    } else {
      var p = document.createElement("p");
      p.textContent = text;
      container.appendChild(p);
    }
  }

  function buildItem(release, index) {
    var tag = release.tag_name || release.name || "未知版本";

    var item = document.createElement("article");
    item.className = "card changelog-item reveal";

    var head = document.createElement("div");
    head.className = "changelog-head";

    var version = document.createElement("h3");
    version.className = "changelog-version";
    version.textContent = tag;
    head.appendChild(version);

    if (index === 0) {
      var latest = document.createElement("span");
      latest.className = "changelog-latest";
      latest.textContent = "最新";
      head.appendChild(latest);
    }

    var date = document.createElement("span");
    date.className = "changelog-date";
    date.textContent = formatDate(release.published_at);
    head.appendChild(date);

    if (release.html_url) {
      var link = document.createElement("a");
      link.className = "changelog-github";
      link.href = release.html_url;
      link.target = "_blank";
      link.rel = "noopener";
      link.textContent = "GitHub 查看";
      head.appendChild(link);
    }

    var body = document.createElement("div");
    body.className = "changelog-body";
    renderBody(body, release.body);

    item.appendChild(head);
    item.appendChild(body);
    return item;
  }

  fetch("https://api.github.com/repos/" + REPO + "/releases?per_page=100", {
    headers: { Accept: "application/vnd.github+json" }
  })
    .then(function (res) {
      if (!res.ok) throw new Error("HTTP " + res.status);
      return res.json();
    })
    .then(function (releases) {
      if (!Array.isArray(releases) || releases.length === 0) {
        statusEl.textContent = "暂无更新日志。";
        return;
      }
      statusEl.remove();
      var items = releases.map(buildItem);
      items.forEach(function (el) { listEl.appendChild(el); });
      if (window.voxlinkObserveReveal) window.voxlinkObserveReveal(items);
    })
    .catch(function () {
      statusEl.innerHTML = "";
      var p = document.createElement("p");
      p.textContent = "更新日志加载失败，请稍后再试。";
      statusEl.appendChild(p);
      var a = document.createElement("a");
      a.className = "changelog-status-link";
      a.href = "https://github.com/" + REPO + "/releases";
      a.target = "_blank";
      a.rel = "noopener";
      a.textContent = "前往 GitHub Releases 查看";
      statusEl.appendChild(a);
    });
})();
