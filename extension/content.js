// OmniFetch Content Script
// Tracks keyboard modifier overrides (Alt/Ctrl) and injects the IDM-style floating video grabber

(() => {
  // 1. Keyboard modifier tracking for Alt/Ctrl overrides
  let lastAlt = false;
  let lastCtrl = false;

  function syncKeyModifiers(e) {
    const currentAlt = !!e.altKey;
    const currentCtrl = !!e.ctrlKey;

    if (currentAlt !== lastAlt || currentCtrl !== lastCtrl) {
      lastAlt = currentAlt;
      lastCtrl = currentCtrl;

      try {
        chrome.runtime.sendMessage({
          type: "KEY_MODIFIERS_UPDATE",
          altKey: currentAlt,
          ctrlKey: currentCtrl
        });
      } catch (err) {
        // Context invalidated on extension reload
      }
    }
  }

  window.addEventListener("keydown", syncKeyModifiers, { capture: true, passive: true });
  window.addEventListener("keyup", syncKeyModifiers, { capture: true, passive: true });
  window.addEventListener("blur", () => {
    if (lastAlt || lastCtrl) {
      lastAlt = false;
      lastCtrl = false;
      try {
        chrome.runtime.sendMessage({
          type: "KEY_MODIFIERS_UPDATE",
          altKey: false,
          ctrlKey: false
        });
      } catch (err) {}
    }
  });

  // CTRL + Click force-capture handler
  document.addEventListener("click", (e) => {
    if (e.ctrlKey) {
      const anchor = e.target.closest("a");
      if (anchor && anchor.href && /^https?:\/\//i.test(anchor.href)) {
        e.preventDefault();
        e.stopPropagation();

        chrome.runtime.sendMessage({
          type: "CAPTURE_URL",
          url: anchor.href,
          referrer: window.location.href,
          suggestedFileName: anchor.download || ""
        }, (res) => {
          showFloatingNotification("OmniFetch: Captured download link via Ctrl+Click");
        });
      }
    }
  }, { capture: true });

  // 2. IDM-Style Floating Video Grabber
  const detectedStreams = new Set();

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.type === "STREAM_DETECTED" && message.url) {
      detectedStreams.add(message.url);
      attachVideoGrabbers();
    }
  });

  function attachVideoGrabbers() {
    const videos = document.querySelectorAll("video");
    videos.forEach((video) => {
      if (video.dataset.omnifetchAttached) return;
      video.dataset.omnifetchAttached = "true";

      createFloatingGrabber(video);
    });
  }

  function createFloatingGrabber(videoElement) {
    const container = document.createElement("div");
    container.className = "omnifetch-video-grabber";
    container.innerHTML = `
      <div class="omnifetch-grabber-btn" title="Download this video with OmniFetch">
        <svg class="omnifetch-grabber-icon" viewBox="0 0 24 24" fill="currentColor">
          <path d="M17 10.5V7c0-.55-.45-1-1-1H4c-.55 0-1 .45-1 1v10c0 .55.45 1 1 1h12c.55 0 1-.45 1-1v-3.5l4 4v-11l-4 4zM14 13h-3v3H9v-3H6v-2h3V8h2v3h3v2z"/>
        </svg>
        <span class="omnifetch-grabber-text">Download this video</span>
      </div>
    `;

    // Position relative to video or container
    const parent = videoElement.parentElement;
    if (parent && getComputedStyle(parent).position === "static") {
      parent.style.position = "relative";
    }

    container.addEventListener("click", (e) => {
      e.stopPropagation();
      e.preventDefault();

      let streamUrl = videoElement.currentSrc || videoElement.src;
      if (!streamUrl && detectedStreams.size > 0) {
        streamUrl = Array.from(detectedStreams).pop();
      }

      if (streamUrl) {
        chrome.runtime.sendMessage({
          type: "CAPTURE_URL",
          url: streamUrl,
          referrer: window.location.href,
          suggestedFileName: document.title.replace(/[^a-zA-Z0-9_-]/g, "_") + ".mp4"
        }, (res) => {
          showFloatingNotification("OmniFetch: Video captured successfully!");
        });
      } else {
        showFloatingNotification("OmniFetch: No active stream detected yet. Play video first.");
      }
    });

    if (parent) {
      parent.appendChild(container);
    }
  }

  // Observe DOM for dynamically inserted video elements
  const observer = new MutationObserver(() => {
    attachVideoGrabbers();
  });
  observer.observe(document.body || document.documentElement, {
    childList: true,
    subtree: true
  });

  // Initial scan
  attachVideoGrabbers();

  function showFloatingNotification(text) {
    const toast = document.createElement("div");
    toast.className = "omnifetch-toast";
    toast.textContent = text;
    document.body.appendChild(toast);
    setTimeout(() => {
      toast.classList.add("omnifetch-toast-fade");
      setTimeout(() => toast.remove(), 400);
    }, 2500);
  }
})();
