using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RASharp.App.Settings;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaColors = System.Windows.Media.Colors;
using WpfSystemColors = System.Windows.SystemColors;

namespace RASharp.App.Theming;

public sealed partial class SystemThemeService : IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightThemeValue = "AppsUseLightTheme";
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    private readonly System.Windows.Application application;
    private readonly Dispatcher dispatcher;
    private bool disposed;

    public SystemThemeService(System.Windows.Application application)
    {
        this.application = application;
        dispatcher = application.Dispatcher;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_Loaded));
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        ApplyCurrentTheme();
    }

    public bool IsDark { get; private set; }

    public AppThemeMode Mode { get; private set; } = AppThemeMode.System;

    public void SetMode(AppThemeMode mode)
    {
        Mode = mode;
        ApplyCurrentTheme();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        disposed = true;
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(ApplyCurrentTheme, DispatcherPriority.Normal);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            ApplyTitleBar(window, IsDark);
        }
    }

    private void ApplyCurrentTheme()
    {
        IsDark = Mode switch
        {
            AppThemeMode.Light => false,
            AppThemeMode.Dark => true,
            _ => ReadIsDarkTheme(),
        };
        Forms.Application.SetColorMode(Mode switch
        {
            AppThemeMode.Light => Forms.SystemColorMode.Classic,
            AppThemeMode.Dark => Forms.SystemColorMode.Dark,
            _ => Forms.SystemColorMode.System,
        });
        var palette = IsDark ? ThemePalette.Dark : ThemePalette.Light;
        SetBrush("WindowBackgroundBrush", palette.WindowBackground);
        SetBrush("ControlBackgroundBrush", palette.ControlBackground);
        SetBrush("CardBackgroundBrush", palette.CardBackground);
        SetBrush("PrimaryTextBrush", palette.PrimaryText);
        SetBrush("SecondaryTextBrush", palette.SecondaryText);
        SetBrush("BorderBrush", palette.Border);
        SetBrush("InfoBackgroundBrush", palette.InfoBackground);
        SetBrush("SuccessTextBrush", palette.SuccessText);
        SetBrush("AccentBrush", palette.Accent);
        SetBrush("AccentForegroundBrush", palette.AccentForeground);
        SetSystemBrush(WpfSystemColors.ControlBrushKey, palette.ControlBackground);
        SetSystemBrush(WpfSystemColors.ControlTextBrushKey, palette.PrimaryText);
        SetSystemBrush(WpfSystemColors.WindowBrushKey, palette.ControlBackground);
        SetSystemBrush(WpfSystemColors.WindowTextBrushKey, palette.PrimaryText);
        SetSystemBrush(WpfSystemColors.MenuBrushKey, palette.CardBackground);
        SetSystemBrush(WpfSystemColors.MenuTextBrushKey, palette.PrimaryText);
        SetSystemBrush(WpfSystemColors.GrayTextBrushKey, palette.SecondaryText);
        SetSystemBrush(WpfSystemColors.HighlightBrushKey, palette.Accent);
        SetSystemBrush(WpfSystemColors.HighlightTextBrushKey, palette.AccentForeground);

        foreach (Window window in application.Windows)
        {
            ApplyTitleBar(window, IsDark);
            window.InvalidateVisual();
        }
    }

    private void SetBrush(string key, MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        application.Resources[key] = brush;
    }

    private void SetSystemBrush(ResourceKey key, MediaColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        application.Resources[key] = brush;
    }

    private static bool ReadIsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(AppsUseLightThemeValue) is int value && value == 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static void ApplyTitleBar(Window window, bool isDark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        var enabled = isDark ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            _ = NativeMethods.DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                sizeof(int));
        }
    }

    private sealed record ThemePalette(
        MediaColor WindowBackground,
        MediaColor ControlBackground,
        MediaColor CardBackground,
        MediaColor PrimaryText,
        MediaColor SecondaryText,
        MediaColor Border,
        MediaColor InfoBackground,
        MediaColor SuccessText,
        MediaColor Accent,
        MediaColor AccentForeground)
    {
        public static ThemePalette Light { get; } = new(
            MediaColor.FromRgb(245, 245, 245),
            MediaColors.White,
            MediaColors.White,
            MediaColor.FromRgb(32, 32, 32),
            MediaColor.FromRgb(102, 102, 102),
            MediaColor.FromRgb(208, 208, 208),
            MediaColor.FromRgb(234, 243, 250),
            MediaColor.FromRgb(40, 122, 54),
            MediaColor.FromRgb(21, 126, 251),
            MediaColors.White);

        public static ThemePalette Dark { get; } = new(
            MediaColor.FromRgb(30, 30, 30),
            MediaColor.FromRgb(45, 45, 48),
            MediaColor.FromRgb(37, 37, 38),
            MediaColor.FromRgb(245, 245, 245),
            MediaColor.FromRgb(184, 184, 184),
            MediaColor.FromRgb(74, 74, 74),
            MediaColor.FromRgb(38, 59, 76),
            MediaColor.FromRgb(108, 203, 127),
            MediaColor.FromRgb(59, 130, 246),
            MediaColors.White);
    }

    private static class NativeMethods
    {
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(
            nint window,
            int attribute,
            ref int value,
            int valueSize);
    }
}
