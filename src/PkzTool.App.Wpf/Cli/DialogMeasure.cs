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
    /// Render the main window to a PNG, with or without an archive loaded, so its layout can be looked
    /// at rather than only asserted about.
    /// </summary>
    internal static int CaptureMain(string? archivePath, string outputPath, Action<string> log,
        double width = 1240, double height = 820)
    {
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = width,
            Height = height,
        };

        window.Show();
        if (archivePath is not null && File.Exists(archivePath)) window.UiTestLoad(archivePath);
        window.UpdateLayout();

        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render((System.Windows.Media.Visual)window.Content);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = File.Create(outputPath)) encoder.Save(stream);

        log($"wrote {outputPath} ({window.ActualWidth:0}x{window.ActualHeight:0})");
        window.Close();
        return 0;
    }

    /// <summary>
    /// Report the laid-out geometry of the list and the empty state, which is what shows whether the
    /// grid fills the window and whether the empty message is centred in it.
    /// </summary>
    internal static int ReportMainLayout(string? archivePath, Action<string> log)
    {
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            Width = 1240,
            Height = 820,
        };

        window.Show();
        if (archivePath is not null && File.Exists(archivePath)) window.UiTestLoad(archivePath);
        window.UpdateLayout();

        foreach (var (name, element) in window.UiTestLayoutProbe())
        {
            if (element is null) { log($"{name,-22} (not found)"); continue; }

            var origin = element.TransformToAncestor(window).Transform(new Point(0, 0));
            double left = origin.X;
            double right = window.ActualWidth - (left + element.ActualWidth);

            log($"{name,-22} x={left,6:0} w={element.ActualWidth,6:0} " +
                $"right-gap={right,6:0}  h={element.ActualHeight,6:0}");
        }

        // The numbers StretchLastColumn works from, so the column maths can be checked against the
        // real viewport instead of guessed at.
        foreach (var line in window.UiTestColumnProbe()) log(line);

        window.Close();
        return 0;
    }

    /// <summary>
    /// Check the main window's layout at several sizes: the list must fill the panel, the empty message
    /// must be centred in it, and the columns must span it without overflowing into a horizontal
    /// scrollbar. Each of those was wrong at some point — the list filled half the window and the
    /// message was centred in that half rather than the window.
    /// </summary>
    internal static int CheckMainLayout(Action<string> log)
    {
        var sizes = new (double Width, double Height)[]
        {
            (820, 560),    // the declared minimum
            (1240, 820),   // the default
            (1920, 1080),  // maximised on a common display
        };

        int problems = 0;
        foreach (var (width, height) in sizes)
        {
            var window = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 0,
                Top = 0,
                Width = width,
                Height = height,
                // Shown but transparent and non-activating, so it neither flashes up nor steals focus
                // while still receiving the layout pass that a hidden window does not get.
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            window.Show();
            window.UiTestLoad("NONE");   // no archive: the empty state is on screen

            // Two passes: the first realises the visual tree at the requested size, the second lays it
            // out after the empty state has been decided and the trailing column stretched.
            window.UpdateLayout();
            window.UpdateLayout();

            var probe = window.UiTestLayoutProbe().ToDictionary(p => p.Name, p => p.Element);
            var list = probe["FileList"]!;
            var empty = probe["EmptyState"]!;

            // Both are compared against the panel that contains them, which is the box they share: the
            // list fills it and the message sits in its middle.
            var panel = (FrameworkElement)list.Parent;
            double panelWidth = panel.ActualWidth;

            double emptyCentre = empty.TransformToAncestor(panel).Transform(new Point(0, 0)).X + empty.ActualWidth / 2;
            double panelCentre = panelWidth / 2;

            var columns = window.UiTestColumnProbe()
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(p => p.Length >= 3 && p[0] == "overflow")
                .Select(p => double.Parse(p[^1]))
                .FirstOrDefault();

            // The message is centred in the panel, not in a region beside the list. This is the bug that
            // was reported: with the list docked to the left, the message centred within the list's own
            // leftover box and appeared off to the right.
            bool centred = Math.Abs(emptyCentre - panelCentre) < 8;

            // The columns fit: no positive overflow, so no horizontal scrollbar.
            bool columnsFit = columns <= 0;

            // The list fills the panel, measured with an archive loaded because the empty state hides
            // the list and a collapsed element reports no width.
            window.UiTestLoadArchivesForLayoutCheck();
            window.UpdateLayout();
            window.UpdateLayout();
            double listFill = panel.ActualWidth - list.ActualWidth;
            bool fills = list.Visibility == Visibility.Visible && listFill < 20;

            bool ok = fills && centred && columnsFit;
            if (!ok) problems++;

            log($"{width,5:0}x{height,-5:0} panel={panelWidth,6:0} listGap={listFill,4:0} " +
                $"emptyOffCentre={emptyCentre - panelCentre,5:0} columnsOverflow={columns,4:0}  " +
                (ok ? "ok" : "PROBLEM"));

            window.Close();
        }

        log(string.Empty);
        log(problems == 0
            ? "the list fills the panel, the empty state is centred, and the columns fit"
            : $"{problems} size(s) have a layout problem");
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
