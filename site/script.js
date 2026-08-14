// VoxLink Landing — interactions
// 1) 主题切换（深色 / 亮色，localStorage 记忆）
// 2) GitHub API 获取最新 Release（版本徽章；完整更新日志见 changelog.html）
// 3) 滚动 reveal（IntersectionObserver，供 changelog.js 复用）
// 4) Header 滚动描边 / 移动端导航

(function () {
  "use strict";

  /* ================= 主题切换 ================= */
  var THEME_KEY = "voxlink-theme";
  var root = document.documentElement;
  var themeToggle = document.getElementById("themeToggle");

  function currentTheme() {
    return root.getAttribute("data-theme") === "light" ? "light" : "dark";
  }

  themeToggle.addEventListener("click", function () {
    var next = currentTheme() === "dark" ? "light" : "dark";
    root.setAttribute("data-theme", next);
    try { localStorage.setItem(THEME_KEY, next); } catch (e) {}
  });

  // 系统主题变化时，若用户未手动选择过，则跟随系统
  window.matchMedia("(prefers-color-scheme: light)").addEventListener("change", function (e) {
    var stored = null;
    try { stored = localStorage.getItem(THEME_KEY); } catch (err) {}
    if (stored !== "light" && stored !== "dark") {
      root.setAttribute("data-theme", e.matches ? "light" : "dark");
    }
  });

  /* ================= GitHub 最新 Release ================= */
  var REPO = "znc15/VoxLink";
  var badgeEl = document.getElementById("releaseBadge");

  function fetchJson(url) {
    return fetch(url, { headers: { Accept: "application/vnd.github+json" } }).then(function (res) {
      if (!res.ok) throw new Error("HTTP " + res.status);
      return res.json();
    });
  }

  // 首页徽章：最新版本号 + MIT（更新日志在 changelog.html）
  if (badgeEl) {
    // 优先 /releases/latest；404 或被限流时回退到 /releases 列表第一条
    fetchJson("https://api.github.com/repos/" + REPO + "/releases/latest")
      .catch(function () {
        return fetchJson("https://api.github.com/repos/" + REPO + "/releases?per_page=1").then(function (list) {
          if (!list || !list.length) throw new Error("no releases");
          return list[0];
        });
      })
      .then(function (data) {
        var tag = data.tag_name || data.name || "";
        if (tag) badgeEl.textContent = tag + " 已发布 · MIT 开源";
      })
      .catch(function () {
        // 拉取失败：保留静态默认文案
      });
  }

  /* ================= 滚动 reveal ================= */
  var revealEls = document.querySelectorAll(".reveal");

  revealEls.forEach(function (el) {
    var delay = el.getAttribute("data-delay");
    if (delay) el.style.setProperty("--reveal-delay", delay + "ms");
  });

  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // 暴露给 changelog.js：动态插入的卡片同样走 reveal 动画
  function observeReveal(els) {
    var observer = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add("visible");
            observer.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -40px 0px" }
    );
    els.forEach(function (el) { observer.observe(el); });
  }

  if (reduceMotion || !("IntersectionObserver" in window)) {
    revealEls.forEach(function (el) { el.classList.add("visible"); });
    window.voxlinkObserveReveal = function (els) {
      els.forEach(function (el) { el.classList.add("visible"); });
    };
  } else {
    observeReveal(revealEls);
    window.voxlinkObserveReveal = observeReveal;
  }

  /* ================= Header 滚动描边 ================= */
  var header = document.getElementById("siteHeader");
  var onScroll = function () {
    header.classList.toggle("scrolled", window.scrollY > 8);
  };
  window.addEventListener("scroll", onScroll, { passive: true });
  onScroll();

  /* ================= 移动端导航 ================= */
  var toggle = document.getElementById("navToggle");
  var mobileNav = document.getElementById("navMobile");

  toggle.addEventListener("click", function () {
    var open = mobileNav.classList.toggle("open");
    toggle.setAttribute("aria-expanded", String(open));
    toggle.setAttribute("aria-label", open ? "关闭菜单" : "打开菜单");
  });

  mobileNav.querySelectorAll("a").forEach(function (link) {
    link.addEventListener("click", function () {
      mobileNav.classList.remove("open");
      toggle.setAttribute("aria-expanded", "false");
    });
  });
})();
