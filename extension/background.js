// OmniFetch Chrome Extension (Manifest V3)
// Tier 1 Browser Scout: Intercepts downloads, harvests cookies, sniffs streaming media, and communicates via Native Messaging

const NATIVE_HOST = "com.omnifetch.bridge";

// 1. Initialize default configuration in chrome.storage.local on install
chrome.runtime.onInstalled.addListener(async () => {
  const current = await chrome.storage.local.get(["captureEnabled", "videoGrabberEnabled", "bypassExtensions"]);
  const defaults = {
    captureEnabled: current.captureEnabled ?? true,
    videoGrabberEnabled: current.videoGrabberEnabled ?? true,
    bypassExtensions: current.bypassExtensions ?? [".pdf"]
  };
  await chrome.storage.local.set(defaults);
  console.log("[OmniFetch] Extension installed with configuration:", defaults);
});

// Cache in-memory for active tab key modifiers (Alt = bypass, Ctrl = force)
const tabModifierState = new Map();
// Cache detected video stream URLs per tab
const tabStreams = new Map();

// Helper to initialize streams from session storage (persists across service worker restarts)
async function initSessionStreams() {
  try {
    if (chrome.storage?.session) {
      const stored = await chrome.storage.session.get("tabStreams");
      if (stored && stored.tabStreams) {
        for (const [tabIdStr, streamsObj] of Object.entries(stored.tabStreams)) {
          const tid = Number(tabIdStr);
          if (!isNaN(tid) && typeof streamsObj === "object") {
            tabStreams.set(tid, new Map(Object.entries(streamsObj)));
          }
        }
      }
    }
  } catch (e) {}
}
initSessionStreams();

async function persistSessionStreams() {
  try {
    if (chrome.storage?.session) {
      const serialized = {};
      for (const [tabId, streamMap] of tabStreams.entries()) {
        if (streamMap instanceof Map) {
          serialized[tabId] = Object.fromEntries(streamMap.entries());
        }
      }
      await chrome.storage.session.set({ tabStreams: serialized });
    }
  } catch (e) {}
}

function recordStreamForTab(tabId, streamUrl) {
  if (!tabId || tabId < 0 || !streamUrl) return;
  if (!tabStreams.has(tabId)) {
    tabStreams.set(tabId, new Map());
  }
  const streamMeta = parseStreamMetadata(streamUrl);
  const streamKey = streamMeta.itag || (streamMeta.quality + "_" + streamMeta.format);
  const currentTabStreams = tabStreams.get(tabId);
  if (currentTabStreams instanceof Map) {
    currentTabStreams.set(streamKey, streamMeta);
  }
  persistSessionStreams();
  console.log(`[OmniFetch BG] Tab ${tabId} detected stream: [${streamMeta.label}] format=${streamMeta.format}, size=${streamMeta.sizeFormatted || "unknown"}, itag=${streamMeta.itag}`);
}

