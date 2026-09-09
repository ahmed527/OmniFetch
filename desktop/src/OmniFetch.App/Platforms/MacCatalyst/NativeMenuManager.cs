using System;
using UIKit;

namespace OmniFetch.App.Platforms.MacCatalyst;

/// <summary>
/// Platform coordinator for Mac Catalyst desktop menu interactions and keyboard shortcuts.
/// </summary>
public static class NativeMenuManager
{
    public static void ConfigureMacCatalystWindow(UIWindow? window)
    {
        if (window == null) return;
        
        // Ensure standard window chrome and native title visibility
        if (window.WindowScene?.Titlebar != null)
        {
            window.WindowScene.Titlebar.TitleVisibility = UITitlebarTitleVisibility.Visible;
        }
    }
}
