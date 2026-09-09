// OmniFetch Extension Popup Controller

document.addEventListener("DOMContentLoaded", async () => {
  const toggleCapture = document.getElementById("toggleCapture");
  const toggleVideoGrabber = document.getElementById("toggleVideoGrabber");
  const toggleBypassPdf = document.getElementById("toggleBypassPdf");
  const connectionBadge = document.getElementById("connectionBadge");
  const statusText = document.getElementById("statusText");
  const btnTestConnection = document.getElementById("btnTestConnection");
  const diagnosticsOutput = document.getElementById("diagnosticsOutput");

  // 1. Load saved preferences from chrome.storage.local
  const config = await chrome.storage.local.get(["captureEnabled", "videoGrabberEnabled", "bypassExtensions"]);
  toggleCapture.checked = config.captureEnabled ?? true;
  toggleVideoGrabber.checked = config.videoGrabberEnabled ?? true;

  const bypassList = config.bypassExtensions || [".pdf"];
  toggleBypassPdf.checked = bypassList.includes(".pdf");

  // 2. Attach toggle change listeners
  toggleCapture.addEventListener("change", async () => {
    await chrome.storage.local.set({ captureEnabled: toggleCapture.checked });
  });

  toggleVideoGrabber.addEventListener("change", async () => {
    await chrome.storage.local.set({ videoGrabberEnabled: toggleVideoGrabber.checked });
  });

  toggleBypassPdf.addEventListener("change", async () => {
    let currentBypass = (await chrome.storage.local.get("bypassExtensions")).bypassExtensions || [".pdf"];
    if (toggleBypassPdf.checked) {
      if (!currentBypass.includes(".pdf")) currentBypass.push(".pdf");
    } else {
      currentBypass = currentBypass.filter(ext => ext !== ".pdf");
    }
    await chrome.storage.local.set({ bypassExtensions: currentBypass });
  });

  // 3. Ping daemon on popup open
  async function checkDaemonHealth() {
    connectionBadge.className = "status-badge status-checking";
    statusText.textContent = "Checking...";

    const startTime = performance.now();
    try {
      const p = chrome.runtime.sendMessage({ type: "PING_DAEMON" }, (res) => {
        if (chrome.runtime?.lastError) {
          connectionBadge.className = "status-badge status-offline";
          statusText.textContent = "Daemon Offline";
          diagnosticsOutput.textContent = `Offline: ${chrome.runtime.lastError.message || "Native host unavailable"}`;
          diagnosticsOutput.className = "diagnostics-text diag-warning";
          return;
        }

        const elapsed = Math.round(performance.now() - startTime);

        if (res && res.success && res.data && res.data.status === "ok") {
          connectionBadge.className = "status-badge status-connected";
          statusText.textContent = "Connected";
          diagnosticsOutput.textContent = `OmniFetch daemon online (${elapsed} ms latency)`;
          diagnosticsOutput.className = "diagnostics-text diag-success";
        } else {
          connectionBadge.className = "status-badge status-offline";
          statusText.textContent = "Daemon Offline";
          const msg = res && res.error ? res.error : "Desktop app not running";
          diagnosticsOutput.textContent = `Offline: ${msg}`;
          diagnosticsOutput.className = "diagnostics-text diag-warning";
        }
      });
      if (p && typeof p.catch === "function") {
        p.catch(() => {});
      }
    } catch (err) {}
  }

  btnTestConnection.addEventListener("click", () => {
    checkDaemonHealth();
  });

  // Initial ping check
  checkDaemonHealth();
});