// 2. Listen for messages from content scripts and popup
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || !message.type) return false;

  if (message.type === "KEY_MODIFIERS_UPDATE") {
    if (sender.tab && sender.tab.id) {
      tabModifierState.set(sender.tab.id, {
        altKey: !!message.altKey,
        ctrlKey: !!message.ctrlKey,
        timestamp: Date.now()
      });
    }
    return false;
  }

  if (message.type === "REGISTER_STREAMS") {
    const tabId = sender.tab ? sender.tab.id : message.tabId;
    if (tabId && Array.isArray(message.streams)) {
      message.streams.forEach(s => {
        const url = typeof s === "string" ? s : s.url;
        recordStreamForTab(tabId, url);
      });
      console.log(`[OmniFetch BG] Tab ${tabId} registered ${message.streams.length} stream(s) from content script`);
    }
    sendResponse({ success: true });
    return false;
  }

  if (message.type === "GET_TAB_STREAMS") {
    const tabId = sender.tab ? sender.tab.id : null;
    let streamList = [];
    if (tabId && tabStreams.has(tabId)) {
      const tabMap = tabStreams.get(tabId);
      if (tabMap instanceof Map) {
        streamList = Array.from(tabMap.values()).sort((a, b) => b.order - a.order);
      } else if (tabMap instanceof Set) {
        streamList = Array.from(tabMap).map(url => (typeof url === "string" ? parseStreamMetadata(url) : url));
      }
    }
    console.log(`[OmniFetch BG] GET_TAB_STREAMS for tab ${tabId}: returning ${streamList.length} stream option(s)`, streamList);
    sendResponse({ success: true, streams: streamList });
    return false;
  }

  if (message.type === "PING_DAEMON") {
    const pingPayload = { action: "ping", client: "OmniFetch Chrome Extension" };
    chrome.runtime.sendNativeMessage(NATIVE_HOST, pingPayload, (response) => {
      if (chrome.runtime.lastError) {
        sendResponse({
          success: false,
          error: chrome.runtime.lastError.message || "Native host unavailable"
        });
      } else {
        sendResponse({
          success: true,
          data: response
        });
      }
    });
    return true; // Keep message channel open for async sendResponse
  }

  if (message.type === "CAPTURE_URL") {
    console.log("[OmniFetch BG] Received CAPTURE_URL request:", {
      url: message.url,
      suggestedFileName: message.suggestedFileName,
      referrer: message.referrer
    });
    if (!message.url || message.url.startsWith("blob:")) {
      console.warn("[OmniFetch BG] Rejected blob: or empty URL:", message.url);
      sendResponse({ success: false, error: "Browser blob: URLs cannot be downloaded directly outside the browser." });
      return false;
    }

    captureExplicitUrl(message.url, message.referrer, message.suggestedFileName)
      .then(res => {
        console.log("[OmniFetch BG] captureExplicitUrl response from Native Bridge:", res);
        sendResponse({ success: true, data: res });
      })
      .catch(err => {
        console.error("[OmniFetch BG] captureExplicitUrl error:", err);
        sendResponse({ success: false, error: err.message });
      });
    return true;
  }

  if (message.type === "SET_REFRESH_JOB") {
    chrome.storage.local.set({ pendingRefreshJobId: message.jobId }, () => {
      sendResponse({ success: true });
    });
    return true;
  }

  return false;
});

// Clean up states on tab closure
chrome.tabs.onRemoved.addListener((tabId) => {
  tabModifierState.delete(tabId);
  tabStreams.delete(tabId);
  persistSessionStreams();
});

// Reset streams when tab navigates to a new URL
chrome.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (changeInfo.url) {
    console.log(`[OmniFetch BG] Tab ${tabId} navigated to new URL; clearing previous stream cache`);
    tabStreams.delete(tabId);
    persistSessionStreams();
  }
});

// 3. Intercept browser downloads before disk writing begins
chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  (async () => {
    try {
      const config = await chrome.storage.local.get(["captureEnabled", "bypassExtensions", "pendingRefreshJobId"]);
      if (config.captureEnabled === false) {
        return; // Interception globally disabled
      }

      // Check modifier key state for the originating tab
      let altBypass = false;
      let ctrlForce = false;
      if (downloadItem.tabId && tabModifierState.has(downloadItem.tabId)) {
        const mod = tabModifierState.get(downloadItem.tabId);
        if (Date.now() - mod.timestamp < 10000) { // Valid within last 10s
          altBypass = mod.altKey;
          ctrlForce = mod.ctrlKey;
        }
      }

      // ALT Key Override: User requested native browser handling
      if (altBypass && !ctrlForce) {
        console.log("[OmniFetch] Download bypassed by user holding ALT key.");
        return;
      }

      // Check bypass extension list unless CTRL key was held to force capture
      const filename = (downloadItem.filename || "").toLowerCase();
      if (!ctrlForce && config.bypassExtensions && Array.isArray(config.bypassExtensions)) {
        const isBypassed = config.bypassExtensions.some(ext => filename.endsWith(ext.toLowerCase()));
        if (isBypassed) {
          console.log(`[OmniFetch] File '${downloadItem.filename}' skipped per bypass rules.`);
          return;
        }
      }

      // 1. Instantly cancel native browser download
      chrome.downloads.cancel(downloadItem.id, () => {
        if (chrome.runtime.lastError) {
          // Download might have been aborted or completed
        }
      });

      // 2. Extract authentication cookies for target domain
      const targetUrl = downloadItem.finalUrl || downloadItem.url;
      const urlObj = new URL(targetUrl);
      const cookies = await chrome.cookies.getAll({ domain: urlObj.hostname });
      const cookieHeader = cookies && cookies.length > 0
        ? cookies.map(c => `${c.name}=${c.value}`).join("; ")
        : "";

      // 3. Determine if this download is fulfilling an expired link refresh
      let payload;
      if (config.pendingRefreshJobId) {
        payload = {
          action: "refresh_url",
          jobId: config.pendingRefreshJobId,
          newUrl: targetUrl,
          cookies: cookieHeader,
          referrer: downloadItem.referrer || ""
        };
        await chrome.storage.local.remove("pendingRefreshJobId");
        console.log(`[OmniFetch] Refreshing expired URL for job ${payload.jobId}`);
      } else {
        const sanitizedName = sanitizeInterceptedFileName(downloadItem, targetUrl);
        payload = {
          action: "download",
          url: targetUrl,
          referrer: downloadItem.referrer || "",
          cookies: cookieHeader,
          userAgent: navigator.userAgent,
          mimeType: downloadItem.mime || "",
          fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : 0,
          suggestedFileName: sanitizedName
        };
      }

      // 4. Dispatch context payload to macOS Native Messaging Host
      chrome.runtime.sendNativeMessage(NATIVE_HOST, payload, (response) => {
        if (chrome.runtime.lastError) {
          console.error("[OmniFetch] IPC Bridge Error:", chrome.runtime.lastError.message);
          chrome.action.setBadgeText({ text: "ERR" });
          chrome.action.setBadgeBackgroundColor({ color: "#DC2626" });
        } else if (response && response.status === "error") {
          console.warn("[OmniFetch] Bridge reported daemon error:", response.message);
          chrome.action.setBadgeText({ text: "OFF" });
          chrome.action.setBadgeBackgroundColor({ color: "#6B7280" });
        } else {
          console.log("[OmniFetch] Download intercepted and accepted by engine:", response);
          chrome.action.setBadgeText({ text: "OK" });
          chrome.action.setBadgeBackgroundColor({ color: "#16A34A" });
          setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
        }
      });
    } catch (err) {
      console.error("[OmniFetch] Unexpected download interception error:", err);
    }
  })();
});

