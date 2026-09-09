using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
#if MACCATALYST
using Microsoft.Maui.ApplicationModel;
using UIKit;
using UniformTypeIdentifiers;
#endif

namespace OmniFetch.App.Services;

/// <summary>
/// Cross-platform helper for native folder picking and macOS Finder navigation.
/// </summary>
public static class FolderPickerHelper
{
    /// <summary>
    /// Prompts the user with a native macOS directory picker dialog.
    /// Returns the selected absolute directory path, or null if cancelled.
    /// </summary>
    public static async Task<string?> PickFolderAsync(string? initialDirectory = null)
    {
#if MACCATALYST
        try
        {
            var tcs = new TaskCompletionSource<string?>();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                try
                {
                    var picker = new UIDocumentPickerViewController(new[] { UTTypes.Folder }, asCopy: false)
                    {
                        AllowsMultipleSelection = false
                    };

                    picker.DidPickDocumentAtUrls += (sender, args) =>
                    {
                        if (args.Urls != null && args.Urls.Length > 0)
                        {
                            string path = args.Urls[0].Path ?? string.Empty;
                            tcs.TrySetResult(string.IsNullOrWhiteSpace(path) ? null : path);
                        }
                        else
                        {
                            tcs.TrySetResult(null);
                        }
                    };

                    picker.WasCancelled += (sender, args) =>
                    {
                        tcs.TrySetResult(null);
                    };

                    var window = UIApplication.SharedApplication.ConnectedScenes
                        .OfType<UIWindowScene>()
                        .SelectMany(s => s.Windows)
                        .FirstOrDefault(w => w.IsKeyWindow);

                    var rootVc = window?.RootViewController;
                    while (rootVc?.PresentedViewController != null)
                    {
                        rootVc = rootVc.PresentedViewController;
                    }

                    if (rootVc != null)
                    {
                        rootVc.PresentViewController(picker, true, null);
                    }
                    else
                    {
                        tcs.TrySetResult(null);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[FolderPickerHelper] Error presenting folder picker: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });

            return await tcs.Task;
        }
        catch
        {
            return null;
        }
#else
        await Task.CompletedTask;
        return initialDirectory;
#endif
    }

    /// <summary>
    /// Reveals a downloaded file in macOS Finder.
    /// </summary>
    public static void RevealInFinder(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;

        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-R \"{filePath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                {
                    OpenFolder(dir);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderPickerHelper] Could not reveal in Finder: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the specified directory in macOS Finder.
    /// </summary>
    public static void OpenFolder(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return;

        try
        {
            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderPickerHelper] Could not open folder in Finder: {ex.Message}");
        }
    }

    /// <summary>
    /// Launches the downloaded file with its default system handler.
    /// </summary>
    public static void OpenFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"\"{filePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FolderPickerHelper] Could not open file: {ex.Message}");
        }
    }
}
