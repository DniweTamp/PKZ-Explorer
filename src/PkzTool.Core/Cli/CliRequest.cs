using PkzTool.Core.Archives;
using PkzTool.Core.Compression;

namespace PkzTool.Core.Cli;

/// <summary>
/// The operation a CLI invocation performs. Exactly one is allowed per invocation.
/// </summary>
public enum CliOperation
{
    List,
    Extract,
    ExtractAll,
    Replace,
    Recompress,
    Add,
    Delete,
    Rename,
    CreateFromFolder,
}

/// <summary>
/// A parsed command line. The syntax is "the operation flag and options first, PKZ paths last", so
/// parsing stops treating arguments as options once the first bare path is seen.
/// </summary>
public sealed class CliRequest
{
    /// <summary>The operation to perform.</summary>
    public CliOperation Operation { get; set; }

    /// <summary>The flag as the user wrote it, for error messages.</summary>
    public string OperationFlag { get; set; } = string.Empty;

    /// <summary>The PKZ archives to operate on, in the order given.</summary>
    public List<string> Archives { get; } = new();

    /// <summary>Repeated <c>--entry</c> selectors: exact paths, folders, or globs.</summary>
    public List<string> Entries { get; } = new();

    /// <summary>Folder an extract or an add reads from or writes to.</summary>
    public string? Out { get; set; }

    /// <summary>Which loaded PKZ an add or a rename targets, by file name or path.</summary>
    public string? Pkz { get; set; }

    /// <summary>New name for <c>--rename</c>.</summary>
    public string? Name { get; set; }

    /// <summary>CSV export destination for <c>--list</c>.</summary>
    public string? Csv { get; set; }

    /// <summary>Requested compression; null means the flag was absent, i.e. none.</summary>
    public EntryCompression? Compression { get; set; }

    /// <summary>Codec level for the requested compression.</summary>
    public int? Level { get; set; }

    /// <summary>Skip the "does compression actually help" rule.</summary>
    public bool ForceCompression { get; set; }

    /// <summary>Report what would happen without writing anything.</summary>
    public bool DryRun { get; set; }

    /// <summary>Log each file as it is handled.</summary>
    public bool Verbose { get; set; }

    /// <summary>The compression to use, defaulting to none as the brief specifies.</summary>
    public EntryCompression EffectiveCompression => Compression ?? EntryCompression.None;

    /// <summary>
    /// The level to use: an explicit <c>--level</c>, otherwise the codec's default. Clamped, so a value
    /// meant for one codec cannot fail against another.
    /// </summary>
    public int EffectiveLevel => Compression is null
        ? CompressionCodec.DefaultZstdLevel
        : CompressionCodec.ClampLevel(Compression.Value,
            Level ?? CompressionCodec.DefaultLevel(Compression.Value));
}
