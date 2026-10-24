using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using PkzTool.App.Wpf.Theming;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Shared shell for every dialog: centred on the owner, hidden from the taskbar, using the themed
/// window background, and repainting the native title bar whenever the palette changes.
/// <para>
/// It also owns sizing, because a dialog that cannot show its own buttons is unusable and the failure
/// is easy to ship: every dialog here used to declare a fixed height with <c>ResizeMode="NoResize"</c>,
/// and once the content grew past that height the bottom row — the buttons — was clipped with no way
/// to scroll to it. Three dialogs were in that state before this was fixed.
/// </para>
/// <para>
/// The height a dialog declares is treated as a <em>preferred</em> size, not a limit. On first show
/// the window grows to whatever its content actually needs (measured, so wrapped text is accounted
/// for), and it is resizable with a scrollbar as a fallback for a smaller screen or a larger font than
/// the layout assumed.
/// </para>
/// </summary>
public class ThemedDialog : Window
{
    /// <summary>
    /// The tallest a dialog may grow to fit its content, as a fraction of the working area. Beyond
    /// this the content scrolls instead, so a dialog can never grow past the bottom of the screen.
    /// </summary>
    private const double MaxHeightFraction = 0.92;

    private bool _sizedToContent;

    public ThemedDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        // Resizable, with a sensible floor. A dialog whose content does not fit is then still usable:
        // the user can drag it larger, and the scroll viewer added below reaches anything hidden.
        ResizeMode = ResizeMode.CanResizeWithGrip;
        MinWidth = 360;
        MinHeight = 180;

        SetResourceReference(BackgroundProperty, "Brush.Window");

        Loaded += OnLoaded;
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    /// <summary>
    /// Wrap the dialog's content in a scroll viewer and grow the window to the height the content
    /// needs. Runs once, on the first show, because the content has to be measured after the theme's
    /// resources are in place for wrapped text to report its real height.
    /// </summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeManager.ApplyTitleBar(this);

        if (_sizedToContent) return;
        _sizedToContent = true;

