using System.Windows;
using Microsoft.Win32;

namespace Hearth.Core;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// Swaps the token dictionary at runtime and follows the Windows theme.
///
/// Every brush in the app is referenced with DynamicResource against these
/// tokens, never a literal colour, so a swap repaints the whole window without
/// rebuilding any visual tree. The two dictionaries must expose an identical
/// key set — a key present in one and missing from the other resolves to
/// nothing after a swap and paints transparent.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppTheme _preference = AppTheme.System;

    public static AppTheme Preference => _preference;

    /// <summary>Whether dark tokens are currently loaded.</summary>
    public static bool IsDark { get; private set; }

    public static event EventHandler? Changed;

    public static void Apply(AppTheme theme)
    {
        _preference = theme;
        var dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => WindowsPrefersDark()
        };

        Load(dark);
    }

    /// <summary>Cycles System → Light → Dark, which is the order a toggle reads in.</summary>
    public static AppTheme Cycle()
    {
        var next = _preference switch
        {
            AppTheme.System => AppTheme.Light,
            AppTheme.Light => AppTheme.Dark,
            _ => AppTheme.System
        };
        Apply(next);
        return next;
    }

    private static void Load(bool dark)
    {
        IsDark = dark;

        var uri = new Uri(
            dark ? "Themes/Dark.xaml" : "Themes/Light.xaml",
            UriKind.Relative);

        var dict = (ResourceDictionary)Application.LoadComponent(uri);
        var merged = Application.Current.Resources.MergedDictionaries;

        // The token dictionary is always slot 0 by construction, so replacing it
        // in place keeps the styles that follow resolving against fresh values.
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool WindowsPrefersDark()
    {
        try
        {
            // AppsUseLightTheme is 0 for dark. Absent on older builds, where
            // light is the correct assumption.
            var value = Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 1);
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Repaints when Windows switches theme, but only while following the
    /// system — an explicit choice should not be overridden by the OS.
    /// </summary>
    public static void WatchSystem()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (_preference != AppTheme.System) return;

            Application.Current?.Dispatcher.Invoke(() => Load(WindowsPrefersDark()));
        };
    }
}
