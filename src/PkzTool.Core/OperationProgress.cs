namespace PkzTool.Core;

/// <summary>
/// Progress for a long operation, plus the point at which the caller's <see cref="CancellationToken"/>
/// is checked. Shared by the GUI's progress bar and the CLI's verbose logging, so every long operation
/// in the tool reports the same way.
/// </summary>
/// <param name="Completed">Items finished so far.</param>
/// <param name="Total">Items expected in total.</param>
/// <param name="Message">What is happening, for the status line.</param>
public readonly record struct OperationProgress(int Completed, int Total, string Message)
{
    /// <summary>Fraction complete in 0..1, or 0 when the total is unknown.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp(Completed / (double)Total, 0, 1);
}

/// <summary>
/// One thing that went wrong during a multi-file operation. Operations collect these instead of
/// aborting, so one bad file cannot lose the rest of a selection.
/// </summary>
/// <param name="Path">The virtual or real path involved.</param>
/// <param name="Error">The failure message.</param>
public readonly record struct OperationError(string Path, string Error)
{
    public override string ToString() => $"{Path}: {Error}";
}