        FitToContent();
    }

    /// <summary>
    /// Set by a dialog whose own layout already handles a short window, so the base does not grow the
    /// window to the full height of its text. The About dialog is the only one: its body scrolls
    /// between fixed header and button rows, and sizing it to all of its text would push the Close
    /// button off the screen to make room for prose nobody has to read at once.
    /// </summary>
    protected bool SizesItself { get; init; }

    private void FitToContent()
    {
        if (Content is not FrameworkElement content) return;

        // A dialog that already scrolls its own text (the About dialog does) must not be wrapped in a
        // second viewer: the inner one would never reach its own scrollbar, and the outer one would
        // carry the button row below the fold. Such a dialog keeps its declared height, capped to the
        // working area so it cannot run off the bottom of the screen.
        if (SizesItself)
        {
            double room = WorkingAreaHeight();
            if (Height > room) Height = room;
            CentreIfNeeded();
            return;
        }

        if (!ContainsOwnScrollViewer(content))
        {
            var scroller = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = content,
            };
            Content = scroller;
            content = scroller;
        }

        // Measure at the width the dialog will actually have, with unbounded height, so the height
        // reported is the height the content wants once its text has wrapped.
        double width = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(width) || width <= 0) width = MinWidth;

        content.Measure(new Size(width, double.PositiveInfinity));
        double wantedContent = content.DesiredSize.Height;

        // Everything the window adds around its content: the caption bar and the resize border.
        //
        // The SystemParameters figures are used rather than measuring this window, because at this point
        // the content on screen has already been squeezed by the old window height and would report a
        // wrong overhead. They do add up to about 8px less than the real non-client height on a stock
        // theme, which comes straight off the bottom row, so that shortfall is covered explicitly.
        double frame = SystemParameters.WindowCaptionHeight
                       + SystemParameters.WindowResizeBorderThickness.Top
                       + SystemParameters.WindowResizeBorderThickness.Bottom
                       + NonClientShortfall;

        double wanted = wantedContent + frame + ContentSlack;

        // Never taller than the working area, so a dialog cannot extend past the bottom of the screen.
        // When the content does not fit in that budget the scroll viewer reaches the rest.
        double budget = WorkingAreaHeight();

        // The height grows to fit the content and never beyond the screen. A dialog whose declared
        // height is already larger keeps it, so a deliberately roomy layout is not shrunk — unless the
        // dialog asked to shrink, which a message box does: a one-line question looks broken sitting in
        // the empty space a many-line error needed.
        Height = ShrinksToContent
            ? Math.Min(Math.Max(wanted, MinHeight), budget)
            : Math.Max(Height, Math.Min(wanted, budget));

        // Re-centre, because the window may have grown past where it was placed.
        CentreIfNeeded();
    }

    /// <summary>
    /// Set by a dialog whose height should follow its content in both directions. False by default: a
    /// dialog with a deliberately spacious layout keeps the height it declares.
    /// </summary>
    protected bool ShrinksToContent { get; init; }

    /// <summary>
    /// The raw working-area height of the monitor this dialog is on, for the check that the lookup
    /// works. Zero means the lookup failed and the primary monitor would be used instead.
    /// </summary>
    internal double UiTestMonitorWorkHeight() => WorkingAreaHeight() / MaxHeightFraction;

    /// <summary>
    /// The usable height of the monitor this dialog is on. <see cref="SystemParameters.WorkArea"/> is the
    /// primary monitor only, so a dialog opened from a window on a shorter second display could be sized
    /// past the bottom of that display; this asks the owner's screen instead and falls back to the
    /// primary when there is no owner.
    /// </summary>
    private double WorkingAreaHeight()
    {
        var handle = Owner is not null
            ? new System.Windows.Interop.WindowInteropHelper(Owner).Handle
            : new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (handle != IntPtr.Zero
            && MonitorFromWindow(handle, MonitorDefaultToNearest) is { } monitor
            && monitor != IntPtr.Zero)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
                return (info.WorkArea.Bottom - info.WorkArea.Top) * MaxHeightFraction;
        }

        return SystemParameters.WorkArea.Height * MaxHeightFraction;
    }

    private const uint MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr handle, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>The native MONITORINFO, whose rectangles are integer pixels, not the WPF <c>Rect</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>Whether a dialog's own content already contains a scroll viewer.</summary>
    private static bool ContainsOwnScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer) return true;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
            if (ContainsOwnScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)))
                return true;
        return false;
    }

    /// <summary>
    /// Slack added to the measured content height. The measurement above is taken at the window's
    /// client width, which is slightly narrower than the area the content ends up in once the resize
    /// border is accounted for; without this the last row can still land a few pixels short.
    /// </summary>
    private const double ContentSlack = 4;

    /// <summary>
    /// How much taller a themed window's non-client area is than the caption and resize border add up to.
    /// Measured on this app's windows with GetClientRect; without it the computed height lands about
    /// 8px short and the shortfall is taken off the bottom row, which is where the buttons are.
    /// </summary>
    private const double NonClientShortfall = 8;

    /// <summary>Re-centre over the owner, but only while the window still wants centring.</summary>
    private void CentreIfNeeded()
    {
        if (WindowStartupLocation == WindowStartupLocation.CenterOwner) CentreOnOwner();
    }

    /// <summary>
    /// Re-centre over the owner after the window's size changed. WPF applies
    /// <see cref="WindowStartupLocation.CenterOwner"/> once, when the window is first shown, so a window
    /// that then grows would hang off the bottom of its owner without this.
    /// </summary>
    private void CentreOnOwner()
    {
        if (Owner is null)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        Rect bounds = Owner.WindowState == WindowState.Normal
            ? new Rect(Owner.Left, Owner.Top, Owner.ActualWidth, Owner.ActualHeight)
            : new Rect(0, 0, Owner.ActualWidth, Owner.ActualHeight);

        if (bounds.IsEmpty || bounds.Width <= 0) return;

        double w = !double.IsNaN(Width) && Width > 0 ? Width : ActualWidth;
        double h = !double.IsNaN(Height) && Height > 0 ? Height : ActualHeight;
        Left = bounds.Left + (bounds.Width - w) / 2;
        Top = bounds.Top + (bounds.Height - h) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
    }

    private void OnThemeChanged() => ThemeManager.ApplyTitleBar(this);
}
