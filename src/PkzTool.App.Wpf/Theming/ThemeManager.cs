using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Xt1.Core.Settings;

namespace PkzTool.App.Wpf.Theming;

/// <summary>
/// Copies the active palette into the application's resources (so every themed control updates), and
/// paints the native title bar to match. Palette lookup goes through <see cref="Brush"/> so code-behind
/// can theme non-templated pieces.
/// <para>
/// This is the same manager XT1 Studio uses, kept identical on purpose: the two tools share one visual
/// design language, so their palettes, title-bar handling and DynamicResource strategy must not drift.</para>
/// </summary>
public static class ThemeManager
{
    private static readonly HashSet<Window> TitleBarHooked = new();

    public static bool IsDark { get; private set; }

    public static event Action? Changed;

    public static void Initialize(AppTheme preference)
    {
        IsDark = Resolve(preference);
        ApplyPalette();
        ApplyTitleBars();
    }

    public static void Set(AppTheme preference)
    {
        IsDark = Resolve(preference);
        ApplyPalette();
        ApplyTitleBars();
        Changed?.Invoke();
    }

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public static bool SystemPrefersDark()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light) return light == 0;
        }
        catch
        {
            // Unreadable means "light", the Windows default.
        }
        return false;
    }

    private static bool Resolve(AppTheme preference) => preference switch
    {
        AppTheme.Dark => true,
        AppTheme.Light => false,
        _ => SystemPrefersDark(),
    };

    /// <summary>
    /// Copy the palette's entries into the application's top-level resources. Overwriting keys one by
    /// one (rather than swapping a merged dictionary) makes WPF reliably invalidate every
    /// <c>DynamicResource</c> that references them, including inside control templates.
    /// </summary>
    private static void ApplyPalette()
    {
        if (Application.Current is not { } app) return;
        var source = new ResourceDictionary
        {
            Source = new Uri(IsDark
                ? "pack://application:,,,/Themes/Dark.xaml"
                : "pack://application:,,,/Themes/Light.xaml"),
        };
        foreach (var key in source.Keys)
            app.Resources[key] = source[key];
    }

    private static void ApplyTitleBars()
    {
        if (Application.Current is null) return;
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    public static void ApplyTitleBar(Window window)
    {
        void Apply()
        {
            if (!OperatingSystem.IsWindows()) return;
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            int dark = IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
            catch
            {
                // Older Windows or a non-DWM session: the title bar keeps its system colour.
            }
        }

        if (window.IsLoaded) Apply();
        if (TitleBarHooked.Add(window))
        {
            window.SourceInitialized += (_, _) => Apply();
            window.Closed += (s, _) => { if (s is Window w) TitleBarHooked.Remove(w); };
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
