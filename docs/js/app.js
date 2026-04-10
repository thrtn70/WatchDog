/* ═══════════════════════════════════════════════════════════
   WatchDog — Landing Page
   - GitHub API release fetch
   - FAQ accordion (grid-template-rows)
   - Scroll-triggered reveals (IntersectionObserver)
   ═══════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ── URL Validation ───────────────────────────────────────

  var ALLOWED_HOSTS = ['github.com', 'objects.githubusercontent.com'];

  function isSafeDownloadUrl(url) {
    try {
      var parsed = new URL(url);
      return parsed.protocol === 'https:' &&
        ALLOWED_HOSTS.indexOf(parsed.hostname) !== -1;
    } catch (e) {
      return false;
    }
  }

  // ── GitHub Release Fetch ─────────────────────────────────

  var RELEASES_URL = 'https://api.github.com/repos/thrtn70/WatchDog/releases/latest';
  var SETUP_SUFFIX = '-Setup.exe';
  var ZIP_SUFFIX = '-win-x64.zip';

  function fetchLatestRelease() {
    fetch(RELEASES_URL, {
      headers: { 'Accept': 'application/vnd.github+json' },
    })
      .then(function (res) {
        if (!res.ok) {
          // Consume the response body to free the connection
          return res.text().then(function () { return null; });
        }
        return res.json();
      })
      .then(function (release) {
        if (!release) return;

        var version = (release.tag_name || '').replace(/^v/, '');
        if (!version) return;

        var setupAsset = null;
        var zipAsset = null;

        (release.assets || []).forEach(function (asset) {
          if (asset.name && asset.name.endsWith(SETUP_SUFFIX)) setupAsset = asset;
          if (asset.name && asset.name.endsWith(ZIP_SUFFIX)) zipAsset = asset;
        });

        // Download button — validate URL before assigning
        var btn = document.getElementById('download-btn');
        var btnText = document.getElementById('download-text');
        if (btn && btnText) {
          btnText.textContent = 'Download v' + version;
          if (setupAsset && isSafeDownloadUrl(setupAsset.browser_download_url)) {
            btn.href = setupAsset.browser_download_url;
          }
        }

        // File size
        if (setupAsset && setupAsset.size) {
          var sizeMb = (setupAsset.size / (1024 * 1024)).toFixed(0);
          var sizeEl = document.getElementById('download-size');
          var dotEl = document.getElementById('size-dot');
          if (sizeEl) sizeEl.textContent = '~' + sizeMb + ' MB';
          if (dotEl) dotEl.style.display = '';
        }

        // Portable links — validate URL before assigning
        if (zipAsset && isSafeDownloadUrl(zipAsset.browser_download_url)) {
          ['portable-link', 'portable-link-2'].forEach(function (id) {
            var el = document.getElementById(id);
            if (el) el.href = zipAsset.browser_download_url;
          });
        }
      })
      .catch(function () {
        // Silently fall back — generic release link already set in HTML
      });
  }

  // ── FAQ Accordion ────────────────────────────────────────

  function initFaq() {
    var items = document.querySelectorAll('.faq-item');

    items.forEach(function (item) {
      var btn = item.querySelector('.faq-q');
      if (!btn) return;

      btn.addEventListener('click', function () {
        var wasOpen = item.classList.contains('open');

        // Close all and reset ARIA
        items.forEach(function (other) {
          other.classList.remove('open');
          var otherBtn = other.querySelector('.faq-q');
          if (otherBtn) otherBtn.setAttribute('aria-expanded', 'false');
        });

        // Toggle clicked
        if (!wasOpen) {
          item.classList.add('open');
          btn.setAttribute('aria-expanded', 'true');
        }
      });
    });
  }

  // ── Scroll Reveals ───────────────────────────────────────

  function initReveals() {
    var els = document.querySelectorAll('.reveal');
    if (!els.length) return;

    // Respect reduced motion or missing IntersectionObserver
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches ||
        !('IntersectionObserver' in window)) {
      els.forEach(function (el) { el.classList.add('visible'); });
      return;
    }

    var observer = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add('visible');
            observer.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.15, rootMargin: '0px 0px -40px 0px' }
    );

    els.forEach(function (el) { observer.observe(el); });
  }

  // ── Copy Buttons on Pre Blocks ────────────────────────────

  function initCopyButtons() {
    var blocks = document.querySelectorAll('.setup-card pre');

    blocks.forEach(function (pre) {
      var btn = document.createElement('button');
      btn.className = 'copy-btn';
      btn.setAttribute('aria-label', 'Copy to clipboard');
      btn.textContent = 'Copy';

      btn.addEventListener('click', function () {
        var text = pre.textContent;
        navigator.clipboard.writeText(text).then(function () {
          btn.textContent = 'Copied';
          btn.classList.add('copied');
          setTimeout(function () {
            btn.textContent = 'Copy';
            btn.classList.remove('copied');
          }, 2000);
        }).catch(function () {
          // Fallback: select the text
          var range = document.createRange();
          range.selectNodeContents(pre);
          var sel = window.getSelection();
          sel.removeAllRanges();
          sel.addRange(range);
        });
      });

      // Wrap pre in a container for positioning
      var wrapper = document.createElement('div');
      wrapper.className = 'pre-wrapper';
      pre.parentNode.insertBefore(wrapper, pre);
      wrapper.appendChild(pre);
      wrapper.appendChild(btn);
    });
  }

  // ── Sticky Nav ────────────────────────────────────────────

  function initNav() {
    var nav = document.querySelector('.site-nav');
    if (!nav) return;

    // Toggle background on scroll
    var scrollThreshold = 40;
    var onScroll = function () {
      nav.classList.toggle('scrolled', window.scrollY > scrollThreshold);
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    onScroll();

    // Smooth scroll for anchor links
    nav.addEventListener('click', function (e) {
      var link = e.target.closest('a[href^="#"]');
      if (!link) return;

      var targetId = link.getAttribute('href').slice(1);
      var target = targetId ? document.getElementById(targetId) : null;

      if (target) {
        e.preventDefault();
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      } else if (link.getAttribute('href') === '#') {
        e.preventDefault();
        window.scrollTo({ top: 0, behavior: 'smooth' });
      }
    });
  }

  // ── Init ─────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    fetchLatestRelease();
    initFaq();
    initReveals();
    initNav();
    initCopyButtons();
  });
})();
