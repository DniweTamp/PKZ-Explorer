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

        // Any argument means headless, so this decision is made from the argument count alone. The
        // console streams are attached first so a headless run's output is visible even though the
        // process is a WinExe with no console of its own.
        if (e.Args.Length > 0)
        {
            // --show is the one exception: it opens the shell with archives preloaded. That is what
            // lets the GUI be driven and screenshotted from a script without automating the Open
            // dialog, and it is only reachable from a command line.
            int show = Array.FindIndex(e.Args, a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));
            if (show >= 0)
            {
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
