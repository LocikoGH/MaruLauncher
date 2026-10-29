using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MaruLauncher;

public partial class App : Application
{
    private readonly DispatcherTimer _systemThemeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private ThemeChoice _themeChoice = ThemeChoice.Dark;
    private bool? _lastSystemTheme;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _systemThemeTimer.Tick += (_, _) =>
        {
            if (_themeChoice != ThemeChoice.System)
                return;
            var isDark = ReadSystemTheme();
            if (_lastSystemTheme != isDark)
                ApplyPalette(isDark);
        };
        _systemThemeTimer.Start();
    }

    public void ApplyTheme(ThemeChoice choice)
    {
        _themeChoice = choice;
        if (choice == ThemeChoice.System)
        {
            var systemTheme = ReadSystemTheme();
            _lastSystemTheme = systemTheme;
            ApplyPalette(systemTheme);
            return;
        }
        _lastSystemTheme = null;
        ApplyPalette(choice == ThemeChoice.Dark);
    }

    private bool ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            return value is not int lightTheme || lightTheme == 0;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private void ApplyPalette(bool dark)
    {
        var colors = dark
            ? new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#11141B",
                ["SurfaceBrush"] = "#1C212B",
                ["PanelBrush"] = "#252C38",
                ["TextBrush"] = "#D2D3D8",
                ["MutedBrush"] = "#898D98",
                ["AccentBrush"] = "#3A3644",
                ["BorderBrush"] = "#30343D",
                ["DangerBrush"] = "#795056",
                ["SuccessBrush"] = "#486456",
                ["HoverBrush"] = "#292D36",
                ["SelectedBrush"] = "#34313D",
                ["PressedBrush"] = "#25232B"
            }
            : new Dictionary<string, string>
            {
                ["BackgroundBrush"] = "#F2F4F8",
                ["SurfaceBrush"] = "#FFFFFF",
                ["PanelBrush"] = "#E8EBF2",
                ["TextBrush"] = "#18202C",
                ["MutedBrush"] = "#596476",
                ["AccentBrush"] = "#5545D6",
                ["BorderBrush"] = "#CBD1DC",
                ["DangerBrush"] = "#B42332",
                ["SuccessBrush"] = "#137A52",
                ["HoverBrush"] = "#DDE1EA",
                ["SelectedBrush"] = "#D1D6E2",
                ["PressedBrush"] = "#C4CAD7"
            };
        foreach (var (key, hex) in colors)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            Resources[key] = new SolidColorBrush(color);
        }
        Resources["MainBackgroundBrush"] = dark
            ? Resources["DarkBackgroundBrush"]
            : Resources["BackgroundBrush"];
        Resources[SystemColors.WindowBrushKey] = Resources["BackgroundBrush"];
        Resources[SystemColors.WindowTextBrushKey] = Resources["TextBrush"];
        Resources[SystemColors.ControlBrushKey] = Resources["SurfaceBrush"];
        Resources[SystemColors.ControlTextBrushKey] = Resources["TextBrush"];
        Resources[SystemColors.HighlightBrushKey] = Resources["SelectedBrush"];
        Resources[SystemColors.HighlightTextBrushKey] = Resources["TextBrush"];
        Resources[SystemColors.InactiveSelectionHighlightBrushKey] = Resources["HoverBrush"];
        Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = Resources["TextBrush"];
    }
}
