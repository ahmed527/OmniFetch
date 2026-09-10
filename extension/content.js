// OmniFetch Content Script
// Tracks keyboard modifier overrides (Alt/Ctrl) and injects the IDM-style floating video grabber

(() => {
  // Context guard: check if extension context is still valid
  function isExtensionValid() {
    return typeof chrome !== "undefined" && Boolean(chrome.runtime && chrome.runtime.id);
  }

  // Global safeguard: intercept and suppress any extension context invalidation unhandled rejections
  // so Chrome will never log them on chrome://extensions
  if (typeof window !== "undefined") {
    window.addEventListener("unhandledrejection", (event) => {
      const reason = event.reason ? (event.reason.message || String(event.reason)) : "";
      if (reason.includes("Extension context invalidated") || reason.includes("message port closed")) {
        event.preventDefault();
        cleanupOnInvalidatedContext();
      }
    });
  }

  // Safe wrapper for chrome.runtime.sendMessage that prevents Uncaught (in promise) Error: Extension context invalidated
  function safeSendMessage(message, callback) {
    if (!isExtensionValid()) {
      cleanupOnInvalidatedContext();
      if (typeof callback === "function") callback(null);
      return;
    }

    try {
      let callbackFired = false;
      const resPromise = chrome.runtime.sendMessage(message, (res) => {
        callbackFired = true;
        if (chrome.runtime?.lastError) {
          const errText = chrome.runtime.lastError.message || "";
          if (errText.includes("Extension context invalidated")) {
            cleanupOnInvalidatedContext();
          }
        }
        if (typeof callback === "function") {
          callback(res);
        }
      });

      if (resPromise && typeof resPromise.catch === "function") {
        resPromise.catch((err) => {
          cleanupOnInvalidatedContext();
          if (!callbackFired && typeof callback === "function") {
            callback(null);
          }
        });
      }
    } catch (err) {
      cleanupOnInvalidatedContext();
      if (typeof callback === "function") {
        callback(null);
      }
    }
  }

  function cleanupOnInvalidatedContext() {
    if (observer) {
      try {
        observer.disconnect();
      } catch (e) {}
      observer = null;
    }
    try {
      window.removeEventListener("keydown", syncKeyModifiers, { capture: true });
      window.removeEventListener("keyup", syncKeyModifiers, { capture: true });
      window.removeEventListener("blur", handleBlur);
      document.removeEventListener("click", handleClickCapture, { capture: true });
    } catch (e) {}
  }

  // 1. Keyboard modifier tracking for Alt/Ctrl overrides
  let lastAlt = false;
  let lastCtrl = false;

  function syncKeyModifiers(e) {
    if (!isExtensionValid()) {
      cleanupOnInvalidatedContext();
      return;
    }

    const currentAlt = !!e.altKey;
    const currentCtrl = !!e.ctrlKey;

    if (currentAlt !== lastAlt || currentCtrl !== lastCtrl) {
      lastAlt = currentAlt;
      lastCtrl = currentCtrl;

      safeSendMessage({
        type: "KEY_MODIFIERS_UPDATE",
        altKey: currentAlt,
        ctrlKey: currentCtrl
      });
    }
  }

  function handleBlur() {
    if (!isExtensionValid()) {
      cleanupOnInvalidatedContext();
      return;
    }
    if (lastAlt || lastCtrl) {
      lastAlt = false;
      lastCtrl = false;
      safeSendMessage({
        type: "KEY_MODIFIERS_UPDATE",
        altKey: false,
        ctrlKey: false
      });
    }
  }

  window.addEventListener("keydown", syncKeyModifiers, { capture: true, passive: true });
  window.addEventListener("keyup", syncKeyModifiers, { capture: true, passive: true });
  window.addEventListener("blur", handleBlur);

  // CTRL + Click force-capture handler
  function handleClickCapture(e) {
    if (!isExtensionValid()) {
      cleanupOnInvalidatedContext();
      return;
    }

    if (e.ctrlKey) {
      const anchor = e.target.closest("a");
      if (anchor && anchor.href && /^https?:\/\//i.test(anchor.href)) {
        e.preventDefault();
        e.stopPropagation();

        safeSendMessage({
          type: "CAPTURE_URL",
          url: anchor.href,
          referrer: window.location.href,
          suggestedFileName: anchor.download || ""
        }, (res) => {
          showFloatingNotification("OmniFetch: Captured download link via Ctrl+Click");
        });
      }
    }
  }
  document.addEventListener("click", handleClickCapture, { capture: true });

  // 2. IDM-Style Floating Video Grabber
  const detectedStreams = new Set();

  function attachVideoGrabbers() {
    if (!isExtensionValid()) {
      cleanupOnInvalidatedContext();
      return;
    }

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

    container.addEventListener("click", async (e) => {
      e.stopPropagation();
      e.preventDefault();

      if (!isExtensionValid()) {
        showFloatingNotification("OmniFetch extension was reloaded. Please refresh this page.");
        cleanupOnInvalidatedContext();
        return;
      }

      let streamUrl = videoElement.currentSrc || videoElement.src;
      // If currentSrc is a blob URL or empty, search detected streams
      if (!streamUrl || streamUrl.startsWith("blob:")) {
        streamUrl = null;
        if (detectedStreams.size > 0) {
          streamUrl = Array.from(detectedStreams).pop();
        }
      }

      // If still no stream found, query background script for any streams recorded for this tab
      if (!streamUrl) {
        try {
          if (!isExtensionValid()) return;
          const tabRes = await new Promise((resolve) => {
            const timeoutId = setTimeout(() => resolve(null), 2000);
            safeSendMessage({ type: "GET_TAB_STREAMS" }, (res) => {
              clearTimeout(timeoutId);
              resolve(res);
            });
          });
          if (tabRes && tabRes.streams && tabRes.streams.length > 0) {
            streamUrl = tabRes.streams[tabRes.streams.length - 1];
            detectedStreams.add(streamUrl);
          }
        } catch (err) {}
      }

      if (streamUrl && !streamUrl.startsWith("blob:")) {
        showFloatingNotification("OmniFetch: Sending stream to OmniFetch...");
        const suggestedName = extractVideoFileName(streamUrl);
        safeSendMessage({
          type: "CAPTURE_URL",
          url: streamUrl,
          referrer: window.location.href,
          suggestedFileName: suggestedName
        }, (res) => {
          if (chrome.runtime?.lastError) {
            showFloatingNotification("OmniFetch Error: " + chrome.runtime.lastError.message);
          } else if (!res || !res.success) {
            showFloatingNotification("OmniFetch Error: " + (res?.error || "Could not connect to desktop app"));
          } else if (res.data && res.data.status === "error") {
            showFloatingNotification("OmniFetch: " + (res.data.message || "Download declined"));
          } else {
            showFloatingNotification("OmniFetch: Download queued in OmniFetch! Check desktop app.");
          }
        });
      } else {
        showFloatingNotification("OmniFetch: Please press Play on the video to capture stream");
      }
    });

    if (parent) {
      parent.appendChild(container);
    }
  }

  function extractVideoFileName(streamUrl) {
    let title = "";
    try {
      // 1. Check YouTube watch metadata header
      const ytHeader = document.querySelector("h1.ytd-watch-metadata yt-formatted-string") ||
                       document.querySelector("h1.title yt-formatted-string") ||
                       document.querySelector("h1.title");
      if (ytHeader && ytHeader.textContent) {
        title = ytHeader.textContent.trim();
      }
    } catch (e) {}

    // 2. Fallback to document title
    if (!title && document.title) {
      title = document.title.replace(/\s*-\s*YouTube$/i, "").trim();
    }

    // 3. Clean invalid file name characters
    if (title) {
      title = title.replace(/[\\/:*?"<>|]/g, "_").replace(/\s+/g, " ").trim();
    }

    if (!title || title.length === 0) {
      title = "video";
    }

    // 4. Sniff extension from stream URL
    let ext = ".mp4";
    if (streamUrl) {
      const lower = streamUrl.toLowerCase();
      if (lower.includes("mime=video%2fwebm") || lower.includes("mime=video/webm")) {
        ext = ".webm";
      } else if (lower.includes("mime=audio%2fmp4") || lower.includes("mime=audio/mp4")) {
        ext = ".m4a";
      } else if (lower.includes("mime=audio%2fwebm") || lower.includes("mime=audio/webm")) {
        ext = ".weba";
      } else if (lower.includes(".m3u8")) {
        ext = ".mp4";
      }
    }

    return `${title}${ext}`;
  }

  // Observe DOM for dynamically inserted video elements
  let observer = null;
  if (isExtensionValid()) {
    try {
      observer = new MutationObserver(() => {
        if (!isExtensionValid()) {
          cleanupOnInvalidatedContext();
          return;
        }
        attachVideoGrabbers();
      });
      observer.observe(document.body || document.documentElement, {
        childList: true,
        subtree: true
      });
    } catch (err) {
      cleanupOnInvalidatedContext();
    }
  }

  // Initial scan
  attachVideoGrabbers();

  function showFloatingNotification(text) {
    try {
      const toast = document.createElement("div");
      toast.className = "omnifetch-toast";
      toast.textContent = text;
      document.body.appendChild(toast);
      setTimeout(() => {
        toast.classList.add("omnifetch-toast-fade");
        setTimeout(() => toast.remove(), 400);
      }, 2500);
    } catch (e) {}
  }
})();
