using System;
using Microsoft.Extensions.DependencyInjection;
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
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var mainPage = _services.GetRequiredService<Views.MainPage>();
        var window = new Window(new NavigationPage(mainPage))
        {
            Title = "Internet Download Manager (OmniFetch for Mac)",
            Width = 1100,
            Height = 720,
            MinimumWidth = 800,
            MinimumHeight = 500
        };
        return window;
    }
}
