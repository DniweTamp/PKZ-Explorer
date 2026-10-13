using System.Buffers.Binary;
using System.Text;
using PkzTool.Core.Compression;

namespace PkzTool.Core.Archives;

/// <summary>One file to store as a PKZ member, with its already-decided storage.</summary>
/// <param name="Name">Virtual path, forward slashes, e.g. <c>pl/pl0000.dat</c>.</param>
/// <param name="Data">The uncompressed bytes.</param>
/// <param name="Requested">The compression asked for; the worthwhile rule may override it.</param>
/// <param name="Level">Codec level for <paramref name="Requested"/>.</param>
public sealed record PkzFile(string Name, byte[] Data, EntryCompression Requested, int Level);

/// <summary>A file with its storage already resolved, ready to lay out.</summary>
public sealed record PackedFile(string Name, byte[] Packed, int RawSize, EntryCompression Compression);

/// <summary>
/// Writes a Bayonetta 3 <c>.pkz</c> archive, reproducing the layout the game and PKZe use
/// (<c>XT1_FORMAT.md</c> §2, cross-checked against <c>pkz_tool.py</c>): a 0x20 header, the entry table
/// (0x20 per entry), the shared string area (compression strings first, then path strings, all 8-byte
/// aligned), then the data, 64-byte aligned per entry.
/// <para>
/// Unlike the mod manager's packer this preserves the caller's entry order instead of sorting by name:
/// in this tool the order is meaningful to the user, because the list view's default sort is "PKZ
/// order" and a save must not silently reshuffle the archive.
/// </para>
/// </summary>
public static class PkzWriter
{
    private const uint HeaderReserved2 = 0x1000; // written by PKZe; readers ignore it
    private const int DataAlignment = 64;
    private const int StringAlignment = 8;

    /// <summary>
    /// Compress every file per its request, applying the worthwhile rule, then lay the archive out.
    /// </summary>
    public static byte[] Build(IReadOnlyList<PkzFile> files, string? oodleDllPath,
        bool forceCompression = false, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packed = Pack(files, oodleDllPath, forceCompression, progress, cancellationToken);
        return Layout(packed, progress, cancellationToken);
    }

