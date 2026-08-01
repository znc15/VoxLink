// VoxLink site — 滚动错落淡入、管线进度光带、导航、年份
(function () {
  "use strict";

  // 移动端导航
  var toggle = document.querySelector(".nav-toggle");
  var links = document.querySelector(".nav-links");
  if (toggle && links) {
    toggle.addEventListener("click", function () {
      var open = links.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
    links.querySelectorAll("a").forEach(function (a) {
      a.addEventListener("click", function () {
        links.classList.remove("open");
        toggle.setAttribute("aria-expanded", "false");
      });
    });
  }

  // 页脚年份
  var y = document.getElementById("year");
  if (y) y.textContent = new Date().getFullYear();

  // 滚动错落淡入：所有 .reveal 进入视口即加 .in
  var revealEls = document.querySelectorAll(".reveal");
  if ("IntersectionObserver" in window && revealEls.length) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          e.target.classList.add("in");
          io.unobserve(e.target);
        }
      });
    }, { threshold: 0.15, rootMargin: "0px 0px -8% 0px" });
    revealEls.forEach(function (el) { io.observe(el); });
  } else {
    revealEls.forEach(function (el) { el.classList.add("in"); });
  }

  // 管线步骤进度光带：视口出现时加 .in（CSS 里光带 width:0 → 100%）
  var pipeSteps = document.querySelectorAll(".pipe-step");
  if ("IntersectionObserver" in window && pipeSteps.length) {
    var po = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          e.target.classList.add("in");
          po.unobserve(e.target);
        }
      });
    }, { threshold: 0.4 });
    pipeSteps.forEach(function (el) { po.observe(el); });
  } else {
    pipeSteps.forEach(function (el) { el.classList.add("in"); });
  }

  // 导航栏滚动阴影
  var nav = document.querySelector(".nav");
  if (nav) {
    var onScroll = function () {
      nav.style.boxShadow = window.scrollY > 8 ? "0 4px 20px -18px rgba(0,0,0,.5)" : "none";
    };
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
  }
})();

