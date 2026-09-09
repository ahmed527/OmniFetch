using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace OmniFetch.App;

public partial class App : Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;

        // Wire global crash and unhandled exception logging
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var logger = _services.GetService<ILogger<App>>();
            logger?.LogCritical(e.ExceptionObject as Exception, "FATAL: Unhandled AppDomain exception encountered");
        };

        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            var logger = _services.GetService<ILogger<App>>();
            logger?.LogError(e.Exception, "Unobserved Task exception encountered");
            e.SetObserved();
        };

        var startupLogger = _services.GetService<ILogger<App>>();
        startupLogger?.LogInformation("OmniFetch Desktop Application initialized successfully");
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var mainPage = _services.GetRequiredService<Views.MainPage>();
        NavigationPage.SetHasNavigationBar(mainPage, false);
        var navPage = new NavigationPage(mainPage);
        NavigationPage.SetHasNavigationBar(navPage, false);
        var window = new Window(navPage)
        {
            Title = "OmniFetch",
            Width = 1100,
            Height = 720,
            MinimumWidth = 800,
            MinimumHeight = 500
        };
        return window;
    }
}
