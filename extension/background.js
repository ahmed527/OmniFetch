// OmniFetch Chrome Extension (Manifest V3)
// Intercepts browser downloads and dispatches download context to com.omnifetch.bridge

const NATIVE_HOST = "com.omnifetch.bridge";

chrome.downloads.onDeterminingFilename.addListener((downloadItem, suggest) => {
  // 1. Instantly cancel native browser download
  chrome.downloads.cancel(downloadItem.id, () => {
    if (chrome.runtime.lastError) {
      // Download may have already completed or been cancelled
    }
  });

  const targetUrl = downloadItem.finalUrl || downloadItem.url;
  let urlObj;
  try {
    urlObj = new URL(targetUrl);
  } catch (e) {
    console.error("Invalid download URL:", targetUrl, e);
    return;
  }

  // 2. Extract authentication cookies for target domain
  chrome.cookies.getAll({ domain: urlObj.hostname }, (cookies) => {
    const cookieHeader = cookies && cookies.length > 0 
      ? cookies.map(c => `${c.name}=${c.value}`).join("; ") 
      : "";

    const payload = {
      action: "new_download",
      url: targetUrl,
      referrer: downloadItem.referrer || "",
      cookies: cookieHeader,
      userAgent: navigator.userAgent,
      mimeType: downloadItem.mime || "",
      fileSize: downloadItem.fileSize > 0 ? downloadItem.fileSize : 0,
      suggestedFileName: downloadItem.filename || ""
    };

    // 3. Dispatch context to macOS Native Messaging Host
    chrome.runtime.sendNativeMessage(NATIVE_HOST, payload, (response) => {
      if (chrome.runtime.lastError) {
        console.error("OmniFetch IPC Bridge Error:", chrome.runtime.lastError.message);
      } else {
        console.log("OmniFetch Bridge Response:", response);
      }
    });
  });
});
