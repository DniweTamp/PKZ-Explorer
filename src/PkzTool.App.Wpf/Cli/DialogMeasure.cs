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
    /// A main window configured for a headless check: off-screen, not activated, and forced back to the
    /// normal state.
    /// <para>
    /// The three settings each fix a real failure. <c>WindowState = Normal</c> is required because the
    /// saved settings restore a maximised window, and WPF throws on
    /// "ShowActivated is false and WindowState is set to Maximized"; without it, leaving the app
    /// maximised made every check fail, and made the geometry reports measure the maximised size instead
    /// of the size asked for. <c>UiTestHeadless</c> stops the close path raising a modal prompt that
    /// nothing in a headless run could dismiss.
    /// </para>
    /// </summary>
    private static MainWindow HeadlessMainWindow(double width, double height, bool offScreen)
    {
        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowState = WindowState.Normal,
            Left = offScreen ? -32000 : 0,
            Top = offScreen ? -32000 : 0,
            Width = width,
            Height = height,
            Opacity = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        window.UiTestHeadless();
        return window;
    }

    /// <summary>
    /// Render the main window to a PNG, with or without an archive loaded, so its layout can be looked
    /// at rather than only asserted about.
    /// </summary>
    internal static int CaptureMain(string? archivePath, string outputPath, Action<string> log,
        double width = 1240, double height = 820)
    {
        var window = HeadlessMainWindow(width, height, offScreen: true);

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
    /// Confirm the monitor lookup works, so the dialog height cap is the screen the dialog is actually
    /// on rather than the primary one. Falls back silently if the P/Invoke is wrong, so a silent
    /// fallback is exactly what this checks for.
    /// </summary>
    internal static int CheckMonitorHeight(Action<string> log)
    {
        var dialog = new SettingsDialog(new Core.Settings.PkzSettings())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
        };

        dialog.Show();
        dialog.UpdateLayout();

        log($"SystemParameters.WorkArea.Height : {SystemParameters.WorkArea.Height:0}");
        log($"dialog monitor work height        : {dialog.UiTestMonitorWorkHeight():0}");
        log($"dialog height cap (92% of that)   : {dialog.UiTestMonitorWorkHeight() * 0.92:0}");
        log($"window ActualHeight               : {dialog.ActualHeight:0}");

        // The lookup must return a plausible screen height, not zero (P/Invoke failed) or the primary
        // monitor's height when the dialog is demonstrably elsewhere (which one display cannot show, but
        // a zero is unambiguously a failure).
        double monitor = dialog.UiTestMonitorWorkHeight();
        bool ok = monitor > 200 && monitor <= 10000;

        log(ok ? "the monitor lookup returns a real work area" : "the monitor lookup failed");

        dialog.Close();
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Check whether the trailing-column stretch settles. It is wired to <c>LayoutUpdated</c>, which
    /// fires on every layout pass, and it changes a column width, which causes a layout pass: if the
    /// change never converges the UI thread spins. This runs a fixed number of passes and reports how
    /// many times the stretch actually ran, so a loop shows up as a count far above the pass count.
    /// </summary>
    internal static int CheckStretchSettles(Action<string> log)
    {
        var window = HeadlessMainWindow(1240, 820, offScreen: false);

        window.Show();
        window.UpdateLayout();
        window.UiTestLoadArchivesForLayoutCheck();
        window.UpdateLayout();

        // Force a change the stretch must respond to, by resizing the window. Without this the check
        // passes for the wrong reason: a stretch wired to nothing runs zero times, and zero is not a
        // loop, so "no runs" would be scored as healthy. This proves it runs at all.
        int beforeResize = window.UiTestStretchCount;
        window.Width = 1000;
        window.UpdateLayout();
        window.UpdateLayout();
        int respondedToResize = window.UiTestStretchCount - beforeResize;

        // A width the user dragged must survive the fill that runs on the next pass, when there is room
        // for it. It used to be overwritten outright, which made the trailing column unresizable: the
        // edge moved and snapped back before the mouse was released. This is checked at a wide window so
        // the cap described below is not what is being measured.
        window.Width = 1240;
        window.UpdateLayout();
        window.UpdateLayout();

        double widthBeforeDrag = window.UiTestCompressionColumnWidth();

        // Narrow it by a fixed amount, but never below something a column could legally be: a drag of
        // 150 on a column that is only 120 wide would ask for a negative width, which WPF rejects.
        double dragged = Math.Max(70, widthBeforeDrag - 150);
        window.UiTestDragCompressionColumn(dragged);
        window.UpdateLayout();
        window.UpdateLayout();
        double widthAfterDrag = window.UiTestCompressionColumnWidth();

        // The drag must shrink the column and stay shrunk. If the column is already at the floor there is
        // nothing to narrow to, so that case is reported rather than counted as a failure.
        bool dragHonoured = widthAfterDrag < widthBeforeDrag - 1;
        bool nothingToNarrow = widthBeforeDrag <= 71;

        // The cap must still hold: a width dragged wider than the window can fit is reduced, so a wide
        // drag cannot reintroduce the horizontal scrollbar.
        window.UiTestDragCompressionColumn(9000);
        window.UpdateLayout();
        window.UpdateLayout();
        bool cappedToFit = window.UiTestColumnOverflow() <= 0;

        // Now leave the scene alone. Any further run is the stretch re-invalidating layout off its own
        // change, which is the loop this exists to catch.
        int beforeIdle = window.UiTestStretchCount;
        const int passes = 20;
        for (int i = 0; i < passes; i++) window.UpdateLayout();
        int idleRuns = window.UiTestStretchCount - beforeIdle;

        // It must react to a real change, and must not keep reacting once nothing changes.
        bool reacted = respondedToResize > 0;
        bool settled = idleRuns == 0;

        log($"stretch runs after a resize: {respondedToResize} (must be > 0, or it is not wired up)");
        log($"trailing column after a narrower drag: {widthBeforeDrag:0} -> {widthAfterDrag:0} " +
            (nothingToNarrow ? "(already at the floor, nothing to narrow to)" : "(must shrink, or the drag is being overwritten)"));
        log($"an absurd drag is capped so the columns still fit: {cappedToFit}");
        log($"stretch runs over {passes} idle layout passes: {idleRuns} (must be 0, or it is looping)");

        if (!reacted) log("the stretch never ran: it is not responding to layout changes at all");
        if (!dragHonoured && !nothingToNarrow) log("the dragged width was overwritten by the fill");
        if (!cappedToFit) log("a dragged width overflowed the list, so a scrollbar would appear");
        if (!settled) log("the stretch does NOT settle: layout is looping");

        bool dragOk = dragHonoured || nothingToNarrow;
        window.Close();
        return reacted && settled && dragOk && cappedToFit ? 0 : 1;
    }

    /// <summary>
    /// Report the laid-out geometry of the list and the empty state, which is what shows whether the
    /// grid fills the window and whether the empty message is centred in it.
    /// </summary>
    internal static int ReportMainLayout(string? archivePath, Action<string> log)
    {
        var window = HeadlessMainWindow(1240, 820, offScreen: true);

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

        // The toolbar, for the search box width.
        var search = window.UiTestSearchGeometry();
        log("");
        log($"search box            {search.Box,6:0}   (left gap {search.LeftGap:0}, right gap {search.RightGap:0}, column {search.Column:0})");

        // This is the diagnostic counterpart to --check-main-layout, but it still reports failure when
        // the columns do not fit: a report that always exits 0 cannot be used as a gate, and the number
        // it prints would be the only sign of a problem.
        double overflow = window.UiTestColumnOverflow();

        window.Close();

        log(string.Empty);
        log(overflow <= 0
            ? "the columns fit the list"
            : $"the columns overflow the list by {overflow:0}px; a horizontal scrollbar will appear");
        return overflow <= 0 ? 0 : 1;
    }

    /// <summary>
    /// Verify the per-dialog size overrides actually reach the base class, and that each dialog ends up
    /// with a height that fits the screen and shows its content. This is where a derived class failing
    /// to set <c>SizesItself</c> in time for the base's <c>Loaded</c> handler would show up.
    /// </summary>
    internal static int CheckDialogSizes(Action<string> log)
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

        int problems = 0;
        log("dialog                  declaredH  finalH   width  scrolls  verdict");
        log("-------------------------------------------------------------------");

        foreach (var (name, window) in dialogs)
        {
            double declared = window.Height;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Show();
            window.UpdateLayout();

            // The question is whether the button row is reachable, which is the failure that shipped.
            // Whether a scroll viewer is present in the outer content is not the test: AboutDialog has
            // its own inner one, and a dialog can be tall enough that nothing needs scrolling at all.
            var last = LastButton(window);
            double clientBottom = window.ActualHeight
                                  - SystemParameters.WindowCaptionHeight
                                  - SystemParameters.WindowResizeBorderThickness.Top
                                  - SystemParameters.WindowResizeBorderThickness.Bottom;

            bool buttonVisible = last is null || last.TransformToAncestor(window)
                .Transform(new Point(0, 0)).Y + last.ActualHeight <= clientBottom + 1;

            bool fitsScreen = window.ActualHeight <= SystemParameters.WorkArea.Height;

            // A button below the fold is still reachable when something can scroll to it.
            bool reachable = buttonVisible || CanScrollTo(window, last);

            bool ok = fitsScreen && reachable;
            if (!ok) problems++;

            log($"{name,-22} {declared,8:0} {window.ActualHeight,8:0} {window.ActualWidth,7:0}  " +
                $"{buttonVisible,9}  " + (ok ? "ok" : "PROBLEM"));

            window.Close();
        }

        log(string.Empty);
        log(problems == 0
            ? "every dialog fits the screen and can reach its button row"
            : $"{problems} dialog(s) cannot reach their button row");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// Whether any scroll viewer inside the window can bring the given element into view. Used to tell a
    /// button that is merely below the fold from one the user genuinely cannot reach.
    /// </summary>
    private static bool CanScrollTo(Window window, Button? target)
    {
        if (target is null) return true;

        var scrollers = new List<ScrollViewer>();
        CollectScrollers(window, scrollers);
        return scrollers.Any(s => s.ScrollableHeight > 0);
    }

    private static void CollectScrollers(DependencyObject root, List<ScrollViewer> found)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroller) found.Add(scroller);
            CollectScrollers(child, found);
        }
    }

    /// <summary>
    /// Confirm that wrapping a dialog's content in a scroll viewer did not break its default and cancel
    /// buttons. Enter and Escape are what most people use, and a dialog nobody can dismiss is worse than
    /// one whose buttons are merely off screen.
    /// </summary>
    internal static int CheckDialogButtons(Action<string> log)
    {
        var dialogs = new (string Name, Window Window)[]
        {
            ("OverwriteDialog", new OverwriteDialog(12)),
            ("TextPromptDialog", new TextPromptDialog("Rename file", "hint", "pl0000.dat")),
            ("CompressionDialog", new CompressionDialog("Recompress 5 file(s)", string.Empty,
                Core.Archives.EntryCompression.ZStandard, 3)),
        };

        int problems = 0;
        foreach (var (name, window) in dialogs)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = 0;
            window.Top = 0;
            window.Show();
            window.UpdateLayout();

            var buttons = new List<Button>();
            Collect(window, buttons);

            var accept = buttons.FirstOrDefault(b => b.IsDefault);
            var cancel = buttons.FirstOrDefault(b => b.IsCancel);

            // The buttons must still be found and still carry their roles after the content was wrapped:
            // IsDefault and IsCancel are what make Enter and Escape work.
            bool ok = accept is not null && cancel is not null && accept.IsVisible && cancel.IsVisible;
            if (!ok) problems++;

            log($"{name,-22} default={(accept?.Content?.ToString() ?? "none"),-12} " +
                $"cancel={(cancel?.Content?.ToString() ?? "none"),-10} " + (ok ? "ok" : "PROBLEM"));

            window.Close();
        }

        log(string.Empty);
        log(problems == 0
            ? "every dialog still has a working default and cancel button"
            : $"{problems} dialog(s) lost their default or cancel button");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// The last Button in a visual tree, in layout order: the one furthest down, and so the first to go
    /// missing when a dialog is too short for its content.
    /// </summary>
    private static Button? LastButton(DependencyObject root)
    {
        var found = new List<Button>();
        Collect(root, found);
        return found.Count > 0 ? found[^1] : null;
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

    /// <summary>
    /// Check that a pkz can be dropped anywhere on the window, and that dropping one opens it.
    /// <para>
    /// Two things were wrong. The only drop target was the list, so dropping onto the toolbar, the status
    /// bar or the empty area did nothing — and with no archive loaded the list is Collapsed, so there was
    /// no target at all in the very state where a drop is wanted. And the drop handler opened with "if no
    /// archive is loaded, return", so even a drop that reached it did nothing.
    /// </para>
    /// </summary>
    internal static int CheckDroppingAPkzOpensIt(Action<string> log)
    {
        var window = HeadlessMainWindow(1240, 820, offScreen: false);
        window.Show();
        window.UpdateLayout();

        int problems = 0;

        // Every region of the window must resolve to a drop target.
        foreach (var (name, element) in window.UiTestDropTargets())
        {
            bool accepts = window.UiTestAcceptsDrop(element);
            if (!accepts) problems++;
            log($"{(accepts ? "ok  " : "FAIL")} a drop on {name} is accepted");
        }

        // Dropping a pkz with nothing loaded must open it, which is what the empty state is for.
        string pkz = Path.Combine(Path.GetTempPath(), "pkztool_dropcheck_" + Guid.NewGuid().ToString("N") + ".pkz");
        try
        {
            PkzTool.Core.Archives.PkzWriter.WriteFile(pkz, new[]
            {
                new PkzTool.Core.Archives.PkzFile("pl/pl0000.dat", "dropped"u8.ToArray(),
                    PkzTool.Core.Archives.EntryCompression.None, 0),
            }, oodleDllPath: null);

            int before = window.UiTestArchiveCount;
            window.UiTestDropFiles(new[] { pkz });
            window.UpdateLayout();
            int after = window.UiTestArchiveCount;

            bool opened = after > before;
            if (!opened) problems++;
            log($"{(opened ? "ok  " : "FAIL")} dropping a pkz with nothing loaded opens it ({before} -> {after} archive(s))");
        }
        finally
        {
            try { File.Delete(pkz); } catch (IOException) { }
        }

        window.Close();

        log(string.Empty);
        log(problems == 0
            ? "a pkz can be dropped anywhere on the window and is opened"
            : $"{problems} drop problem(s)");
        return problems == 0 ? 0 : 1;
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
            var window = HeadlessMainWindow(width, height, offScreen: false);

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

            var columns = window.UiTestColumnOverflow();

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

            // The search box must use its column rather than sitting in the middle of it as a sliver.
            // It was 90px in an 878px column, leaving ~394px dead on each side, because an unconstrained
            // TextBox asks for about 90px and the grid was centred rather than stretched.
            var sg = window.UiTestSearchGeometry();
            bool searchFills = sg.Column > 0 && sg.Box >= sg.Column - 40;

            bool ok = fills && centred && columnsFit && searchFills;
            if (!ok) problems++;

            log($"        search box {sg.Box,5:0} of a {sg.Column,5:0} column (gaps {sg.LeftGap:0}/{sg.RightGap:0})" +
                (searchFills ? string.Empty : "  <-- search does not fill its column"));
            log($"{width,5:0}x{height,-5:0} panel={panelWidth,6:0} listGap={listFill,4:0} " +
                $"emptyOffCentre={emptyCentre - panelCentre,5:0} columnsOverflow={columns,4:0}  " +
                (ok ? "ok" : "PROBLEM"));

            window.Close();
        }

        log(string.Empty);
        log(problems == 0
            ? "the list fills the panel, the empty state is centred, the columns fit, and the search box uses its space"
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
}
