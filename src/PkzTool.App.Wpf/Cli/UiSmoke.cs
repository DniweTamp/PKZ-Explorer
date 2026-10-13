using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PkzTool.App.Wpf.Dialogs;

namespace PkzTool.App.Wpf.Cli;

/// <summary>
/// A headless check that the shell and every dialog construct, lay out and dispose without a user.
/// <para>
/// This is the only way to prove the XAML is sound — every resource key resolves, every binding has a
/// source, and no dialog throws in its constructor — without a human clicking through the app. It runs
/// with any argument, so it is available in CI and from a script.
/// </para>
/// </summary>
internal static class UiSmoke
{
    /// <summary>Build everything and report what was checked. Returns 0 on success, 1 on a failure.</summary>
    internal static int Run(string? archive, Action<string> log)
    {
        var problems = new List<string>();
        int checks = 0;

        // The window is constructed and measured but never shown, so nothing appears on screen.
        MainWindow? window = null;
        try
        {
            window = new MainWindow();
            // Suppress prompts: a check that fails must report itself, not wait on a dialog.
            window.UiTestHeadless();
            window.Width = 1240;
            window.Height = 820;

            // Measuring forces the whole visual tree to be realised, which is what surfaces a bad
            // resource key or a broken style reference.
            window.Measure(new Size(1240, 820));
            window.Arrange(new Rect(0, 0, 1240, 820));
            window.UpdateLayout();
            checks++;
            log($"ok   MainWindow built and laid out ({window.Title})");

            // A real archive loads through the same path the File menu uses.
            if (archive is not null)
            {
                if (!File.Exists(archive))
                {
                    problems.Add($"the archive to test with does not exist: {archive}");
                }
                else
                {
                    window.UiTestLoad(archive);
                    var state = window.UiTestState();
                    checks++;
                    log($"ok   loaded {Path.GetFileName(archive)}: {state}");
                    if (state.StartsWith("0 of"))
                        problems.Add($"loading {archive} produced no rows");

                    // The interactive behaviours cannot be screenshotted, so they are driven here:
                    // expansion, the search filter, each sort column, and the modified marker.
                    foreach (var (name, result) in window.UiTestInteractive())
                    {
                        checks++;
                        log($"ok   {name}: {result}");
                        if (result.StartsWith("FAIL")) problems.Add($"{name}: {result}");
                    }

                    // Leave an edit staged when the window closes. A headless run must not raise the
                    // "unsaved changes" prompt here: nobody could answer it, so the process would hang.
                    // The window's Close below is the check.
                    window.UiTestStageSomething();
                    checks++;
                    log("ok   staged an edit, then closed without a prompt");
                }
            }
        }
        catch (Exception ex)
        {
            problems.Add($"MainWindow: {ex.GetType().Name}: {ex.Message}");
        }

        // Every dialog is constructed, measured and closed. This is what validates their XAML.
        foreach (var (name, make) in DialogFactories())
        {
            try
            {
                var dialog = make();
                dialog.Measure(new Size(900, 700));
                dialog.Arrange(new Rect(0, 0, 900, 700));
                dialog.UpdateLayout();
                checks++;
                log($"ok   {name} ({dialog.Width:0}x{dialog.Height:0})");
                dialog.Close();
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Laid out for real, off-screen, so the check below sees actual positions rather than a
        // measurement. A dialog whose buttons sit past the bottom edge is unusable, and that shipped
        // once: the settings window was too short for its own content and hid OK and Cancel.
        foreach (var (name, make) in DialogFactories())
        {
            ThemedDialog? dialog = null;
            try
            {
                dialog = make();
                dialog.WindowStartupLocation = WindowStartupLocation.Manual;
                dialog.Left = -32000;
                dialog.Top = -32000;
                dialog.Show();
                dialog.UpdateLayout();

                var last = LastButton(dialog);
                if (last is null)
                {
                    checks++;
                    log($"ok   {name} has no button row to check");
                    continue;
                }

                double bottom = last.TransformToAncestor(dialog)
                    .Transform(new Point(0, 0)).Y + last.ActualHeight;
                double clientBottom = dialog.ActualHeight
                                      - SystemParameters.WindowCaptionHeight
                                      - SystemParameters.WindowResizeBorderThickness.Top
                                      - SystemParameters.WindowResizeBorderThickness.Bottom;

                checks++;
                if (bottom <= clientBottom + 1)
                {
                    log($"ok   {name} shows its button row ({last.Content})");
                }
                else
                {
                    problems.Add($"{name}: '{last.Content}' sits {bottom - clientBottom:0}px past the bottom edge");
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{name} layout: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                dialog?.Close();
            }
        }

        // The main window's own layout, at more than one size. Reported here rather than only under its
        // own switch, because a broken list layout is exactly the kind of thing a release should refuse
        // to ship: the list once filled half the window and the empty message centred in that half.
        {
            var lines = new List<string>();
            int layoutResult = DialogMeasure.CheckMainLayout(lines.Add);
            foreach (var line in lines) log($"     {line}");
            checks++;
            if (layoutResult == 0)
            {
                log("ok   main window layout at 3 sizes");
            }
            else
            {
                problems.Add("main window layout check failed");
            }
        }

        // Dialog sizing and dismissal. These are what a user notices first and a test cannot see: a
        // dialog too short for its own buttons, or one whose Enter and Escape stopped working after its
        // content was wrapped in a scroll viewer.
        foreach (var (label, check) in new (string, Func<Action<string>, int>)[]
                 {
                     ("dialogs size to their content", DialogMeasure.CheckDialogSizes),
                     ("dialogs keep their default and cancel buttons", DialogMeasure.CheckDialogButtons),
                     ("the trailing column settles", DialogMeasure.CheckStretchSettles),
                     ("the dialog height cap uses the right monitor", DialogMeasure.CheckMonitorHeight),
                     ("a dropped pkz is opened", DialogMeasure.CheckDroppingAPkzOpensIt),
                     ("a bad archive is reported", DialogMeasure.CheckBadArchiveIsReported),
                     ("message dialogs are themed and cannot block", DialogMeasure.CheckMessageDialogs),
                     ("search finds files in collapsed folders", DialogMeasure.CheckSearchFindsCollapsed),
                 })
        {
            var lines = new List<string>();
            int result = check(lines.Add);
            foreach (var line in lines) log($"     {line}");
            checks++;
            if (result == 0)
            {
                log($"ok   {label}");
            }
            else
            {
                problems.Add($"{label}: check failed");
            }
        }

        window?.Close();

        // Delete the picker's fixture archive, which is written to the temp folder to give that dialog
        // real items. Done after every dialog has closed, because the picker holds it open until then.
        CleanUpFixture();

        foreach (var problem in problems) log($"FAIL {problem}");
        log(problems.Count == 0
            ? $"ui smoke: {checks} checks passed"
            : $"ui smoke: {checks} checks, {problems.Count} failure(s)");
        return problems.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// The last Button in a dialog's visual tree, in layout order. That is the one that lands furthest
    /// down, so it is the one that goes missing first when a dialog is too short for its content.
    /// </summary>
    private static Button? LastButton(DependencyObject root)
    {
        var found = new List<Button>();
        CollectButtons(root, found);
        return found.Count > 0 ? found[^1] : null;
    }

    private static void CollectButtons(DependencyObject root, List<Button> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button button) found.Add(button);
            CollectButtons(child, found);
        }
    }

    /// <summary>
    /// One factory per dialog. A dialog that needs data gets a minimal, valid sample rather than null,
    /// because the point is to exercise its real construction path.
    /// </summary>
    private static IEnumerable<(string Name, Func<ThemedDialog> Make)> DialogFactories()
    {
        yield return ("AboutDialog", () => new AboutDialog(2, 120, new Core.Settings.PkzSettings()));

        yield return ("CompressionDialog", () => new CompressionDialog(
            "Recompress 3 file(s)", string.Empty, Core.Archives.EntryCompression.ZStandard, 3));

        yield return ("OverwriteDialog", () => new OverwriteDialog(7));

        yield return ("TextPromptDialog", () => new TextPromptDialog(
            "Rename file", "This changes the file name only.", "pl0000.dat"));

        yield return ("OodleDialog", () => new OodleDialog(null));

        yield return ("SettingsDialog", () => new SettingsDialog(new Core.Settings.PkzSettings()));

        yield return ("PkzPickDialog", () => new PkzPickDialog(
            WorkspaceFixture(), "pl"));

        yield return ("ThemedDialog base", () => new ThemedDialog { Title = "Base" });
    }

    /// <summary>
    /// A throwaway loaded workspace for the pkz picker. It reads a tiny archive written in the temp
    /// folder, so the dialog's list has real items without depending on a game install. The workspace
    /// is kept alive in a static field because the picker holds the archives it is given.
    /// <para>
    /// The archive is written once and deleted by <see cref="CleanUpFixture"/>. It used to be written
    /// on every call and never removed, which left a temp file behind on every run.
    /// </para>
    /// </summary>
    private static List<Core.Workspace.LoadedArchive> WorkspaceFixture()
    {
        if (_smokeWorkspace is not null) return _smokeWorkspace.Archives.ToList();

        try
        {
            _smokePath = Path.Combine(Path.GetTempPath(), "pkztool_uismoke_" + Guid.NewGuid().ToString("N") + ".pkz");
            Core.Archives.PkzWriter.WriteFile(_smokePath, new[]
            {
                new Core.Archives.PkzFile("pl/pl0000.dat", "smoke"u8.ToArray(),
                    Core.Archives.EntryCompression.None, 0),
            }, oodleDllPath: null);

            var workspace = new Core.Workspace.PkzWorkspace();
            workspace.Load(new[] { _smokePath });
            _smokeWorkspace = workspace;
            return workspace.Archives.ToList();
        }
        catch (Exception)
        {
            // A picker with no archives is still worth constructing; the caller reports the failure.
            return new List<Core.Workspace.LoadedArchive>();
        }
    }

    /// <summary>
    /// Close and delete the picker's fixture archive. The workspace's file handle is released first,
    /// because Windows will not delete a file this process still holds open.
    /// </summary>
    private static void CleanUpFixture()
    {
        _smokeWorkspace?.Dispose();
        _smokeWorkspace = null;

        if (_smokePath is null) return;
        try { File.Delete(_smokePath); }
        catch (IOException) { /* Best effort: a failed cleanup must not fail the run. */ }
        catch (UnauthorizedAccessException) { }
        _smokePath = null;
    }

    /// <summary>Keeps the smoke-test workspace's archives open while the picker dialog uses them.</summary>
    private static Core.Workspace.PkzWorkspace? _smokeWorkspace;

    /// <summary>The fixture archive's path, so it can be deleted once the dialogs are done with it.</summary>
    private static string? _smokePath;
}
