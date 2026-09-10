using System;
using System.Windows.Input;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace OmniFetch.App.Views.Controls;

public partial class ToolbarButton : ContentView
{
    private DateTime _lastClickTime = DateTime.MinValue;

    public static readonly BindableProperty TextProperty =
        BindableProperty.Create(
            nameof(Text), 
            typeof(string), 
            typeof(ToolbarButton), 
            string.Empty,
            propertyChanged: (bindable, oldValue, newValue) =>
            {
                if (bindable is ToolbarButton btn && btn.BtnLabel != null)
                {
                    btn.BtnLabel.Text = (string)newValue;
                }
            });

    public static readonly BindableProperty IconSourceProperty =
        BindableProperty.Create(
            nameof(IconSource), 
            typeof(ImageSource), 
            typeof(ToolbarButton), 
            null,
            propertyChanged: (bindable, oldValue, newValue) =>
            {
                if (bindable is ToolbarButton btn && btn.BtnImage != null)
                {
                    btn.BtnImage.Source = (ImageSource?)newValue;
                }
            });

    public static readonly BindableProperty CommandProperty =
        BindableProperty.Create(
            nameof(Command), 
            typeof(ICommand), 
            typeof(ToolbarButton), 
            null);

    public static readonly BindableProperty CommandParameterProperty =
        BindableProperty.Create(
            nameof(CommandParameter), 
            typeof(object), 
            typeof(ToolbarButton), 
            null);

    public static readonly BindableProperty ButtonWidthProperty =
        BindableProperty.Create(
            nameof(ButtonWidth),
            typeof(double),
            typeof(ToolbarButton),
            76.0,
            propertyChanged: (bindable, oldValue, newValue) =>
            {
                if (bindable is ToolbarButton btn && btn.RootGrid != null)
                {
                    btn.RootGrid.WidthRequest = (double)newValue;
                }
            });

    public event EventHandler? Clicked;

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double ButtonWidth
    {
        get => (double)GetValue(ButtonWidthProperty);
        set => SetValue(ButtonWidthProperty, value);
    }

    public ImageSource? IconSource
    {
        get => (ImageSource?)GetValue(IconSourceProperty);
        set => SetValue(IconSourceProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public ToolbarButton()
    {
        InitializeComponent();
    }

    private void OnNativeButtonClicked(object? sender, EventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [TOOLBAR] Native click for '{Text}'\n");
        }
        catch { }

        var now = DateTime.UtcNow;
        if ((now - _lastClickTime).TotalMilliseconds < 100) return;
        _lastClickTime = now;

        Clicked?.Invoke(this, EventArgs.Empty);

        if (Command != null)
        {
            if (Command.CanExecute(CommandParameter))
            {
                Command.Execute(CommandParameter);
            }
            else
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                        $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [TOOLBAR] Command.CanExecute returned false for '{Text}'\n");
                }
                catch { }
            }
        }
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Logs/OmniFetch/omnifetch.log"),
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [TOOLBAR] PointerEntered for '{Text}'\n");
        }
        catch { }
        ButtonBorder.BackgroundColor = Color.FromArgb("#E2E8F0");
        ButtonBorder.Stroke = Color.FromArgb("#CBD5E1");
    }

    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        ButtonBorder.BackgroundColor = Colors.Transparent;
        ButtonBorder.Stroke = Colors.Transparent;
    }
}
