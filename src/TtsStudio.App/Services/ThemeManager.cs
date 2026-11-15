using System.Windows;
using Microsoft.Win32;

namespace TtsStudio.App.Services;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>
/// Swaps the colour palette (Themes/Dark.xaml or Themes/Light.xaml) in the application resources.
/// Styles reference palette colours with DynamicResource, so open windows update in place;
/// custom-drawn controls listen to <see cref="Changed"/>. With <see cref="AppTheme.System"/> the
/// app follows the Windows "app mode" setting, including changes made while it is running.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ResourceDictionary? _palette;

    public static AppTheme Preference { get; private set; } = AppTheme.System;

    /// <summary>Whether the dark palette is the one in use.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised on the UI thread after the palette changes.</summary>
    public static event Action? Changed;

    public static void Apply(AppTheme preference)
    {
        bool first = _palette is null;
        if (first) SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        Preference = preference;
        bool dark = preference switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => SystemUsesDarkTheme(),
        };
        if (!first && dark == IsDark) return;
        IsDark = dark;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/TtsStudio;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) merged.Remove(_palette);
        merged.Insert(0, palette);
        _palette = palette;

        foreach (Window w in Application.Current.Windows) TitleBar.Apply(w);
        Changed?.Invoke();
    }

    private static bool SystemUsesDarkTheme() =>
        Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1) is int light && light == 0;

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (Preference == AppTheme.System) Apply(AppTheme.System);
        });
    }
}