// 4. Explicit capture helper for context menu or content script requests
async function captureExplicitUrl(url, referrer, suggestedFileName) {
  const urlObj = new URL(url);
  const cookies = await chrome.cookies.getAll({ domain: urlObj.hostname });
  const cookieHeader = cookies && cookies.length > 0
    ? cookies.map(c => `${c.name}=${c.value}`).join("; ")
    : "";

  const payload = {
    action: "download",
    url: url,
    referrer: referrer || "",
    cookies: cookieHeader,
    userAgent: navigator.userAgent,
    suggestedFileName: suggestedFileName || ""
  };

  return new Promise((resolve, reject) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST, payload, (response) => {
      if (chrome.runtime.lastError) {
        reject(new Error(chrome.runtime.lastError.message));
      } else {
        resolve(response);
      }
    });
  });
}

// Helper to sanitize intercepted filenames and eliminate .dat/.bin extensions
function sanitizeInterceptedFileName(downloadItem, targetUrl) {
  let filename = downloadItem.filename || "";
  const lowerName = filename.toLowerCase();
  const lowerUrl = (targetUrl || "").toLowerCase();

  const isDummyExt = lowerName.endsWith(".dat") || lowerName.endsWith(".bin") || lowerName.endsWith(".tmp") || !filename.includes(".");
  const isYouTube = lowerUrl.includes("googlevideo.com") || lowerUrl.includes("videoplayback");

  if (isDummyExt || isYouTube) {
    let preferredExt = ".mp4";
    const mime = (downloadItem.mime || "").toLowerCase();
    if (mime.includes("video/webm") || lowerUrl.includes("mime=video%2fwebm") || lowerUrl.includes("mime=video/webm")) {
      preferredExt = ".webm";
    } else if (mime.includes("audio/mp4") || lowerUrl.includes("mime=audio%2fmp4") || lowerUrl.includes("mime=audio/mp4")) {
      preferredExt = ".m4a";
    } else if (mime.includes("application/pdf")) {
      preferredExt = ".pdf";
    } else if (mime.includes("application/zip")) {
      preferredExt = ".zip";
    }

    if (isYouTube) {
      if (!filename || filename.toLowerCase().includes("videoplayback")) {
        filename = `YouTube_Video${preferredExt}`;
      } else if (lowerName.endsWith(".dat") || lowerName.endsWith(".bin")) {
        filename = filename.replace(/\.(dat|bin)$/i, preferredExt);
      }
    } else if (isDummyExt && preferredExt) {
      filename = filename ? filename.replace(/\.[^.]+$/, preferredExt) : `download${preferredExt}`;
    }
  }

  return filename;
}

