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
            });

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

        // 2. Register Core Singletons
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
