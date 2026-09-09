using Microsoft.Maui;
using Microsoft.Maui.Controls;

namespace OmniFetch.App;

public partial class App : Application
{
    private readonly Views.MainPage _mainPage;

    public App(Views.MainPage mainPage)
    {
        InitializeComponent();
        _mainPage = mainPage;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new NavigationPage(_mainPage))
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