// Helper to parse stream metadata, resolutions, formats, and sizes
function parseStreamMetadata(rawUrl) {
  const lowerUrl = rawUrl.toLowerCase();
  let quality = "Original";
  let label = "Video";
  let format = "MP4";
  let sizeBytes = null;
  let sizeFormatted = "";
  let order = 500;
  let itag = "";

  try {
    const u = new URL(rawUrl);
    itag = u.searchParams.get("itag") || "";
    const clen = u.searchParams.get("clen");
    if (clen) {
      sizeBytes = parseInt(clen, 10);
      if (!isNaN(sizeBytes) && sizeBytes > 0) {
        if (sizeBytes >= 1024 * 1024 * 1024) {
          sizeFormatted = (sizeBytes / (1024 * 1024 * 1024)).toFixed(1) + " GB";
        } else {
          sizeFormatted = (sizeBytes / (1024 * 1024)).toFixed(1) + " MB";
        }
      }
    }

    const mime = (u.searchParams.get("mime") || "").toLowerCase();
    if (mime.includes("webm")) format = "WebM";
    else if (mime.includes("mp4")) format = "MP4";
    else if (mime.includes("audio")) format = "M4A";

    switch (itag) {
      case "137":
      case "248":
      case "399":
        quality = "1080p";
        label = "1080p HD";
        order = 1080;
        break;
      case "22":
        quality = "720p";
        label = "720p HD";
        format = "MP4";
        order = 721;
        break;
      case "136":
      case "247":
      case "398":
        quality = "720p";
        label = "720p HD";
        order = 720;
        break;
      case "135":
      case "244":
      case "397":
        quality = "480p";
        label = "480p";
        order = 480;
        break;
      case "18":
        quality = "360p";
        label = "360p";
        format = "MP4";
        order = 361;
        break;
      case "134":
      case "243":
      case "396":
        quality = "360p";
        label = "360p";
        order = 360;
        break;
      case "160":
      case "278":
        quality = "144p";
        label = "144p";
        order = 144;
        break;
      case "140":
        quality = "Audio";
        label = "Audio only (M4A)";
        format = "M4A";
        order = 50;
        break;
      case "251":
        quality = "Audio";
        label = "Audio only (Opus)";
        format = "WebM";
        order = 49;
        break;
      default:
        if (lowerUrl.includes(".m3u8")) {
          quality = "HLS";
          label = "HLS Stream";
          format = "M3U8";
          order = 700;
        } else if (lowerUrl.includes(".mpd")) {
          quality = "DASH";
          label = "DASH Stream";
          format = "MPD";
          order = 600;
        } else if (lowerUrl.includes(".webm")) {
          quality = "WebM";
          label = "WebM Video";
          format = "WebM";
          order = 300;
        } else {
          quality = "Video";
          label = "MP4 Video";
          format = "MP4";
          order = 350;
        }
        break;
    }
  } catch (e) {}

  return {
    url: rawUrl,
    itag,
    quality,
    label,
    format,
    sizeBytes,
    sizeFormatted,
    order
  };
}

// 5. Sniff streaming media playlists (.m3u8, .mpd) and direct video streams (e.g. YouTube googlevideo.com)
chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.url) return;

    const lowerUrl = details.url.toLowerCase();
    let isStream = false;
    let streamUrl = details.url;

    if (lowerUrl.includes(".m3u8") || lowerUrl.includes(".mpd")) {
      isStream = true;
    } else if (lowerUrl.includes("googlevideo.com/videoplayback")) {
      isStream = true;
      try {
        const u = new URL(details.url);
        u.searchParams.delete("range");
        u.searchParams.delete("rn");
        streamUrl = u.toString();
      } catch (e) {
        streamUrl = details.url;
      }
    } else if (details.type === "media" || lowerUrl.includes(".mp4") || lowerUrl.includes(".webm") || lowerUrl.includes(".ts")) {
      isStream = true;
    }

    if (isStream) {
      if (details.tabId >= 0) {
        recordStreamForTab(details.tabId, streamUrl);
      } else {
        // Associate background media pipeline requests with the active focused tab
        try {
          if (chrome.tabs && chrome.tabs.query) {
            chrome.tabs.query({ active: true, lastFocusedWindow: true }, (tabs) => {
              if (tabs && tabs.length > 0 && tabs[0].id) {
                recordStreamForTab(tabs[0].id, streamUrl);
              }
            });
          }
        } catch (e) {}
      }
    }
  },
  { urls: ["<all_urls>"] }
);
