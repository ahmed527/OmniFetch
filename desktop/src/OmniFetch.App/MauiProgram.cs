using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Controls.Hosting;
using Microsoft.Maui.Hosting;
using OmniFetch.App.Platforms.MacCatalyst;
using OmniFetch.App.ViewModels;
using OmniFetch.App.Views;
using OmniFetch.Core.Engine;
using OmniFetch.Core.Ipc;
using OmniFetch.Core.Logging;
using OmniFetch.Core.Media;
using OmniFetch.Core.Persistence;

namespace OmniFetch.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            })
            .ConfigureMauiHandlers(handlers =>
            {
#if MACCATALYST
                Microsoft.Maui.Handlers.ButtonHandler.Mapper.AppendToMapping("MacCatalystButtonContrastFix", (handler, view) =>
                {
                    if (handler.PlatformView is UIKit.UIButton uiButton && view is Button mauiButton)
                    {
                        var bg = mauiButton.BackgroundColor;
                        var text = mauiButton.TextColor ?? Microsoft.Maui.Graphics.Colors.Black;
                        var platformText = Microsoft.Maui.Platform.ColorExtensions.ToPlatform(text);
                        var platformBg = (bg != null && bg != Microsoft.Maui.Graphics.Colors.Transparent)
                            ? Microsoft.Maui.Platform.ColorExtensions.ToPlatform(bg)
                            : null;

                        if (platformBg != null)
                        {
                            var config = UIKit.UIButtonConfiguration.FilledButtonConfiguration;
                            config.BaseBackgroundColor = platformBg;
                            config.BaseForegroundColor = platformText;
                            config.CornerStyle = UIKit.UIButtonConfigurationCornerStyle.Fixed;
                            config.Background.CornerRadius = (System.Runtime.InteropServices.NFloat)Math.Max(4, mauiButton.CornerRadius);

                            if (mauiButton.BorderWidth > 0 && mauiButton.BorderColor != null && mauiButton.BorderColor != Microsoft.Maui.Graphics.Colors.Transparent)
                            {
                                config.Background.StrokeWidth = (System.Runtime.InteropServices.NFloat)mauiButton.BorderWidth;
                                config.Background.StrokeColor = Microsoft.Maui.Platform.ColorExtensions.ToPlatform(mauiButton.BorderColor);
                            }

                            if (!string.IsNullOrEmpty(mauiButton.Text))
                            {
                                config.Title = mauiButton.Text;
                            }

                            uiButton.Configuration = config;
                            uiButton.BackgroundColor = platformBg;
                        }
                        else
                        {
                            var config = UIKit.UIButtonConfiguration.PlainButtonConfiguration;
                            config.BaseForegroundColor = platformText;
                            if (!string.IsNullOrEmpty(mauiButton.Text))
                            {
                                config.Title = mauiButton.Text;
                            }
                            uiButton.Configuration = config;
                        }

                        // Maintain high-contrast legible title across ALL control states (Normal, Highlighted, Disabled, Selected, Focused)
                        uiButton.ConfigurationUpdateHandler = (btn) =>
                        {
                            var activeConfig = btn.Configuration ?? (platformBg != null 
                                ? UIKit.UIButtonConfiguration.FilledButtonConfiguration 
                                : UIKit.UIButtonConfiguration.PlainButtonConfiguration);

                            activeConfig.BaseForegroundColor = platformText;
                            if (platformBg != null)
                            {
                                activeConfig.BaseBackgroundColor = platformBg;
                            }
                            if (!string.IsNullOrEmpty(mauiButton.Text))
                            {
                                activeConfig.Title = mauiButton.Text;
                            }
                            btn.Configuration = activeConfig;
                        };

                        try
                        {
                            uiButton.SetTitleColor(platformText, UIKit.UIControlState.Normal);
                            if (!string.IsNullOrEmpty(mauiButton.Text))
                            {
                                uiButton.SetTitle(mauiButton.Text, UIKit.UIControlState.Normal);
                            }
                        }
                        catch
                        {
                            // In Mac Catalyst Mac Idiom, UIButtonConfiguration handles state styling
                        }
                    }
                });
#endif
            });

        // Register production & development file logger
        builder.Logging.AddOmniFetchFileLogging();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        // 1. Persistence & SQLite Setup
        var dbOptions = OmniFetchDbContext.CreateOptions();
        using (var initContext = new OmniFetchDbContext(dbOptions))
        {
            initContext.Database.EnsureCreated();
        }

        var repository = new DownloadRepository(dbOptions);
        var writeBehindService = new WriteBehindService(repository);
        var engine = new DownloadEngine(repository: repository, writeBehindService: writeBehindService);
        var ipcServer = new UnixDomainSocketServer();
        var activityLockService = new MacActivityLockService();
        var settingsService = new OmniFetch.Core.Settings.SettingsService();

        // 2. Register Core Singletons
        builder.Services.AddSingleton<OmniFetch.Core.Settings.ISettingsService>(settingsService);
        builder.Services.AddSingleton<IDownloadRepository>(repository);
        builder.Services.AddSingleton<IWriteBehindService>(writeBehindService);
        builder.Services.AddSingleton<IDownloadEngine>(engine);
        builder.Services.AddSingleton<IIpcServer>(ipcServer);
        builder.Services.AddSingleton<IActivityLockService>(activityLockService);

        // Media & HLS Stream Services
        builder.Services.AddSingleton<IHlsParser, HlsParser>();
        builder.Services.AddSingleton<IFFmpegLocator, FFmpegLocator>();
        builder.Services.AddSingleton<IHlsSegmentDownloader, HlsSegmentDownloader>();
        builder.Services.AddSingleton<IHlsVideoAssembler, HlsVideoAssembler>();
        builder.Services.AddSingleton<IHlsDownloadManager, HlsDownloadManager>();

        // 3. Register ViewModels and Views
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}
