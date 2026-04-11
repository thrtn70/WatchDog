/* ═══════════════════════════════════════════════════════════
   WatchDog — Landing Page
   - GitHub API release fetch
   - FAQ accordion (grid-template-rows)
   - Scroll-triggered reveals (IntersectionObserver)
   ═══════════════════════════════════════════════════════════ */

(function () {
  'use strict';

  // ── URL Validation ───────────────────────────────────────

  var ALLOWED_ORIGINS = ['https://github.com', 'https://objects.githubusercontent.com'];

  function isSafeDownloadUrl(url) {
    try {
      var parsed = new URL(url);
      return ALLOWED_ORIGINS.indexOf(parsed.origin) !== -1;
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

        // Download buttons — validate URL before assigning
        [['download-btn', 'download-text'], ['download-btn-footer', 'download-text-footer']].forEach(function (pair) {
          var btn = document.getElementById(pair[0]);
          var btnText = document.getElementById(pair[1]);
          if (btn && btnText) {
            btnText.textContent = 'Download v' + version;
            if (setupAsset && isSafeDownloadUrl(setupAsset.browser_download_url)) {
              btn.href = setupAsset.browser_download_url;
            }
          }
        });

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
      .catch(function (err) {
        // Fall back to generic release link already set in HTML
        if (typeof console !== 'undefined' && console.warn) {
          console.warn('[WatchDog] Release fetch failed:', err);
        }
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
          var otherPanel = other.querySelector('.faq-a');
          if (otherBtn) otherBtn.setAttribute('aria-expanded', 'false');
          if (otherPanel) otherPanel.setAttribute('aria-hidden', 'true');
        });

        // Toggle clicked
        if (!wasOpen) {
          item.classList.add('open');
          btn.setAttribute('aria-expanded', 'true');
          var panel = item.querySelector('.faq-a');
          if (panel) panel.setAttribute('aria-hidden', 'false');
        }
      });
    });
  }

  // ── Scroll Reveals ───────────────────────────────────────

  function initReveals() {
    // Skip feature-hero elements if scroll-driven animations are supported (CSS handles them)
    var supportsScrollTimeline = CSS && CSS.supports && CSS.supports('animation-timeline', 'scroll()');
    var selector = supportsScrollTimeline ? '.reveal:not(.feature-hero)' : '.reveal';
    var els = document.querySelectorAll(selector);
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
    var COPY_LABEL = 'Copy';

    function makeCheckIcon() {
      var svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      svg.setAttribute('class', 'copy-check');
      svg.setAttribute('width', '14');
      svg.setAttribute('height', '14');
      svg.setAttribute('viewBox', '0 0 24 24');
      svg.setAttribute('fill', 'none');
      svg.setAttribute('stroke', 'currentColor');
      svg.setAttribute('stroke-width', '3');
      svg.setAttribute('stroke-linecap', 'round');
      svg.setAttribute('stroke-linejoin', 'round');
      var poly = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
      poly.setAttribute('points', '20 6 9 17 4 12');
      svg.appendChild(poly);
      return svg;
    }

    blocks.forEach(function (pre) {
      var btn = document.createElement('button');
      btn.className = 'copy-btn';
      btn.setAttribute('aria-label', 'Copy to clipboard');
      btn.textContent = COPY_LABEL;

      btn.addEventListener('click', function () {
        var text = pre.textContent;
        navigator.clipboard.writeText(text).then(function () {
          btn.textContent = '';
          btn.appendChild(makeCheckIcon());
          btn.classList.add('copied');
          setTimeout(function () {
            btn.textContent = COPY_LABEL;
            btn.classList.remove('copied');
          }, 2000);
        }).catch(function () {
          // Fallback: select the text
          var range = document.createRange();
          range.selectNodeContents(pre);
          var sel = window.getSelection();
          if (sel) {
            sel.removeAllRanges();
            sel.addRange(range);
          }
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

    // Mobile nav toggle
    var toggle = nav.querySelector('.nav-toggle');
    var links = nav.querySelector('.site-nav__links');
    if (toggle && links) {
      toggle.addEventListener('click', function () {
        var open = links.classList.toggle('open');
        toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      });

      // Close menu on link click
      links.addEventListener('click', function (e) {
        if (e.target.closest('a')) {
          links.classList.remove('open');
          toggle.setAttribute('aria-expanded', 'false');
        }
      });

      // Close on outside click
      document.addEventListener('click', function (e) {
        if (!nav.contains(e.target)) {
          links.classList.remove('open');
          toggle.setAttribute('aria-expanded', 'false');
        }
      });
    }

    // Smooth scroll for anchor links
    nav.addEventListener('click', function (e) {
      var link = e.target.closest('a[href^="#"]');
      if (!link) return;

      var targetId = link.getAttribute('href').slice(1);
      var target = targetId ? document.getElementById(targetId) : null;

      if (target) {
        e.preventDefault();
        target.scrollIntoView({ behavior: 'smooth', block: 'start' });
        // Move focus to the target section for keyboard navigation
        target.setAttribute('tabindex', '-1');
        target.focus({ preventScroll: true });
      } else if (link.getAttribute('href') === '#') {
        e.preventDefault();
        window.scrollTo({ top: 0, behavior: 'smooth' });
      }
    });
  }

  // ── Scroll Spy ───────────────────────────────────────────

  function initScrollSpy() {
    var navLinks = document.querySelectorAll('.site-nav__links a[href^="#"]');
    if (!navLinks.length || !('IntersectionObserver' in window)) return;

    var sections = [];
    navLinks.forEach(function (link) {
      var id = link.getAttribute('href').slice(1);
      var section = document.getElementById(id);
      if (section) sections.push({ el: section, link: link });
    });

    if (!sections.length) return;

    var observer = new IntersectionObserver(
      function (entries) {
        // Find the topmost visible section across all entries
        var visibleTargets = [];
        entries.forEach(function (entry) {
          if (entry.isIntersecting) visibleTargets.push(entry.target);
        });
        if (!visibleTargets.length) return;

        var topmost = sections.find(function (s) {
          return visibleTargets.indexOf(s.el) !== -1;
        });
        if (topmost) {
          navLinks.forEach(function (l) { l.classList.remove('active'); });
          topmost.link.classList.add('active');
        }
      },
      { rootMargin: '-20% 0px -60% 0px' }
    );

    sections.forEach(function (s) { observer.observe(s.el); });
  }

  // ── Releases Section ─────────────────────────────────────

  var RELEASES_LIST_URL = 'https://api.github.com/repos/thrtn70/WatchDog/releases?per_page=6';

  function formatDate(dateStr) {
    var d = new Date(dateStr);
    if (isNaN(d.getTime())) return '';
    var months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
    return months[d.getMonth()] + ' ' + d.getDate() + ', ' + d.getFullYear();
  }

  function getSetupUrl(release) {
    var asset = null;
    (release.assets || []).forEach(function (a) {
      if (!asset && a.name && a.name.endsWith(SETUP_SUFFIX)) asset = a;
    });
    if (asset && isSafeDownloadUrl(asset.browser_download_url)) {
      return asset.browser_download_url;
    }
    // Fallback to release page — validate even though GitHub API is trusted
    return isSafeDownloadUrl(release.html_url) ? release.html_url : 'https://github.com/thrtn70/WatchDog/releases';
  }

  function fetchReleasesList() {
    var container = document.getElementById('releases-list');
    if (!container) return;

    fetch(RELEASES_LIST_URL, {
      headers: { 'Accept': 'application/vnd.github+json' },
    })
      .then(function (res) {
        if (!res.ok) return res.text().then(function () { return null; });
        return res.json();
      })
      .then(function (releases) {
        if (!releases || !releases.length) return;

        // Filter out drafts — they have incomplete assets and placeholder tags
        var published = releases.filter(function (r) { return !r.draft; });
        if (!published.length) return;

        var hero = published[0];
        var rest = published.slice(1);

        // Find the first stable release for the "Latest" badge
        var firstStableIdx = -1;
        if (hero.prerelease) {
          for (var i = 0; i < rest.length; i++) {
            if (!rest[i].prerelease && !rest[i].draft) {
              firstStableIdx = i;
              break;
            }
          }
        }

        // Build hero card
        var isPrerelease = hero.prerelease;
        var heroTag = hero.tag_name || '';
        var heroDate = hero.published_at ? formatDate(hero.published_at) : '';
        var heroUrl = getSetupUrl(hero);

        var heroHtml = document.createElement('div');
        heroHtml.className = 'release-hero reveal ' + (isPrerelease ? 'release-hero--prerelease' : 'release-hero--stable');

        var infoDiv = document.createElement('div');
        infoDiv.className = 'release-hero__info';

        var topDiv = document.createElement('div');
        topDiv.className = 'release-hero__top';

        var badge = document.createElement('span');
        badge.className = 'release-badge ' + (isPrerelease ? 'release-badge--prerelease' : 'release-badge--latest');
        badge.textContent = isPrerelease ? 'Pre-release' : 'Latest';
        topDiv.appendChild(badge);

        var version = document.createElement('span');
        version.className = 'release-hero__version';
        version.textContent = heroTag;
        topDiv.appendChild(version);

        infoDiv.appendChild(topDiv);

        var date = document.createElement('span');
        date.className = 'release-hero__date';
        date.textContent = heroDate;
        infoDiv.appendChild(date);

        heroHtml.appendChild(infoDiv);

        var dlBtn = document.createElement('a');
        dlBtn.className = 'btn btn--primary';
        dlBtn.href = heroUrl;
        dlBtn.target = '_blank';
        dlBtn.rel = 'noopener';
        dlBtn.textContent = 'Download ' + heroTag;
        heroHtml.appendChild(dlBtn);

        container.appendChild(heroHtml);

        // Build compact rows
        if (rest.length) {
          var list = document.createElement('div');
          list.className = 'release-compact-list reveal';

          rest.forEach(function (rel, idx) {
            var row = document.createElement('div');
            row.className = 'release-row';

            var rowInfo = document.createElement('div');
            rowInfo.className = 'release-row__info';

            // Badge for first stable release when hero is pre-release
            if (idx === firstStableIdx) {
              var stableBadge = document.createElement('span');
              stableBadge.className = 'release-badge release-badge--latest';
              stableBadge.textContent = 'Latest';
              rowInfo.appendChild(stableBadge);
            }

            var rowVersion = document.createElement('span');
            rowVersion.className = 'release-row__version';
            rowVersion.textContent = rel.tag_name || '';
            rowInfo.appendChild(rowVersion);

            var rowDate = document.createElement('span');
            rowDate.className = 'release-row__date';
            rowDate.textContent = rel.published_at ? formatDate(rel.published_at) : '';
            rowInfo.appendChild(rowDate);

            row.appendChild(rowInfo);

            var dlLink = document.createElement('a');
            dlLink.className = 'release-row__dl';
            dlLink.href = getSetupUrl(rel);
            dlLink.target = '_blank';
            dlLink.rel = 'noopener';
            dlLink.setAttribute('aria-label', 'Download ' + (rel.tag_name || ''));

            var dlSvg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
            dlSvg.setAttribute('width', '16');
            dlSvg.setAttribute('height', '16');
            dlSvg.setAttribute('viewBox', '0 0 24 24');
            dlSvg.setAttribute('fill', 'none');
            dlSvg.setAttribute('stroke', 'currentColor');
            dlSvg.setAttribute('stroke-width', '2');
            dlSvg.setAttribute('stroke-linecap', 'round');
            dlSvg.setAttribute('stroke-linejoin', 'round');
            dlSvg.setAttribute('aria-hidden', 'true');
            var path1 = document.createElementNS('http://www.w3.org/2000/svg', 'polyline');
            path1.setAttribute('points', '7 10 12 15 17 10');
            var path2 = document.createElementNS('http://www.w3.org/2000/svg', 'line');
            path2.setAttribute('x1', '12');
            path2.setAttribute('y1', '15');
            path2.setAttribute('x2', '12');
            path2.setAttribute('y2', '3');
            dlSvg.appendChild(path1);
            dlSvg.appendChild(path2);
            dlLink.appendChild(dlSvg);

            row.appendChild(dlLink);
            list.appendChild(row);
          });

          container.appendChild(list);
        }

        // Trigger reveals on dynamically added elements
        container.querySelectorAll('.reveal').forEach(function (el) {
          el.classList.add('visible');
        });
      })
      .catch(function (err) {
        if (typeof console !== 'undefined' && console.warn) {
          console.warn('[WatchDog] Releases fetch failed:', err);
        }
      });
  }

  // ── Step Number Count-Up ─────────────────────────────────

  function initStepCountUp() {
    var nums = document.querySelectorAll('.step__num');
    if (!nums.length || !('IntersectionObserver' in window)) return;

    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    var observer = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (!entry.isIntersecting) return;
        var el = entry.target;
        var final = parseInt(el.textContent, 10);
        if (isNaN(final)) return;

        observer.unobserve(el);
        var current = 0;
        var duration = 400;
        var start = performance.now();

        function step(now) {
          var elapsed = now - start;
          var progress = Math.min(elapsed / duration, 1);
          // Ease out quart
          var eased = 1 - Math.pow(1 - progress, 4);
          current = Math.round(eased * final);
          el.textContent = current < 10 ? '0' + current : '' + current;
          if (progress < 1) requestAnimationFrame(step);
        }

        requestAnimationFrame(step);
      });
    }, { threshold: 0.5 });

    nums.forEach(function (el) { observer.observe(el); });
  }

  // ── AI Waveform Visualizer ───────────────────────────────

  function initWaveform() {
    var canvas = document.querySelector('.waveform-canvas');
    if (!canvas) return;

    // Skip for reduced motion
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      canvas.style.display = 'none';
      return;
    }

    var ctx = canvas.getContext('2d');
    if (!ctx) return;

    var dpr = window.devicePixelRatio || 1;
    var w, h, bars;
    var BAR_COUNT = 64;
    var frameId = null;
    var isVisible = false;

    // State
    var amplitudes = new Float32Array(BAR_COUNT);
    var targets = new Float32Array(BAR_COUNT);
    var spikeTimer = 0;
    var spikeActive = false;
    var spikePeak = 0;
    var lastTime = 0;

    function resize() {
      var rect = canvas.parentElement.getBoundingClientRect();
      if (rect.width === 0 || rect.height === 0) {
        isVisible = false;
        return;
      }
      w = rect.width;
      h = rect.height;
      canvas.width = w * dpr;
      canvas.height = h * dpr;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    }

    function tick(time) {
      if (!isVisible) return;

      var dt = lastTime ? Math.min((time - lastTime) * 0.001, 0.05) : 0.016;
      lastTime = time;
      var t = time * 0.001;

      // Generate ambient noise targets
      for (var i = 0; i < BAR_COUNT; i++) {
        var noise = Math.sin(t * 2.3 + i * 0.7) * 0.15 +
                    Math.sin(t * 1.1 + i * 1.3) * 0.1 +
                    Math.sin(t * 3.7 + i * 0.3) * 0.08;
        targets[i] = 0.08 + Math.abs(noise);
      }

      // Spike events — every 4-7 seconds
      spikeTimer -= dt;
      if (spikeTimer <= 0 && !spikeActive) {
        spikeActive = true;
        spikePeak = 0.6 + Math.random() * 0.35;
        spikeTimer = 4 + Math.random() * 3;
      }

      if (spikeActive) {
        var center = Math.floor(BAR_COUNT * (0.3 + Math.random() * 0.4));
        var spread = 6 + Math.floor(Math.random() * 8);
        for (var j = -spread; j <= spread; j++) {
          var idx = center + j;
          if (idx >= 0 && idx < BAR_COUNT) {
            var falloff = 1 - Math.abs(j) / (spread + 1);
            targets[idx] = Math.max(targets[idx], spikePeak * falloff * falloff);
          }
        }
        spikePeak *= 0.88;
        if (spikePeak < 0.1) spikeActive = false;
      }

      // Lerp amplitudes toward targets
      for (var k = 0; k < BAR_COUNT; k++) {
        amplitudes[k] += (targets[k] - amplitudes[k]) * 0.18;
      }

      // Draw
      ctx.clearRect(0, 0, w, h);
      var barW = w / BAR_COUNT;
      var gap = 1.5;

      for (var b = 0; b < BAR_COUNT; b++) {
        var barH = amplitudes[b] * h * 0.8;
        var x = b * barW + gap;
        var y = (h - barH) / 2;
        var bw = barW - gap * 2;
        if (bw < 1) bw = 1;

        // Color: warm amber for spikes, teal for ambient
        var intensity = amplitudes[b];
        if (intensity > 0.35) {
          // Warm amber for spikes — capped at 0.6 for readability
          var alpha = Math.min(intensity * 1.2, 0.6);
          ctx.fillStyle = 'rgba(217, 169, 78, ' + alpha + ')';
        } else {
          ctx.fillStyle = 'rgba(46, 196, 182, ' + (intensity * 1.8) + ')';
        }

        ctx.beginPath();
        if (ctx.roundRect) {
          ctx.roundRect(x, y, bw, barH, 1);
        } else {
          ctx.rect(x, y, bw, barH);
        }
        ctx.fill();
      }

      frameId = requestAnimationFrame(tick);
    }

    // Visibility observer — pause when off-screen
    var observer = new IntersectionObserver(function (entries) {
      isVisible = entries[0].isIntersecting;
      if (isVisible) {
        resize();
        if (!frameId) frameId = requestAnimationFrame(tick);
      } else if (frameId) {
        cancelAnimationFrame(frameId);
        frameId = null;
      }
    }, { threshold: 0.1 });

    observer.observe(canvas.parentElement);
    window.addEventListener('resize', function () { if (isVisible) resize(); }, { passive: true });

    // Pause animation when tab is hidden to save CPU
    document.addEventListener('visibilitychange', function () {
      if (document.hidden) {
        if (frameId) { cancelAnimationFrame(frameId); frameId = null; }
      } else if (isVisible && !frameId) {
        lastTime = 0;
        frameId = requestAnimationFrame(tick);
      }
    });
  }

  // ── Screenshot Lightbox ──────────────────────────────────

  function initLightbox() {
    var lightbox = document.getElementById('lightbox');
    var lightboxImg = document.getElementById('lightbox-img');
    if (!lightbox || !lightboxImg) return;

    var screenshots = document.querySelectorAll('.screenshot');

    screenshots.forEach(function (link) {
      link.addEventListener('click', function (e) {
        e.preventDefault();
        var img = link.querySelector('img');
        if (!img) return;

        // Validate same-origin before assigning src
        try {
          var parsed = new URL(link.href);
          if (parsed.origin !== window.location.origin) return;
        } catch (err) { return; }

        lightboxImg.src = link.href;
        lightboxImg.alt = img.alt;
        lightbox.showModal();
        document.body.style.overflow = 'hidden';
      });
    });

    function closeLightbox() {
      if (lightbox.open) lightbox.close();
      lightboxImg.src = '';
      document.body.style.overflow = '';
    }

    // Close on backdrop click or close button
    lightbox.addEventListener('click', function (e) {
      if (e.target === lightbox || e.target.classList.contains('lightbox__close')) {
        closeLightbox();
      }
    });

    // Native <dialog> handles Escape automatically, but ensure cleanup runs
    lightbox.addEventListener('close', function () {
      lightboxImg.src = '';
      document.body.style.overflow = '';
    });
  }

  // ── Console Easter Egg ───────────────────────────────────

  function initConsoleEasterEgg() {
    if (typeof console === 'undefined' || !console.log) return;
    console.log(
      '%c WatchDog ',
      'background: #2EC4B6; color: #0d1117; font-weight: 900; font-size: 14px; padding: 4px 8px; border-radius: 4px;',
      '\n\nLightweight game clipper for Windows.\nhttps://github.com/thrtn70/WatchDog\n\nGPL-2.0 · .NET 9 · OBS Studio\n'
    );
  }

  // ── Init ─────────────────────────────────────────────────

  document.addEventListener('DOMContentLoaded', function () {
    fetchLatestRelease();
    initFaq();
    initReveals();
    initNav();
    initCopyButtons();
    initScrollSpy();
    fetchReleasesList();
    initStepCountUp();
    initWaveform();
    initLightbox();
    initConsoleEasterEgg();
  });
})();
