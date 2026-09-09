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
    captureExplicitUrl(message.url, message.referrer, message.suggestedFileName)
      .then(res => sendResponse({ success: true, data: res }))
      .catch(err => sendResponse({ success: false, error: err.message }));
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

// Clean up modifier states on tab closure
chrome.tabs.onRemoved.addListener((tabId) => {
  tabModifierState.delete(tabId);
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
        payload = {
          action: "download",
          url: targetUrl,
          referrer: downloadItem.referrer || "",
          cookies: cookieHeader,
          userAgent: navigator.userAgent,
          mimeType: downloadItem.mime || "",
          fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : 0,
          suggestedFileName: downloadItem.filename || ""
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

// 5. Sniff streaming media playlists (.m3u8, .mpd)
chrome.webRequest.onBeforeRequest.addListener(
  (details) => {
    if (!details.url || details.tabId < 0) return;

    const lowerUrl = details.url.toLowerCase();
    if (lowerUrl.includes(".m3u8") || lowerUrl.includes(".mpd")) {
      chrome.storage.local.get("videoGrabberEnabled", (cfg) => {
        if (cfg.videoGrabberEnabled === false) return;

        chrome.tabs.sendMessage(details.tabId, {
          type: "STREAM_DETECTED",
          url: details.url,
          isHls: lowerUrl.includes(".m3u8"),
          isDash: lowerUrl.includes(".mpd")
        }).catch(() => {
          // Tab may not have content script injected (e.g. extension page or iframe)
        });
      });
    }
  },
  { urls: ["<all_urls>"], types: ["xmlhttprequest", "other", "media"] }
);
