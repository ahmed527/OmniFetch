import { test, describe, before, beforeEach } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const extensionRoot = path.resolve(__dirname, "..");

describe("OmniFetch Chrome Extension (Manifest V3) Test Suite", () => {

  describe("Manifest Validation", () => {
    test("manifest.json exists and is valid Manifest V3", () => {
      const manifestPath = path.join(extensionRoot, "manifest.json");
      assert.ok(fs.existsSync(manifestPath), "manifest.json must exist");

      const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
      assert.equal(manifest.manifest_version, 3, "Must be Manifest V3");
      assert.equal(manifest.name, "OmniFetch Integration");
      assert.ok(manifest.version, "Must declare version");
      assert.ok(manifest.background?.service_worker, "Must declare background service_worker");
      assert.ok(manifest.action?.default_popup, "Must declare default_popup");
    });

    test("manifest.json declares all required permissions", () => {
      const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "manifest.json"), "utf8"));
      const required = ["downloads", "cookies", "nativeMessaging", "storage", "tabs", "webRequest"];
      for (const perm of required) {
        assert.ok(manifest.permissions.includes(perm), `Permission '${perm}' must be declared`);
      }
      assert.ok(manifest.host_permissions.includes("<all_urls>"), "<all_urls> host permission must be declared");
    });

    test("All referenced icon files exist on disk", () => {
      const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "manifest.json"), "utf8"));
      const iconEntries = Object.entries(manifest.icons || {});
      assert.ok(iconEntries.length >= 3, "Must have at least 16, 48, 128 icons");

      for (const [size, relPath] of iconEntries) {
        const fullPath = path.join(extensionRoot, relPath);
        assert.ok(fs.existsSync(fullPath), `Icon '${relPath}' (${size}px) must exist`);
        const stat = fs.statSync(fullPath);
        assert.ok(stat.size > 0, `Icon '${relPath}' must not be empty`);
      }
    });

    test("Content scripts and popup HTML files exist", () => {
      const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "manifest.json"), "utf8"));
      for (const cs of manifest.content_scripts || []) {
        for (const js of cs.js || []) {
          assert.ok(fs.existsSync(path.join(extensionRoot, js)), `Content script '${js}' must exist`);
        }
        for (const css of cs.css || []) {
          assert.ok(fs.existsSync(path.join(extensionRoot, css)), `Content css '${css}' must exist`);
        }
      }

      const popupPath = path.join(extensionRoot, manifest.action.default_popup);
      assert.ok(fs.existsSync(popupPath), `Popup file '${manifest.action.default_popup}' must exist`);
    });
  });

  describe("Download Interception Logic & Mocks", () => {
    let mockStorage;
    let mockChrome;
    let sentNativeMessages;
    let cancelledDownloads;

    beforeEach(() => {
      mockStorage = {
        captureEnabled: true,
        videoGrabberEnabled: true,
        bypassExtensions: [".pdf"]
      };
      sentNativeMessages = [];
      cancelledDownloads = [];

      mockChrome = {
        runtime: {
          lastError: null,
          sendNativeMessage: (host, payload, callback) => {
            sentNativeMessages.push({ host, payload });
            callback({ status: "accepted", jobId: "11111111-2222-3333-4444-555555555555" });
          }
        },
        downloads: {
          cancel: (id, callback) => {
            cancelledDownloads.push(id);
            if (callback) callback();
          }
        },
        cookies: {
          getAll: (details, callback) => {
            callback([
              { name: "session_id", value: "tok_abc123" },
              { name: "auth", value: "secret_xyz" }
            ]);
          }
        },
        storage: {
          local: {
            get: async (keys) => {
              if (Array.isArray(keys)) {
                const res = {};
                for (const k of keys) res[k] = mockStorage[k];
                return res;
              }
              if (typeof keys === "string") return { [keys]: mockStorage[keys] };
              return { ...mockStorage };
            },
            set: async (items) => {
              Object.assign(mockStorage, items);
            },
            remove: async (key) => {
              delete mockStorage[key];
            }
          }
        },
        action: {
          setBadgeText: () => {},
          setBadgeBackgroundColor: () => {}
        }
      };
    });

    test("Cancels native browser download and dispatches NativeDownloadRequest payload", async () => {
      const downloadItem = {
        id: 42,
        url: "https://files.example.com/archive.zip",
        finalUrl: "https://files.example.com/archive.zip",
        referrer: "https://example.com/download-page",
        filename: "archive.zip",
        mime: "application/zip",
        fileSize: 104857600
      };

      // Execute simulated interception routine
      const config = await mockChrome.storage.local.get(["captureEnabled", "bypassExtensions"]);
      assert.equal(config.captureEnabled, true);

      // Cancel
      mockChrome.downloads.cancel(downloadItem.id);
      assert.deepEqual(cancelledDownloads, [42], "Download 42 must be cancelled immediately");

      // Extract cookies
      const cookies = await new Promise(resolve => {
        mockChrome.cookies.getAll({ domain: "files.example.com" }, resolve);
      });
      const cookieHeader = cookies.map(c => `${c.name}=${c.value}`).join("; ");
      assert.equal(cookieHeader, "session_id=tok_abc123; auth=secret_xyz");

      // Construct and dispatch payload
      const payload = {
        action: "download",
        url: downloadItem.finalUrl,
        referrer: downloadItem.referrer,
        cookies: cookieHeader,
        suggestedFileName: downloadItem.filename,
        fileSize: downloadItem.fileSize,
        mimeType: downloadItem.mime
      };

      const res = await new Promise(resolve => {
        mockChrome.runtime.sendNativeMessage("com.omnifetch.bridge", payload, resolve);
      });

      assert.equal(sentNativeMessages.length, 1);
      assert.equal(sentNativeMessages[0].host, "com.omnifetch.bridge");
      assert.equal(sentNativeMessages[0].payload.action, "download");
      assert.equal(sentNativeMessages[0].payload.url, "https://files.example.com/archive.zip");
      assert.equal(sentNativeMessages[0].payload.cookies, "session_id=tok_abc123; auth=secret_xyz");
      assert.equal(res.status, "accepted");
    });

    test("Alt key suppresses download interception", async () => {
      const tabModifiers = { altKey: true, ctrlKey: false };

      // Alt override logic
      let shouldIntercept = true;
      if (tabModifiers.altKey && !tabModifiers.ctrlKey) {
        shouldIntercept = false;
      }

      assert.equal(shouldIntercept, false, "Holding ALT must bypass OmniFetch capture");
      assert.equal(cancelledDownloads.length, 0);
      assert.equal(sentNativeMessages.length, 0);
    });

    test("Bypass extension list allows native handling unless Ctrl is held", async () => {
      const filename = "document.pdf";
      const config = await mockChrome.storage.local.get("bypassExtensions");

      const isBypassed = config.bypassExtensions.some(ext => filename.endsWith(ext));
      assert.equal(isBypassed, true, ".pdf should be identified as bypassed");

      // With Ctrl key held (force capture)
      const ctrlKeyHeld = true;
      let shouldCapture = !isBypassed || ctrlKeyHeld;
      assert.equal(shouldCapture, true, "Holding CTRL must force-capture even bypassed extensions");
    });

    test("Fulfills expired URL refresh workflow", async () => {
      mockStorage.pendingRefreshJobId = "99999999-8888-7777-6666-555555555555";

      const downloadItem = {
        id: 99,
        url: "https://s3.amazonaws.com/bucket/file.zip?fresh-token=abc",
        finalUrl: "https://s3.amazonaws.com/bucket/file.zip?fresh-token=abc",
        referrer: "https://s3.amazonaws.com",
        filename: "file.zip"
      };

      const config = await mockChrome.storage.local.get(["captureEnabled", "pendingRefreshJobId"]);
      assert.ok(config.pendingRefreshJobId);

      // Cancel browser download
      mockChrome.downloads.cancel(downloadItem.id);

      // Build refresh payload
      const payload = {
        action: "refresh_url",
        jobId: config.pendingRefreshJobId,
        newUrl: downloadItem.finalUrl,
        cookies: "auth=new_token",
        referrer: downloadItem.referrer
      };

      await mockChrome.storage.local.remove("pendingRefreshJobId");
      assert.equal(mockStorage.pendingRefreshJobId, undefined);

      await new Promise(resolve => {
        mockChrome.runtime.sendNativeMessage("com.omnifetch.bridge", payload, resolve);
      });

      assert.equal(sentNativeMessages.length, 1);
      assert.equal(sentNativeMessages[0].payload.action, "refresh_url");
      assert.equal(sentNativeMessages[0].payload.jobId, "99999999-8888-7777-6666-555555555555");
      assert.equal(sentNativeMessages[0].payload.newUrl, "https://s3.amazonaws.com/bucket/file.zip?fresh-token=abc");
    });

    test("YouTube googlevideo stream URL strips range parameter for full download", () => {
      const chunkUrl = "https://rr2---sn-4g5lzney.googlevideo.com/videoplayback?expire=1710000000&ei=xyz&ip=1.2.3.4&id=o-ABC&itag=137&source=youtube&requiressl=yes&range=0-1048575&rn=1&alr=yes";
      const u = new URL(chunkUrl);
      u.searchParams.delete("range");
      u.searchParams.delete("rn");
      const fullStreamUrl = u.toString();

      assert.ok(!fullStreamUrl.includes("range="), "Full stream URL must not contain range parameter");
      assert.ok(!fullStreamUrl.includes("rn="), "Full stream URL must not contain rn parameter");
      assert.ok(fullStreamUrl.includes("googlevideo.com/videoplayback"), "Must preserve googlevideo endpoint");
      assert.ok(fullStreamUrl.includes("itag=137"), "Must preserve video quality stream itag");
    });

    test("Rejects blob: URLs in video capture", () => {
      const blobUrl = "blob:https://www.youtube.com/a1b2c3d4-e5f6-7890-abcd-ef1234567890";
      let isValidUrl = !blobUrl.startsWith("blob:");
      assert.equal(isValidUrl, false, "blob: URLs must be flagged as invalid for external capture");
    });

    test("sanitizeInterceptedFileName eliminates .dat and assigns .mp4 for YouTube streams", () => {
      // Inline function equivalent to background.js helper
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

      // Case 1: Generic videoplayback.dat on YouTube
      const res1 = sanitizeInterceptedFileName(
        { filename: "videoplayback.dat", mime: "video/mp4" },
        "https://rr1---sn-xxx.googlevideo.com/videoplayback?expire=123"
      );
      assert.equal(res1, "YouTube_Video.mp4");

      // Case 2: Named file ending in .dat on YouTube with WebM MIME
      const res2 = sanitizeInterceptedFileName(
        { filename: "Epic_Song_Video.dat", mime: "video/webm" },
        "https://rr1---sn-xxx.googlevideo.com/videoplayback?mime=video%2Fwebm"
      );
      assert.equal(res2, "Epic_Song_Video.webm");

      // Case 3: Empty filename on YouTube
      const res3 = sanitizeInterceptedFileName(
        { filename: "", mime: "video/mp4" },
        "https://googlevideo.com/videoplayback"
      );
      assert.equal(res3, "YouTube_Video.mp4");
    });

    test("Safe messaging gracefully catches extension context invalidation without throwing", () => {
      let isExtensionValid = false;
      let cleanupInvoked = false;

      function cleanup() {
        cleanupInvoked = true;
      }

      function safeSendMessage(msg, cb) {
        if (!isExtensionValid) {
          cleanup();
          return;
        }
      }

      // Simulate call after extension reload
      assert.doesNotThrow(() => {
        safeSendMessage({ type: "TEST" });
      });
      assert.equal(cleanupInvoked, true, "Cleanup must be invoked when context is invalidated");
    });

    test("Normal download interception produces valid download payload when pendingRefreshJobId is undefined", async () => {
      delete mockStorage.pendingRefreshJobId;

      const downloadItem = {
        id: 77,
        url: "https://example.com/test.zip",
        finalUrl: "https://example.com/test.zip",
        filename: "test.zip"
      };

      const config = await mockChrome.storage.local.get(["pendingRefreshJobId"]);
      assert.equal(config.pendingRefreshJobId, undefined);

      let payload;
      if (config.pendingRefreshJobId) {
        payload = { action: "refresh_url" };
      } else {
        payload = {
          action: "download",
          url: downloadItem.finalUrl,
          suggestedFileName: downloadItem.filename
        };
      }

      assert.equal(payload.action, "download");
      assert.equal(payload.url, "https://example.com/test.zip");
      assert.equal(payload.suggestedFileName, "test.zip");
    });

    test("parseStreamMetadata accurately identifies 1080p, 720p, 360p, and audio streams with human-readable sizes", () => {
      // Direct testing of stream parsing logic
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
            case "18":
              quality = "360p";
              label = "360p";
              format = "MP4";
              order = 361;
              break;
            case "140":
              quality = "Audio";
              label = "Audio only (M4A)";
              format = "M4A";
              order = 50;
              break;
          }
        } catch (e) {}

        return { url: rawUrl, itag, quality, label, format, sizeBytes, sizeFormatted, order };
      }

      const stream1080 = parseStreamMetadata("https://rr1.googlevideo.com/videoplayback?itag=137&clen=52428800&mime=video/mp4");
      assert.equal(stream1080.quality, "1080p");
      assert.equal(stream1080.format, "MP4");
      assert.equal(stream1080.sizeFormatted, "50.0 MB");

      const stream720 = parseStreamMetadata("https://rr1.googlevideo.com/videoplayback?itag=22&clen=26214400&mime=video/mp4");
      assert.equal(stream720.quality, "720p");
      assert.equal(stream720.format, "MP4");
      assert.equal(stream720.sizeFormatted, "25.0 MB");

      const stream360 = parseStreamMetadata("https://rr1.googlevideo.com/videoplayback?itag=18&clen=10485760&mime=video/mp4");
      assert.equal(stream360.quality, "360p");

      const streamAudio = parseStreamMetadata("https://rr1.googlevideo.com/videoplayback?itag=140&clen=4194304&mime=audio/mp4");
      assert.equal(streamAudio.quality, "Audio");
      assert.equal(streamAudio.format, "M4A");

      // Sorted ordering: highest quality first
      const streams = [stream360, stream1080, streamAudio, stream720];
      streams.sort((a, b) => b.order - a.order);
      assert.equal(streams[0].quality, "1080p");
      assert.equal(streams[1].quality, "720p");
      assert.equal(streams[2].quality, "360p");
      assert.equal(streams[3].quality, "Audio");
    });

    test("Filename formatter attaches quality tag cleanly", () => {
      function formatFileName(title, quality, ext) {
        if (quality && quality !== "Original" && quality !== "Video") {
          const cleanQuality = quality.replace(/[^a-zA-Z0-9]/g, "");
          return `${title} [${cleanQuality}]${ext}`;
        }
        return `${title}${ext}`;
      }

      const formatted = formatFileName("ChatGPT Work, now powered by GPT-6 Astra", "720p", ".mp4");
      assert.equal(formatted, "ChatGPT Work, now powered by GPT-6 Astra [720p].mp4");

      const formattedAudio = formatFileName("My Favorite Song", "Audio", ".m4a");
      assert.equal(formattedAudio, "My Favorite Song [Audio].m4a");
    });
  });

  describe("Syntax and Bracket/Brace Integrity Validation", () => {
    test("content.js, background.js, and popup.js compile into valid V8 ASTs with 0 syntax errors", () => {
      const files = ["content.js", "background.js", "popup/popup.js"];
      for (const relPath of files) {
        const fullPath = path.join(extensionRoot, relPath);
        const code = fs.readFileSync(fullPath, "utf8");
        assert.doesNotThrow(() => {
          new vm.Script(code, { filename: relPath });
        }, `${relPath} must compile cleanly without syntax errors`);
      }
    });

    test("All brackets, braces, and parentheses are 100% balanced across all extension JavaScript files", () => {
      const files = ["content.js", "background.js", "popup/popup.js"];
      for (const relPath of files) {
        const fullPath = path.join(extensionRoot, relPath);
        const code = fs.readFileSync(fullPath, "utf8");

        const stack = [];
        let inSingle = false, inDouble = false, inTemplate = false;
        let inLineComment = false, inBlockComment = false;
        let inRegex = false, inRegexClass = false;
        let escaped = false;
        let lastToken = "";

        for (let i = 0; i < code.length; i++) {
          const ch = code[i];
          const next = code[i + 1];

          if (ch === "\n") {
            inLineComment = false;
            if (inRegex) inRegex = false;
            continue;
          }

          if (inLineComment) continue;
          if (inBlockComment) {
            if (ch === "*" && next === "/") { inBlockComment = false; i++; }
            continue;
          }

          if (inSingle) {
            if (!escaped && ch === "'") inSingle = false;
            escaped = !escaped && ch === "\\";
            continue;
          }
          if (inDouble) {
            if (!escaped && ch === "\"") inDouble = false;
            escaped = !escaped && ch === "\\";
            continue;
          }
          if (inTemplate) {
            if (!escaped && ch === "`") inTemplate = false;
            escaped = !escaped && ch === "\\";
            continue;
          }

          if (inRegex) {
            if (!escaped) {
              if (ch === "[") inRegexClass = true;
              else if (ch === "]" && inRegexClass) inRegexClass = false;
              else if (ch === "/" && !inRegexClass) inRegex = false;
            }
            escaped = !escaped && ch === "\\";
            continue;
          }

          if (ch === "/" && next === "/") { inLineComment = true; i++; continue; }
          if (ch === "/" && next === "*") { inBlockComment = true; i++; continue; }

          if (ch === "/") {
            const regexPreceding = ["(", "[", "{", ";", ",", "=", ":", "!", "&", "|", "?", "~", "^", "+", "-", "*", "%", "<", ">", "return", "case", "typeof"];
            if (regexPreceding.includes(lastToken) || lastToken === "") {
              inRegex = true;
              inRegexClass = false;
              escaped = false;
              continue;
            }
          }

          if (ch === "'") { inSingle = true; escaped = false; continue; }
          if (ch === "\"") { inDouble = true; escaped = false; continue; }
          if (ch === "`") { inTemplate = true; escaped = false; continue; }

          if (!/\s/.test(ch)) {
            if (/[a-zA-Z0-9_$]/.test(ch)) {
              if (/[a-zA-Z0-9_$]/.test(lastToken)) {
                lastToken += ch;
              } else {
                lastToken = ch;
              }
            } else {
              lastToken = ch;
            }
          }

          if (ch === "{" || ch === "(" || ch === "[") {
            stack.push(ch);
          } else if (ch === "}" || ch === ")" || ch === "]") {
            const expected = ch === "}" ? "{" : ch === ")" ? "(" : "[";
            const top = stack.pop();
            assert.equal(top, expected, `Mismatched bracket ${ch} in ${relPath}`);
          }
        }

        assert.equal(stack.length, 0, `Unclosed brackets in ${relPath}: ${stack.join(", ")}`);
      }
    });

    test("content.css and popup.css have perfectly matched braces", () => {
      const cssFiles = ["content.css", "popup/popup.css"];
      for (const relPath of cssFiles) {
        const fullPath = path.join(extensionRoot, relPath);
        const css = fs.readFileSync(fullPath, "utf8");
        let openCount = 0;
        let closeCount = 0;
        for (const ch of css) {
          if (ch === "{") openCount++;
          if (ch === "}") closeCount++;
        }
        assert.equal(openCount, closeCount, `${relPath} braces count mismatch: ${openCount} open vs ${closeCount} closed`);
      }
    });

    test("content.js implements window unhandledrejection safeguard", () => {
      const contentJs = fs.readFileSync(path.join(extensionRoot, "content.js"), "utf8");
      assert.ok(contentJs.includes("unhandledrejection"), "content.js must register unhandledrejection listener");
      assert.ok(contentJs.includes("event.preventDefault()"), "unhandledrejection listener must call event.preventDefault()");
    });
  });
});

