/* ═══════════════════════════════════════════════════════════
   WatchDog Landing Page — JavaScript
   - Fetches latest release from GitHub API
   - FAQ accordion behavior
   ═══════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ── GitHub Release Fetch ─────────────────────────────────

  const RELEASES_URL = 'https://api.github.com/repos/thrtn70/WatchDog/releases/latest';
  const SETUP_SUFFIX = '-Setup.exe';
  const ZIP_SUFFIX = '-win-x64.zip';
  const FALLBACK_URL = 'https://github.com/thrtn70/WatchDog/releases/latest';

  async function fetchLatestRelease() {
    try {
      const response = await fetch(RELEASES_URL, {
        headers: { 'Accept': 'application/vnd.github+json' },
      });

      if (!response.ok) return;

      const release = await response.json();
      const version = (release.tag_name || '').replace(/^v/, '');
      if (!version) return;

      let setupAsset = null;
      let zipAsset = null;

      for (const asset of release.assets || []) {
        if (asset.name && asset.name.endsWith(SETUP_SUFFIX)) {
          setupAsset = asset;
        }
        if (asset.name && asset.name.endsWith(ZIP_SUFFIX)) {
          zipAsset = asset;
        }
      }

      // Update download button
      const btn = document.getElementById('download-btn');
      const btnText = document.getElementById('download-text');
      if (btn && btnText) {
        btnText.textContent = 'Download v' + version;
        if (setupAsset) {
          btn.href = setupAsset.browser_download_url;
        }
      }

      // Update file size
      if (setupAsset && setupAsset.size) {
        const sizeMb = (setupAsset.size / (1024 * 1024)).toFixed(0);
        const sizeEl = document.getElementById('download-size');
        const sepEl = document.getElementById('size-sep');
        if (sizeEl) sizeEl.textContent = '~' + sizeMb + ' MB';
        if (sepEl) sepEl.style.display = '';
      }

      // Update portable ZIP links
      if (zipAsset) {
        var portableLinks = [
          document.getElementById('portable-link'),
          document.getElementById('portable-link-2'),
        ];
        for (const link of portableLinks) {
          if (link) link.href = zipAsset.browser_download_url;
        }
      }
    } catch (_) {
      // Silently fall back to generic release link — already set in HTML
    }
  }

  // ── FAQ Accordion ────────────────────────────────────────

  function initFaqAccordion() {
    var items = document.querySelectorAll('.faq-item');

    items.forEach(function (item) {
      var question = item.querySelector('.faq-question');
      if (!question) return;

      question.addEventListener('click', function () {
        var isOpen = item.classList.contains('open');

        // Close all items
        items.forEach(function (other) {
          other.classList.remove('open');
          var answer = other.querySelector('.faq-answer');
          if (answer) answer.style.maxHeight = null;
        });

        // Open clicked item (if it wasn't already open)
        if (!isOpen) {
          item.classList.add('open');
          var answer = item.querySelector('.faq-answer');
          if (answer) {
            answer.style.maxHeight = answer.scrollHeight + 'px';
          }
        }
      });
    });
  }

  // ── Init ─────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    fetchLatestRelease();
    initFaqAccordion();
  });
})();
