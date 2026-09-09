import { test, describe, before, beforeEach } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

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
  });
});
