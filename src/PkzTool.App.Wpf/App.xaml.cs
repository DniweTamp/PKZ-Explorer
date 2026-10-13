using System.Windows;
using PkzTool.App.Wpf.Cli;
using PkzTool.App.Wpf.Theming;
using PkzTool.Core.Cli;
using PkzTool.Core.Settings;
using Xt1.Core.Settings;

namespace PkzTool.App.Wpf;

/// <summary>
/// The single entry point. Running the executable with <em>any</em> argument runs headless; no
/// arguments at all opens the GUI.
/// </summary>
public partial class App : Application
{
    /// <summary>The live settings, loaded before anything else touches the disk.</summary>
    public static PkzSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        Settings = PkzSettings.Load();
        if (Settings.Theme == AppTheme.System)
            Settings.Theme = ThemeManager.SystemPrefersDark() ? AppTheme.Dark : AppTheme.Light;

        // A check that opens several windows in turn closes each one before opening the next. With the
        // default OnLastWindowClose that first Close starts shutting the application down, and every
        // window after it lays out as a zero-sized husk.
        //
        // OnExplicitShutdown is therefore required for the headless checks, but it must NOT be left in
        // place for the interactive GUI: nothing on the GUI path calls Shutdown, so the process would
        // outlive its window as a zombie holding ~160 MB. The GUI keeps the default mode, where closing
        // the last window ends the process.
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        // Any argument means headless, so this decision is made from the argument count alone. The
        // console streams are attached first so a headless run's output is visible even though the
        // process is a WinExe with no console of its own.
        if (e.Args.Length > 0)
        {
            // A headless run may open and close several windows, which the default mode would treat as
            // "the application is finished". Explicit shutdown keeps that sequence valid; the exit code
            // decides when the run is over, and every headless path ends in Shutdown(code).
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // --show is the one exception: it opens the shell with archives preloaded. That is what
            // lets the GUI be driven and screenshotted from a script without automating the Open
            // dialog, and it is only reachable from a command line.
            int show = Array.FindIndex(e.Args, a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
            if (show >= 0)
            {
                // --show is interactive despite having an argument, so it takes the GUI's shutdown
                // behaviour: closing the window must end the process.
                ShutdownMode = ShutdownMode.OnLastWindowClose;

                base.OnStartup(e);
                ThemeManager.Initialize(Settings.Theme);

                var preloaded = e.Args[(show + 1)..].Where(a => !a.StartsWith('-') && File.Exists(a)).ToList();
                var shell = new MainWindow();
                MainWindow = shell;
                shell.Show();
                if (preloaded.Count > 0) shell.UiTestLoadMany(preloaded);
                return;
            }

            Shutdown(RunHeadless(e.Args));
            return;
        }

        base.OnStartup(e);

        // The palette goes in after the headless check, so a CLI run never builds a window.
        ThemeManager.Initialize(Settings.Theme);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Run the command line. <c>--help</c> prints the usage and succeeds; a usage error prints the usage
    /// and returns 2; a failed operation returns 1.
    /// </summary>
    private static int RunHeadless(string[] args)
    {
        using var console = new ConsoleStreams();

        if (CliParser.IsHelp(args))
        {
            // The usage text is one block with its own line breaks, so it is written as-is rather than
            // split and rejoined.
            foreach (var line in CliParser.Usage().Split(Environment.NewLine)) console.Out(line);
            return 0;
        }

        // Every diagnostic switch below must be tested before the call to CliRunner at the end of this
        // method, because CliRunner rejects any flag it does not know: a diagnostic that reached it
        // would report "unknown option" instead of running.

        try
        {
            // --measure-dialogs reports whether any fixed-size dialog is taller than the space it has,
            // which is the failure that hides a button row off the bottom of the window.
            if (args.Any(a => string.Equals(a, "--measure-dialogs", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.Run(console.Out);
            }

            // --capture-dialog renders one dialog to a PNG so its layout can be inspected.
            int capture = Array.FindIndex(args, a => string.Equals(a, "--capture-dialog", StringComparison.OrdinalIgnoreCase));
            if (capture >= 0)
            {
                ThemeManager.Initialize(Settings.Theme);
                string which = capture + 1 < args.Length ? args[capture + 1] : "settings";
                string to = capture + 2 < args.Length ? args[capture + 2] : "dialog.png";
                return DialogMeasure.Capture(which, to, console.Out);
            }

            // --capture-main renders the main window, and --main-layout reports its geometry.
            int captureMain = Array.FindIndex(args, a => string.Equals(a, "--capture-main", StringComparison.OrdinalIgnoreCase));
            if (captureMain >= 0)
            {
                ThemeManager.Initialize(Settings.Theme);

                // The archive is optional, so the two positionals are told apart by extension rather
                // than by position: taking the first non-flag argument as the archive meant
                // "--capture-main out.png" wrote to the default name and silently ignored the one asked
                // for. A pkz is the only thing this option accepts as an input.
                string? archive = null;
                string to = "main.png";

                foreach (var arg in args[(captureMain + 1)..].TakeWhile(a => !a.StartsWith('-')))
                {
                    if (arg.EndsWith(".pkz", StringComparison.OrdinalIgnoreCase) && archive is null) archive = arg;
                    else to = arg;
                }

                return DialogMeasure.CaptureMain(archive, to, console.Out);
            }

            if (args.Any(a => string.Equals(a, "--check-dialog-buttons", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                int b = DialogMeasure.CheckDialogButtons(console.Out);
                int s = DialogMeasure.CheckDialogSizes(console.Out);
                return b == 0 && s == 0 ? 0 : 1;
            }
            if (args.Any(a => string.Equals(a, "--check-monitor", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckMonitorHeight(console.Out);
            }

            if (args.Any(a => string.Equals(a, "--check-stretch", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckStretchSettles(console.Out);
            }

            if (args.Any(a => string.Equals(a, "--check-main-layout", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckMainLayout(console.Out);
            }

            if (args.Any(a => string.Equals(a, "--main-layout", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                string? archive = args.FirstOrDefault(a => a.EndsWith(".pkz", StringComparison.OrdinalIgnoreCase));
                return DialogMeasure.ReportMainLayout(archive, console.Out);
            }

            // --uitest builds the shell and every dialog headlessly, which is the only way to prove the
            // XAML is sound without a human clicking through it.
            int uiTest = Array.FindIndex(args, a => string.Equals(a, "--uitest", StringComparison.OrdinalIgnoreCase));
            if (uiTest >= 0)
            {
                var target = uiTest + 1 < args.Length && !args[uiTest + 1].StartsWith('-')
                    ? args[uiTest + 1]
                    : null;

                // The shell's own resources have to exist before any window is constructed.
                ThemeManager.Initialize(Settings.Theme);
                return UiSmoke.Run(target, console.Out);
            }

            return new CliRunner(console.Out, console.Error, Settings).Run(args);
        }
        catch (Exception ex)
        {
            // Nothing may escape into an unhandled-exception dialog: a headless run must report and
            // return a nonzero code so a script can act on it.
            console.Error($"error: {ex.Message}");
            return 1;
        }
    }
}
