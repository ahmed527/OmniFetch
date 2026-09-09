using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace OmniFetch.Bridge;

/// <summary>
/// Registers the Native Messaging JSON host manifest in macOS browser configuration directories.
/// </summary>
public static class ManifestRegistrar
{
    public const string HostName = "com.omnifetch.bridge";
    public const string HostDescription = "OmniFetch Native Messaging IPC Bridge";

    private static readonly string[] BrowserHostDirectories =
    {
        "Google/Chrome/NativeMessagingHosts",
        "Chromium/NativeMessagingHosts",
        "Microsoft Edge/NativeMessagingHosts",
        "BraveSoftware/Brave-Browser/NativeMessagingHosts",
        "Arc/User Data/NativeMessagingHosts"
    };

    public static List<string> GetTargetManifestPaths()
    {
        string libraryAppSupport = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support");
        var paths = new List<string>();

        foreach (var relDir in BrowserHostDirectories)
        {
            string dir = Path.Combine(libraryAppSupport, relDir);
            paths.Add(Path.Combine(dir, $"{HostName}.json"));
        }

        return paths;
    }

    public static int Register(string? executablePath = null, string? extensionId = null)
    {
        string binaryPath = executablePath ?? Environment.ProcessPath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(binaryPath) || !File.Exists(binaryPath))
        {
            Console.Error.WriteLine($"[Error] Could not locate bridge binary: '{binaryPath}'");
            return 1;
        }

        var allowedOrigins = new List<string>();
        if (!string.IsNullOrWhiteSpace(extensionId))
        {
            string cleanId = extensionId.Trim().TrimEnd('/');
            if (cleanId.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            {
                allowedOrigins.Add($"{cleanId}/");
            }
            else
            {
                allowedOrigins.Add($"chrome-extension://{cleanId}/");
            }
        }
        else
        {
            // Default development extension origin placeholder (Chromium rejects wildcards)
            allowedOrigins.Add("chrome-extension://omnifetch-default-extension-id/");
        }

        var manifest = new NativeHostManifest
        {
            Name = HostName,
            Description = HostDescription,
            Path = binaryPath,
            Type = "stdio",
            AllowedOrigins = allowedOrigins
        };

        string json = JsonSerializer.Serialize(manifest, BridgeJsonContext.Default.NativeHostManifest);
        int successCount = 0;

        foreach (var manifestPath in GetTargetManifestPaths())
        {
            try
            {
                string? parentDir = Path.GetDirectoryName(manifestPath);
                if (parentDir != null)
                {
                    Directory.CreateDirectory(parentDir);
                }

                File.WriteAllText(manifestPath, json);
                Console.WriteLine($"[Registered] {manifestPath}");
                successCount++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Skipped] {manifestPath}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nSuccessfully registered OmniFetch Native Messaging Host in {successCount} browser path(s).");
        return 0;
    }

    public static int Unregister()
    {
        int removedCount = 0;
        foreach (var manifestPath in GetTargetManifestPaths())
        {
            try
            {
                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                    Console.WriteLine($"[Removed] {manifestPath}");
                    removedCount++;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Failed to remove] {manifestPath}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nUnregistered OmniFetch Native Messaging Host from {removedCount} path(s).");
        return 0;
    }
}