    /// <summary>Compress every file, applying the worthwhile rule to each independently.</summary>
    public static List<PackedFile> Pack(IReadOnlyList<PkzFile> files, string? oodleDllPath,
        bool forceCompression = false, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packed = new List<PackedFile>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(i, files.Count);
            var file = files[i];
            var (bytes, used) = CompressionCodec.Pack(file.Data, file.Requested, file.Level, oodleDllPath,
                forceCompression, cancellationToken);
            packed.Add(new PackedFile(file.Name, bytes, file.Data.Length, used));
        }
        progress?.Invoke(files.Count, files.Count);
        return packed;
    }

    /// <summary>
    /// Lay out an already-packed set of files. Entry order is preserved; duplicate names keep the
    /// first occurrence only, because a later duplicate would shadow it in the virtual filesystem.
    /// </summary>
    public static byte[] Layout(IReadOnlyList<PackedFile> files, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var entries = Deduplicate(files);

        // An archive with no entries is a legitimate state: deleting every file in one leaves exactly
        // that, and the layout below already handles an empty table and string area. (Packing a folder
        // with nothing in it is prevented at the operation, where refusing is the useful behaviour.)
        cancellationToken.ThrowIfCancellationRequested();

        int count = entries.Count;
        int stringBase = PkzArchive.HeaderSize + count * PkzArchive.EntrySize;

        var (compOffsets, pathOffsets, compSize, pathSize) = BuildStringArea(entries);
        int stringSize = compSize + pathSize;
        var stringBlob = new byte[stringSize];
        foreach (var (name, offset) in compOffsets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            Encoding.UTF8.GetBytes(name).CopyTo(stringBlob, offset);
        for (int i = 0; i < count; i++)
            Encoding.UTF8.GetBytes(entries[i].Name).CopyTo(stringBlob, pathOffsets[i]);

        long dataStart = Align(stringBase + stringSize, DataAlignment);
        var dataOffsets = new long[count];
        long cursor = dataStart;
        for (int i = 0; i < count; i++)
        {
            dataOffsets[i] = cursor;
            cursor = Align(cursor + entries[i].Packed.Length, DataAlignment);
        }

        long fileSize = cursor;
        if (fileSize > int.MaxValue)
            throw new InvalidOperationException($"the pkz pack would be {fileSize} bytes, too large to write");
        var output = new byte[(int)fileSize];

        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x00), PkzArchive.MagicValue);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x04), PkzArchive.Version);
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(0x08), (ulong)fileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x10), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x14), PkzArchive.EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x18), (uint)stringSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x1C), HeaderReserved2);

        for (int i = 0; i < count; i++)
        {
            int at = PkzArchive.HeaderSize + i * PkzArchive.EntrySize;
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at), (uint)pathOffsets[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 4),
                (uint)compOffsets[CompressionCodec.CompressionName(entries[i].Compression)]);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(at + 8), (ulong)entries[i].RawSize);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(at + 0x10), (ulong)dataOffsets[i]);
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(at + 0x18), (ulong)entries[i].Packed.Length);
        }

        stringBlob.CopyTo(output, stringBase);
        for (int i = 0; i < count; i++)
            entries[i].Packed.CopyTo(output, (int)dataOffsets[i]);

        progress?.Invoke(count, count);
        return output;
    }

    /// <summary>
    /// Write an already-packed set of files to <paramref name="destination"/> by streaming the layout
    /// straight to disk: header, entry table, string area, then each payload, padding to the alignment
    /// as it goes.
    /// <para>
    /// This exists alongside <see cref="Layout"/> because the two differ in memory, not in bytes. Laying
    /// out in one array needs the whole output in memory at once on top of the payloads already held,
    /// which for a 600 MB archive means roughly 1.2 GB. Streaming writes the same file with only the
    /// payloads resident, which is what makes saving a large archive practical.
    /// </para>
    /// <para>
    /// The write goes to a temporary file beside the destination and is moved into place at the end, so
    /// an interrupted save cannot leave a half-written archive where a game file used to be.
    /// </para>
    /// </summary>
    public static void LayoutToFile(string destination, IReadOnlyList<PackedFile> files,
        Action<int, int>? progress = null, CancellationToken cancellationToken = default)
    {
        var entries = Deduplicate(files);
        cancellationToken.ThrowIfCancellationRequested();

        int count = entries.Count;
        int stringBase = PkzArchive.HeaderSize + count * PkzArchive.EntrySize;
        var (compOffsets, pathOffsets, compSize, pathSize) = BuildStringArea(entries);
        int stringSize = compSize + pathSize;

        // Data offsets must be known before the entry table is written, so the table is built after a
        // sizing pass that touches only lengths.
        long dataStart = Align(stringBase + stringSize, DataAlignment);
        var dataOffsets = new long[count];
        long cursor = dataStart;
        for (int i = 0; i < count; i++)
        {
            dataOffsets[i] = cursor;
            cursor = Align(cursor + entries[i].Packed.Length, DataAlignment);
        }
        long fileSize = cursor;

        string full = Path.GetFullPath(destination);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Header.
                var header = new byte[stringBase];
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x00), PkzArchive.MagicValue);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x04), PkzArchive.Version);
                BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0x08), (ulong)fileSize);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x10), (uint)count);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), PkzArchive.EntrySize);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), (uint)stringSize);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C), HeaderReserved2);

                for (int i = 0; i < count; i++)
                {
                    int at = PkzArchive.HeaderSize + i * PkzArchive.EntrySize;
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at), (uint)pathOffsets[i]);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(at + 4),
                        (uint)compOffsets[CompressionCodec.CompressionName(entries[i].Compression)]);
                    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(at + 8), (ulong)entries[i].RawSize);
                    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(at + 0x10), (ulong)dataOffsets[i]);
                    BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(at + 0x18),
                        (ulong)entries[i].Packed.Length);
                }

                stream.Write(header, 0, header.Length);

                // String area, then pad up to where the data begins.
                var stringBlob = new byte[stringSize];
                foreach (var (name, offset) in compOffsets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                    Encoding.UTF8.GetBytes(name).CopyTo(stringBlob, offset);
                for (int i = 0; i < count; i++)
                    Encoding.UTF8.GetBytes(entries[i].Name).CopyTo(stringBlob, pathOffsets[i]);
                stream.Write(stringBlob, 0, stringBlob.Length);
                WritePadding(stream, dataStart - (stringBase + stringSize));

                // Payloads, each padded to the next 64-byte boundary.
                var zeros = new byte[DataAlignment];
                for (int i = 0; i < count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Invoke(i, count);

                    var packed = entries[i].Packed;
                    stream.Write(packed, 0, packed.Length);

                    long next = i + 1 < count ? dataOffsets[i + 1] : fileSize;
                    WritePadding(stream, next - (dataOffsets[i] + packed.Length), zeros);
                }

                stream.Flush(flushToDisk: true);
            }

            // Atomic on the same volume, which the temp file guarantees.
            File.Move(temp, full, overwrite: true);
            progress?.Invoke(count, count);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Write <paramref name="count"/> zero bytes, reusing the buffer when one is supplied.</summary>
    private static void WritePadding(Stream stream, long count, byte[]? zeros = null)
    {
        if (count <= 0) return;
        zeros ??= new byte[DataAlignment];
        while (count > 0)
        {
            int chunk = (int)Math.Min(count, zeros.Length);
            stream.Write(zeros, 0, chunk);
            count -= chunk;
        }
    }

    /// <summary>Drop entries whose name repeats, keeping the first, as opening an archive does.</summary>
    private static List<PackedFile> Deduplicate(IReadOnlyList<PackedFile> files)
    {
        var entries = new List<PackedFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
            if (seen.Add(file.Name)) entries.Add(file);
        return entries;
    }

    /// <summary>
    /// Assign the string-area offsets for a set of entries: compression strings first (one slot per
    /// distinct kind), then each path, all 8-byte aligned.
    /// <para>
    /// Mirrors PKZe: a compression string is only padded to 8, with no extra byte reserved for its
    /// terminator, so its own length must not be a multiple of 8 ("None"/"ZStandard"/"OodleKraken" are
    /// 4/9/11, so the zero-filled blob always terminates them).
    /// </para>
    /// </summary>
    private static (Dictionary<string, int> CompOffsets, int[] PathOffsets, int CompSize, int PathSize)
        BuildStringArea(IReadOnlyList<PackedFile> entries)
    {
        var compOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        int compSize = 0;
        foreach (var entry in entries)
        {
            string name = CompressionCodec.CompressionName(entry.Compression);
            if (compOffsets.ContainsKey(name)) continue;
            compOffsets[name] = compSize;
            compSize = (int)Align(compSize + Encoding.UTF8.GetByteCount(name), StringAlignment);
        }

        var pathOffsets = new int[entries.Count];
        int pathSize = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            pathOffsets[i] = compSize + pathSize;
            pathSize = (int)Align(pathSize + Encoding.UTF8.GetByteCount(entries[i].Name) + 1, StringAlignment);
        }

        return (compOffsets, pathOffsets, compSize, pathSize);
    }

    /// <summary>
    /// Write <paramref name="files"/> to <paramref name="destination"/>. The bytes are built in memory
    /// and then written to a temporary file beside the destination and moved into place, so an
    /// interrupted save cannot leave a half-written archive where a game file used to be. No backup is
    /// kept.
    /// </summary>
    public static void WriteFile(string destination, IReadOnlyList<PkzFile> files, string? oodleDllPath,
        bool forceCompression = false, Action<int, int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var packed = Pack(files, oodleDllPath, forceCompression, progress, cancellationToken);
        LayoutToFile(destination, packed, progress, cancellationToken);
    }

    /// <summary>
    /// Write already-built archive bytes to <paramref name="destination"/> via a temporary file, then
    /// replace the original. No backup file is left behind.
    /// </summary>
    public static void WriteBytes(string destination, byte[] bytes)
    {
        string full = System.IO.Path.GetFullPath(destination);
        string? directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temp = full + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            // File.Move with overwrite is atomic on the same volume, which the temp file guarantees.
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth masking the original failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static long Align(long value, int boundary) =>
        value % boundary == 0 ? value : (value / boundary + 1) * boundary;
}
