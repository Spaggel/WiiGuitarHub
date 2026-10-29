using System.Windows;
using Microsoft.Win32;
using WiiHub.Core;
using WiiHub.Core.Config;

namespace WiiHub.App;

/// <summary>
/// Applies Light / Dark / Follow-Windows:
/// - swaps the app's colour palette (Themes/Light.xaml or Themes/Dark.xaml), and
/// - sets WPF's Fluent theme so standard controls (dropdowns, checkboxes, scrollbars…) match.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml");

    private static AppTheme _theme = AppTheme.System;
    private static bool _listening;

    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        var app = Application.Current;
        if (app is null) return;

        try
        {
            // Fluent theme for the standard WPF controls (System follows Windows by itself).
            app.ThemeMode = theme switch
            {
                AppTheme.Light => ThemeMode.Light,
                AppTheme.Dark => ThemeMode.Dark,
                _ => ThemeMode.System,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not apply the Windows control theme: {ex.Message}");
        }

        ApplyPalette(IsDark(theme));

        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Windows' light/dark switch arrives as a "General" preference change.
        if (_theme != AppTheme.System || e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.InvokeAsync(() => ApplyPalette(IsDark(AppTheme.System)));
    }

    private static void ApplyPalette(bool dark)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        var current = merged.FirstOrDefault(d => d.Source is { } s &&
            (s.OriginalString.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
             s.OriginalString.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)));

        var wanted = dark ? DarkUri : LightUri;
        if (current?.Source is { } src && src.OriginalString.EndsWith(dark ? "Dark.xaml" : "Light.xaml", StringComparison.OrdinalIgnoreCase))
            return;

        var palette = new ResourceDictionary { Source = wanted };
        if (current is null)
        {
            merged.Add(palette);
        }
        else
        {
            // Replace in place, so it keeps its position relative to the Fluent dictionaries.
            merged[merged.IndexOf(current)] = palette;
        }
    }

    /// <summary>Whether the given theme ends up dark, reading Windows' app mode for System.</summary>
    public static bool IsDark(AppTheme theme) => theme switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => WindowsUsesDarkApps(),
    };

    private static bool WindowsUsesDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }
}
