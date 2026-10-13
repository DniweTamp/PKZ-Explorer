using System.Windows;
using System.Windows.Controls;
using PkzTool.App.Wpf.Dialogs;

namespace PkzTool.App.Wpf.Cli;

/// <summary>
/// Measure each dialog's natural height against the fixed height its window declares.
/// <para>
/// Every dialog is <c>ResizeMode="NoResize"</c> with a hardcoded size, so content that grows taller
/// than the window does not scroll — it clips, and the row that clips is the button row at the
/// bottom. This check exists so that failure is caught here rather than by a user who cannot reach
/// Save.
/// </para>
/// </summary>
internal static class DialogMeasure
{
    /// <summary>
    /// Show each dialog off-screen and confirm that, once laid out, the whole of it — including the
    /// button row — is reachable. This is the check that would have caught the shipped bug, where a
    /// fixed-height dialog clipped its own Save button: it inspects the real laid-out window rather
    /// than trusting the height the XAML declares.
    /// </summary>
    internal static int Run(Action<string> log)
    {
        var dialogs = new (string Name, Window Window)[]
        {
            ("AboutDialog", new AboutDialog(2, 120, new Core.Settings.PkzSettings())),
            ("CompressionDialog", new CompressionDialog("Recompress 5 file(s)", string.Empty,
                Core.Archives.EntryCompression.ZStandard, 3)),
            ("OverwriteDialog", new OverwriteDialog(12)),
            ("TextPromptDialog", new TextPromptDialog("Rename file", "hint", "pl0000.dat")),
            ("OodleDialog", new OodleDialog(null)),
            ("PkzPickDialog", new PkzPickDialog(Array.Empty<Core.Workspace.LoadedArchive>(), "pl")),
            ("SettingsDialog", new SettingsDialog(new Core.Settings.PkzSettings())),
        };

        double screen = SystemParameters.WorkArea.Height;
        int problems = 0;

        log("dialog                    winH  screenH  scroll  resizable  bottomRow  verdict");
        log("------------------------------------------------------------------------------");
        foreach (var (name, window) in dialogs)
        {
            // Off-screen, so nothing flashes up during the check.
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();

            bool resizable = window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;
            var scroller = window.Content as ScrollViewer;
            bool scrollable = scroller is not null;
            bool fitsScreen = window.ActualHeight <= screen;

            // The specific failure that was reported: the last button in the dialog sat below the
            // bottom edge of the window, so it could not be clicked and could not be scrolled to.
            bool bottomRowVisible = LastButtonIsVisible(window);

            bool ok = fitsScreen && resizable && bottomRowVisible;
            if (!ok) problems++;

            log($"{name,-24} {window.ActualHeight,5:0} {screen,7:0}  {scrollable,6}  {resizable,9}  " +
                $"{bottomRowVisible,9}  " + (ok ? "ok" : "PROBLEM"));

            window.Close();
        }

        log(string.Empty);
        log(problems == 0
            ? "every dialog is resizable, fits the screen, and reaches its button row"
            : $"{problems} dialog(s) cannot be resized or cannot reach their button row");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// Render one dialog to a PNG, so its layout can be looked at rather than only asserted about.
    /// Written for the settings dialog, which shipped with its buttons below the visible area.
    /// </summary>
    internal static int Capture(string name, string outputPath, Action<string> log)
    {
        Window window = name.ToLowerInvariant() switch
        {
            "about" => new AboutDialog(2, 120, new Core.Settings.PkzSettings()),
            "compression" => new CompressionDialog("Recompress 5 file(s)", string.Empty,
                Core.Archives.EntryCompression.ZStandard, 3),
            "overwrite" => new OverwriteDialog(12),
            "textprompt" => new TextPromptDialog("Rename file", "hint", "pl0000.dat"),
            "oodle" => new OodleDialog(null),
            "pkzpick" => new PkzPickDialog(Array.Empty<Core.Workspace.LoadedArchive>(), "pl"),
            "settings" => new SettingsDialog(new Core.Settings.PkzSettings()),
            _ => new SettingsDialog(new Core.Settings.PkzSettings()),
        };

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.Show();
        window.UpdateLayout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);

        // Render the window's content rather than the window itself: a headless render cannot capture
        // the native title bar, and a black band there would misrepresent the layout.
        bitmap.Render((System.Windows.Media.Visual)window.Content);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(outputPath)) encoder.Save(stream);

        log($"wrote {outputPath} ({window.ActualWidth:0}x{window.ActualHeight:0})");
        window.Close();
        return 0;
    }

    /// <summary>
    /// Whether the last Button in a dialog's visual tree is visible in the window's client area as the
    /// dialog first appears, without the user having to scroll. Returns true when there is no button.
    /// <para>
    /// This deliberately does <em>not</em> scroll first. An earlier version called
    /// <c>ScrollToEnd</c> and accepted that as proof, which passed the About dialog while a screenshot
    /// showed its Close button cut off below the window edge. A button a user has to hunt for is the
    /// bug being checked for, so scrolling must not be allowed to hide it.
    /// </para>
    /// </summary>
    private static bool LastButtonIsVisible(Window window)
    {
        var buttons = new List<Button>();
        Collect(window, buttons);
        if (buttons.Count == 0) return true;

        var last = buttons[^1];
        if (last.ActualHeight <= 0) return false;   // never laid out, so not visible

        var origin = last.TransformToAncestor(window).Transform(new Point(0, 0));
        double bottom = origin.Y + last.ActualHeight;

        // The client area excludes the caption bar and the resize border.
        double clientBottom = window.ActualHeight
                              - SystemParameters.WindowCaptionHeight
                              - SystemParameters.WindowResizeBorderThickness.Top
                              - SystemParameters.WindowResizeBorderThickness.Bottom;

        return bottom <= clientBottom + 1;
    }

    /// <summary>Gather every Button in a visual tree, in layout order.</summary>
    private static void Collect(DependencyObject root, List<Button> found)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is Button button) found.Add(button);
            Collect(child, found);
        }
    }
}
