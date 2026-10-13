using System.Buffers.Binary;
using System.Text;
using PkzTool.Core.Compression;

namespace PkzTool.Core.Archives;

/// <summary>
/// One member of a PKZ archive as it sits on disk. <see cref="Name"/> is the virtual path exactly as
/// the archive stores it (forward slashes, e.g. <c>pl/pl0000.dat</c>); it is never normalised here, so
/// a round trip through the writer reproduces the original spelling.
/// </summary>
public sealed record PkzEntry(
    string Name,
    EntryCompression Compression,
    long DataOffset,
    long CompressedSize,
    long DecompressedSize);

/// <summary>
/// Reader for the Bayonetta 3 <c>.pkz</c> archive (<c>XT1_FORMAT.md</c> §2). PKZ stores file names, so
/// entries are indexed directly by virtual path with no hash lookup.
/// <para>
/// Header at 0x00 (32 bytes):
/// <c>magic "pkzl" | version 0x00020000 | fileSize u64 | entryCount | entrySize (0x20) |
/// stringAreaSize | stringAreaBase</c>. Entries follow at 0x20, one per <c>entrySize</c> bytes, then the
/// string area. Entry name/compression offsets are relative to the start of the string area; data
/// offsets are absolute.
/// </para>
/// <para>
/// The stream is opened shared-read, so several archives of one virtual filesystem can be open at once
/// and a save can rewrite one while the others are still readable.
/// </para>
/// </summary>
public sealed class PkzArchive : IDisposable
{
    /// <summary>"pkzl" little-endian.</summary>
    public const uint MagicValue = 0x6C7A6B70;

    /// <summary>The only version the tool writes, and the one the game's archives use.</summary>
    public const uint Version = 0x00020000;

    /// <summary>Header size.</summary>
    public const int HeaderSize = 0x20;

    /// <summary>Entry size the game and PKZe use.</summary>
    public const int EntrySize = 0x20;

    /// <summary>Extension of the container.</summary>
    public const string Extension = ".pkz";

    private readonly FileStream _stream;
    private readonly object _gate = new();
    private readonly Dictionary<string, PkzEntry> _byName;

    /// <summary>Absolute path the archive was opened from.</summary>
    public string Path { get; }

    /// <summary>Archive version word, straight from the header.</summary>
    public uint VersionWord { get; }

    /// <summary>Entries in stored order, which is the order the list view shows before any sort.</summary>
    public IReadOnlyList<PkzEntry> Entries { get; }

    private PkzArchive(string path, FileStream stream, uint version, IReadOnlyList<PkzEntry> entries,
        Dictionary<string, PkzEntry> byName)
    {
        Path = path;
        _stream = stream;
        VersionWord = version;
        Entries = entries;
        _byName = byName;
    }

    /// <summary>The bare file name, which is what the list view appends to a shadowed entry.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>True when <paramref name="bytes"/> starts with the PKZ magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(bytes) == MagicValue;

    /// <summary>True when the file at <paramref name="path"/> starts with the PKZ magic.</summary>
    public static bool HasMagic(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < HeaderSize) return false;
            int read = 0;
            while (read < head.Length)
            {
                int n = stream.Read(head[read..]);
                if (n <= 0) return false;
                read += n;
            }
            return HasMagic(head);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Open an archive for reading. Throws <see cref="InvalidDataException"/> when the file is not a
    /// PKZ, when its header is implausible, or when it is truncated.
    /// </summary>
    public static PkzArchive Open(string path)
    {
        // Shared read *and* write: a save replaces the file on disk while this handle is still open, and
        // FileShare.Read alone would make that replace fail with a sharing violation. Nothing here ever
        // writes through this handle, so the sharing is safe.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            // A file too short to hold a header is reported as "not a pkz" rather than as a read that ran
            // off the end, because that is what the user needs to know.
            if (stream.Length < HeaderSize)
                throw new InvalidDataException(
                    $"{path}: not a pkz archive (the file is {stream.Length} bytes, shorter than the 0x20 header)");

            Span<byte> header = stackalloc byte[HeaderSize];
            ReadExact(stream, header, 0);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != MagicValue)
                throw new InvalidDataException($"{path}: not a pkz archive (missing \"pkzl\" magic)");

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[0x04..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header[0x10..]);
            uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header[0x14..]);
            if (count > 10_000_000)
                throw new InvalidDataException($"{path}: implausible pkz entry count {count}");
            if (entrySize < EntrySize || entrySize > 0x1000)
                throw new InvalidDataException($"{path}: implausible pkz entry size {entrySize}");
            long tableBytes = (long)count * entrySize;
            if (tableBytes > stream.Length - HeaderSize || tableBytes > 64L * 1024 * 1024)
                throw new InvalidDataException($"{path}: pkz entry table does not fit the file");

            long stringBase = HeaderSize + tableBytes;
            var entries = new List<PkzEntry>((int)count);
            var raw = new byte[tableBytes];
            ReadExact(stream, raw, HeaderSize);

            for (int i = 0; i < count; i++)
            {
                int at = (int)(i * entrySize);
                uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at));
                uint compOffset = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(at + 4));
                ulong decompressed = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(at + 8));
                ulong dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(at + 0x10));
                ulong compressed = BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(at + 0x18));

                // A data range outside the file means a truncated or hand-edited archive; catching it
                // here turns a confusing read failure at extract time into a clear load error.
                if (dataOffset > (ulong)stream.Length || compressed > (ulong)stream.Length - dataOffset)
                    throw new InvalidDataException(
                        $"{path}: entry {i} data range 0x{dataOffset:X}+{compressed} lies outside the file");

                string name = ReadCString(stream, stringBase + nameOffset);
                string comp = ReadCString(stream, stringBase + compOffset);
                entries.Add(new PkzEntry(name, CompressionCodec.ParseCompression(comp),
                    (long)dataOffset, (long)compressed, (long)decompressed));
            }

            return new PkzArchive(path, stream, version, entries, BuildNameMap(entries));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static Dictionary<string, PkzEntry> BuildNameMap(List<PkzEntry> entries)
    {
        var map = new Dictionary<string, PkzEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
            map.TryAdd(entry.Name, entry); // duplicates: the first wins
        return map;
    }

    /// <summary>Find an entry by virtual path; the lookup is case-insensitive like the engine's.</summary>
    public PkzEntry? Find(string virtualPath) =>
        _byName.TryGetValue(virtualPath, out var entry) ? entry : null;

    /// <summary>Extract one entry, decompressing as needed.</summary>
    public byte[] Extract(PkzEntry entry, string? oodlePath = null)
    {
        byte[] stored = ReadRaw(entry.DataOffset, entry.CompressedSize);
        return CompressionCodec.Decompress(entry.Compression, stored, entry.DecompressedSize, oodlePath);
    }

    /// <summary>Read the stored bytes of one entry without decompressing.</summary>
    public byte[] ReadRaw(long offset, long length)
    {
        if (length < 0 || length > int.MaxValue)
            throw new InvalidDataException($"entry length {length} is out of range");
        var buffer = new byte[length];
        lock (_gate)
        {
            _stream.Position = offset;
            ReadExact(_stream, buffer, -1);
        }
        return buffer;
    }

    private static string ReadCString(FileStream stream, long offset)
    {
        var bytes = new List<byte>(64);
        stream.Position = offset;
        int b;
        while ((b = stream.ReadByte()) > 0 && bytes.Count < 4096) bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void ReadExact(Stream stream, Span<byte> buffer, long position)
    {
        if (position >= 0) stream.Position = position;
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    public void Dispose() => _stream.Dispose();
}
