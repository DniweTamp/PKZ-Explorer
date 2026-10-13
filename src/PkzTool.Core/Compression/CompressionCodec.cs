using PkzTool.Core.Archives;
using ZstdSharp;

namespace PkzTool.Core.Compression;

/// <summary>
/// The one place a file turns into stored PKZ bytes. Both the GUI and the CLI go through here, so the
/// "did compression actually help" rule can only be implemented once.
/// </summary>
public static class CompressionCodec
{
    /// <summary>Default Zstandard level, matching the mod manager.</summary>
    public const int DefaultZstdLevel = 3;

    /// <summary>Lowest Zstandard level ZstdSharp accepts.</summary>
    public const int MinZstdLevel = 1;

    /// <summary>Highest Zstandard level ZstdSharp accepts.</summary>
    public const int MaxZstdLevel = 22;

    /// <summary>Lowest Oodle level (<c>SuperFast</c>).</summary>
    public const int MinOodleLevel = Oodle.LevelSuperFast;

    /// <summary>Highest Oodle level (<c>Optimal5</c>).</summary>
    public const int MaxOodleLevel = Oodle.LevelOptimal5;

    /// <summary>
    /// A compressed entry is kept only when it is strictly smaller than this fraction of the original.
    /// At 90% or more the file is stored uncompressed instead, overriding the requested type for that
    /// file alone. <c>--force-compression</c> in the CLI (and the matching override here) skips the
    /// rule.
    /// </summary>
    public const double CompressionWorthwhileThreshold = 0.90;

    /// <summary>The default level for a compression type.</summary>
    public static int DefaultLevel(EntryCompression compression) => compression switch
    {
        EntryCompression.OodleKraken => Oodle.DefaultKrakenLevel,
        _ => DefaultZstdLevel,
    };

    /// <summary>The lowest selectable level for a compression type.</summary>
    public static int MinLevel(EntryCompression compression) =>
        compression == EntryCompression.OodleKraken ? MinOodleLevel : MinZstdLevel;

    /// <summary>The highest selectable level for a compression type.</summary>
    public static int MaxLevel(EntryCompression compression) =>
        compression == EntryCompression.OodleKraken ? MaxOodleLevel : MaxZstdLevel;

    /// <summary>Clamp a requested level into the range the codec accepts.</summary>
    public static int ClampLevel(EntryCompression compression, int level) =>
        Math.Clamp(level, MinLevel(compression), MaxLevel(compression));

    /// <summary>
    /// Compress <paramref name="data"/> for storage, applying the worthwhile rule unless
    /// <paramref name="forceCompression"/> is set. Returns the bytes to write and the compression the
    /// entry must be labelled with — which is <see cref="EntryCompression.None"/> when the rule
    /// overrode the request, so the label always matches the payload.
    /// </summary>
    public static (byte[] Packed, EntryCompression Compression) Pack(
        byte[] data,
        EntryCompression requested,
        int level,
        string? oodleDllPath,
        bool forceCompression = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // An empty file has nothing to compress, and a zero-length compressed block cannot be read
        // back reliably, so it is always stored as-is.
        if (requested == EntryCompression.None || data.Length == 0)
            return (data, EntryCompression.None);

        byte[] packed = requested switch
        {
            EntryCompression.OodleKraken => Oodle.Compress(data, ClampLevel(requested, level),
                oodleDllPath ?? throw new InvalidOperationException(
                    "Oodle compression was requested but no Oodle dll path is configured " +
                    "(Settings -> Oodle dll)")),
            EntryCompression.ZStandard => ZstdCompress(data, ClampLevel(requested, level)),
            _ => data,
        };

        if (forceCompression) return (packed, requested);

        // The rule: 90% or more of the original means the compression is not worth it.
        return packed.Length >= data.Length * CompressionWorthwhileThreshold
            ? (data, EntryCompression.None)
            : (packed, requested);
    }

    /// <summary>Compress with Zstandard at the given level (clamped to the accepted range).</summary>
    public static byte[] ZstdCompress(byte[] data, int level)
    {
        using var compressor = new Compressor(Math.Clamp(level, MinZstdLevel, MaxZstdLevel));
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>Decompress a Zstandard payload of a known uncompressed size.</summary>
    public static byte[] ZstdDecompress(byte[] data, int rawLength)
    {
        using var decompressor = new Decompressor();
        return decompressor.Unwrap(data, rawLength).ToArray();
    }

    /// <summary>
    /// Decompress an entry payload using the recorded compression kind.
    /// <paramref name="oodleDllPath"/> is required only for Oodle entries.
    /// </summary>
    public static byte[] Decompress(EntryCompression kind, byte[] compressed, long decompressedSize,
        string? oodleDllPath)
    {
        switch (kind)
        {
            case EntryCompression.None:
                return compressed;
            case EntryCompression.ZStandard:
                return ZstdDecompress(compressed, checked((int)decompressedSize));
            case EntryCompression.OodleKraken:
                if (string.IsNullOrWhiteSpace(oodleDllPath))
                    throw new InvalidOperationException(
                        "this entry is Oodle-compressed but no Oodle dll path is configured " +
                        "(Settings -> Oodle dll)");
                return Oodle.Decompress(compressed, checked((int)decompressedSize), oodleDllPath);
            default:
                throw new InvalidDataException($"unsupported compression {kind}");
        }
    }

    /// <summary>The <c>comp</c> string written to the archive for a compression kind.</summary>
    public static string CompressionName(EntryCompression compression) => compression switch
    {
        EntryCompression.None => "None",
        EntryCompression.ZStandard => "ZStandard",
        EntryCompression.OodleKraken => "OodleKraken",
        _ => throw new ArgumentOutOfRangeException(nameof(compression)),
    };

    /// <summary>Parse a <c>comp</c> string from an archive.</summary>
    public static EntryCompression ParseCompression(string s) => s.Trim().ToLowerInvariant() switch
    {
        "none" => EntryCompression.None,
        "zstandard" or "zstd" => EntryCompression.ZStandard,
        "oodlekraken" or "oodle" or "kraken" => EntryCompression.OodleKraken,
        _ => throw new InvalidDataException($"unsupported pkz compression '{s}'"),
    };

    /// <summary>
    /// A short display name for the list view. The archive's own strings ("OodleKraken") are longer
    /// than the column needs, so this is the UI spelling and never what is written to disk.
    /// </summary>
    public static string DisplayName(EntryCompression compression) => compression switch
    {
        EntryCompression.None => "None",
        EntryCompression.ZStandard => "ZStandard",
        EntryCompression.OodleKraken => "Oodle Kraken",
        _ => compression.ToString(),
    };

    /// <summary>Parse a display name (or a canonical name) back to a kind; null when unknown.</summary>
    public static EntryCompression? TryParseKind(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Trim().ToLowerInvariant() switch
        {
            "none" or "uncompressed" or "store" or "stored" => EntryCompression.None,
            "zstandard" or "zstd" => EntryCompression.ZStandard,
            "oodlekraken" or "oodle" or "oodle kraken" or "kraken" => EntryCompression.OodleKraken,
            _ => null,
        };
    }

    /// <summary>Every compression kind the UI offers, in menu order.</summary>
    public static IReadOnlyList<EntryCompression> All { get; } = new[]
    {
        EntryCompression.None,
        EntryCompression.ZStandard,
        EntryCompression.OodleKraken,
    };
}
