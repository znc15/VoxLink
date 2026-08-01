// VoxLink site interactivity (tiny, no deps)
(function () {
  "use strict";

  // mobile nav toggle
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

  // current year in footer
  var y = document.getElementById("year");
  if (y) y.textContent = new Date().getFullYear();

  // subtle reveal on scroll
  var revealEls = document.querySelectorAll(".feature-card,.shot,.pipe-step,.steps li,.stack-card");
  if ("IntersectionObserver" in window && revealEls.length) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting) {
          e.target.style.transition = "opacity .6s ease, transform .6s ease";
          e.target.style.opacity = 1;
          e.target.style.transform = "translateY(0)";
          io.unobserve(e.target);
        }
      });
    }, { threshold: 0.12 });
    revealEls.forEach(function (el) {
      el.style.opacity = 0;
      el.style.transform = "translateY(14px)";
      io.observe(el);
    });
  }

  // nav background tint on scroll
  var nav = document.querySelector(".nav");
  if (nav) {
    var onScroll = function () {
      nav.style.boxShadow = window.scrollY > 8 ? "0 6px 24px -16px rgba(0,0,0,.8)" : "none";
    };
    onScroll();
    window.addEventListener("scroll", onScroll, { passive: true });
  }
})();
