using System.IO;
using System.Runtime.InteropServices;

namespace PkzTool.App.Wpf.Cli;

/// <summary>
/// Makes a WinExe able to write to a console. The app is built as a windowed executable so the GUI
/// never flashes a console window, but a headless run has to be usable from a script: stdout and stderr
/// must reach the caller's pipes.
/// <para>
/// When the process was started from an existing console (cmd, PowerShell) that console is attached, so
/// output appears inline and redirection still works through the inherited handles. When it was started
/// with no console at all, output goes to the process's own streams, which a redirected caller still
/// captures.
/// </para>
/// </summary>
internal sealed class ConsoleStreams : IDisposable
{
    private const int AttachParentProcess = -1;

    private readonly bool _attached;

    public ConsoleStreams()
    {
        try
        {
            // Fails harmlessly when the process already has a console or none is available.
            _attached = AttachConsole(AttachParentProcess);
        }
        catch (DllNotFoundException)
        {
            _attached = false;
        }
        catch (EntryPointNotFoundException)
        {
            _attached = false;
        }
    }

    /// <summary>Write a line to stdout.</summary>
    public void Out(string line) => Write(Console.Out, line);

    /// <summary>Write a line to stderr, which is where every error goes.</summary>
    public void Error(string line) => Write(Console.Error, line);

    /// <summary>
    /// Write a line, falling back to the debugger when a console stream is unavailable. The flush is
    /// explicit because the process exits through <c>Shutdown</c> without running a normal teardown, and
    /// a buffered last line would otherwise be lost.
    /// </summary>
    private static void Write(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
            writer.Flush();
        }
        catch (IOException)
        {
            // A closed pipe (the caller stopped reading) must not fail the run.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (!_attached) return;
        try
        {
            FreeConsole();
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
