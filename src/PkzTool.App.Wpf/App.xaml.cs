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

            // No prompt may be shown from here on unless this turns out to be an interactive launch. A
            // prompt blocks its thread until someone clicks it, so in a headless run it means hanging on
            // the first error instead of failing. The answer becomes the safe one, and what was asked is
            // recorded for a check to assert on.
            Prompt.Suppress = true;

            var args = Normalize(e.Args);

            // --show opens the shell with archives preloaded, so the GUI can be driven and screenshotted
            // from a script without automating the Open dialog.
            int show = Array.FindIndex(args, a => string.Equals(a, "--show", StringComparison.OrdinalIgnoreCase));

            // Explorer passes a bare file path when a .pkz is opened by double-click, and the shell is
            // registered for the extension. That is not a command line, so it opens the GUI with the file
            // loaded.
            //
            // A bare path is never a valid command line — every operation needs a flag — so the two can
            // only be told apart by how the process was started. Without that, a script that forgot its
            // flag would wait forever on a window instead of failing, which is worse than the usage error
            // it used to get.
            bool wasLaunchedByTheShell = WasStartedByTheShell();

            bool opensInGui = show < 0
                              && wasLaunchedByTheShell
                              && args.Length == 1
                              && !args[0].StartsWith('-')
                              && args[0].EndsWith(".pkz", StringComparison.OrdinalIgnoreCase)
                              && File.Exists(args[0]);

            if (show >= 0 || opensInGui)
            {
                // Interactive despite having arguments, so it takes the GUI's shutdown behaviour:
                // closing the window must end the process.
                ShutdownMode = ShutdownMode.OnLastWindowClose;

                // ...and its prompts: this launch has a person in front of it.
                Prompt.Suppress = false;

                base.OnStartup(e);
                ThemeManager.Initialize(Settings.Theme);

                // A path the parser would accept: no dash, and present on disk. CliParser treats a lone
                // "-" as a path, so that rule is shared here rather than guessed at again.
                var toOpen = show >= 0
                    ? args[(show + 1)..].Where(IsPath).ToList()
                    : new List<string> { args[0] };

                var shell = new MainWindow();
                MainWindow = shell;
                shell.Show();
                if (toOpen.Count > 0) shell.LoadInitial(toOpen);
                return;
            }

            Shutdown(RunHeadless(args));
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
    /// Whether this process was started by the Windows shell rather than by a command line.
    /// <para>
    /// This is what tells a double-click apart from a typed command. The arguments are identical either
    /// way, and a console check does not work: a GUI-subsystem executable launched from a command prompt
    /// has no console of its own, so GetConsoleWindow returns zero in both cases. The parent process
    /// does differ, and that is what is compared here — explorer.exe for a shell launch, a shell or
    /// script host otherwise.
    /// </para>
    /// </summary>
    private static bool WasStartedByTheShell()
    {
        try
        {
            uint parent = ParentProcessId();
            if (parent == 0) return false;

            using var process = System.Diagnostics.Process.GetProcessById((int)parent);
            return string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // A parent that has already exited, or a denied query: treat it as a command line, which is
            // the behaviour that reports a usage error rather than opening a window.
            return false;
        }
    }

    /// <summary>The parent process id, from the process snapshot.</summary>
    private static uint ParentProcessId()
    {
        var entry = new ProcessEntry32 { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<ProcessEntry32>() };
        IntPtr snapshot = CreateToolhelp32Snapshot(0x00000002 /* TH32CS_SNAPPROCESS */, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return 0;

        try
        {
            if (!Process32First(snapshot, ref entry)) return 0;

            uint me = (uint)Environment.ProcessId;
            do
            {
                if (entry.ProcessId == me) return entry.ParentProcessId;
            }
            while (Process32Next(snapshot, ref entry));

            return 0;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// The raw arguments with the quoting Windows may leave in place removed.
    /// <para>
    /// A file association is written as <c>"%1"</c>. Windows normally strips those quotes itself, but an
    /// association written as <c>\"%1\"</c> — which a third-party installer or a hand-edited registry key
    /// can produce — passes the quotes through as part of the value. Without this, such a path fails every
    /// File.Exists test and the original "no operation flag was given" failure comes straight back.
    /// </para>
    /// </summary>
    private static string[] Normalize(string[] args)
    {
        var cleaned = new string[args.Length];
        for (int i = 0; i < args.Length; i++) cleaned[i] = args[i].Trim('"');
        return cleaned;
    }

    /// <summary>
    /// Whether an argument is a path rather than a flag. This mirrors
    /// <see cref="CliParser"/>'s rule that a lone <c>-</c> is a path, so the two cannot disagree about
    /// what a given argument means.
    /// </summary>
    private static bool IsPath(string arg) => !arg.StartsWith('-') || arg == "-";

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

                // An optional "WxH" so the window can be captured at a size other than the default,
                // which is how the narrow end of the layout gets looked at.
                double capWidth = 1240, capHeight = 820;
                var sizeArg = args.FirstOrDefault(a => a.StartsWith("--size=", StringComparison.OrdinalIgnoreCase));
                if (sizeArg is not null)
                {
                    var parts = sizeArg["--size=".Length..].Split('x', 'X');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], out double w)
                        && double.TryParse(parts[1], out double h)
                        && w > 0 && h > 0)
                    {
                        capWidth = w;
                        capHeight = h;
                    }
                }

                return DialogMeasure.CaptureMain(archive, to, console.Out, capWidth, capHeight);
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

            if (args.Any(a => string.Equals(a, "--check-messages", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckMessageDialogs(console.Out);
            }
            if (args.Any(a => string.Equals(a, "--check-badload", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckBadArchiveIsReported(console.Out);
            }
            if (args.Any(a => string.Equals(a, "--check-drop", StringComparison.OrdinalIgnoreCase)))
            {
                ThemeManager.Initialize(Settings.Theme);
                return DialogMeasure.CheckDroppingAPkzOpensIt(console.Out);
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
