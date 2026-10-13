using System.Text;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;

namespace PkzTool.Core.Tests;

/// <summary>
/// A disposable temp directory. Every test that touches the disk uses one, so a failure cannot leave
/// state behind for the next test or pollute the developer's machine.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix = "pkztool_test_")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>A path inside the directory; the parent folder is created.</summary>
    public string File(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        var directory = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return full;
    }

    /// <summary>Create a file with the given text content and return its path.</summary>
    public string WriteText(string relative, string content)
    {
        var full = File(relative);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A locked file must not fail a test that already produced its verdict.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Builds synthetic PKZ archives for the tests. Writing through <see cref="PkzWriter"/> keeps the
/// fixtures on the same code path as the app, so a layout bug shows up as a test failure rather than
/// as two implementations quietly agreeing with each other.
/// </summary>
public static class PkzFixture
{
    /// <summary>Bytes that compress well, so the worthwhile rule keeps the compression.</summary>
    public static byte[] Compressible(int length, byte seed = 0)
    {
        var buffer = new byte[length];
        for (int i = 0; i < length; i++) buffer[i] = (byte)('a' + ((i + seed) % 7));
        return buffer;
    }

    /// <summary>Bytes that do not compress, so the worthwhile rule must fall back to stored.</summary>
    public static byte[] Incompressible(int length, int seed = 1)
    {
        var buffer = new byte[length];
        new Random(seed).NextBytes(buffer);
        return buffer;
    }

    /// <summary>An ASCII payload with a recognisable marker, for round-trip assertions.</summary>
    public static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The stored bytes for a payload, as the app would compress them.</summary>
    public static (byte[] Packed, EntryCompression Kind) Pack(byte[] data, EntryCompression requested,
        string? oodlePath = null, bool force = false) =>
        CompressionCodec.Pack(data, requested, CompressionCodec.DefaultLevel(requested), oodlePath, force);

    /// <summary>
    /// Write a PKZ containing the given entries, in the order given. The <c>decompressedSize</c> field
    /// is set from the original payload so the reader sees a consistent archive.
    /// </summary>
    public static void Write(string path, params (string Name, byte[] Data, EntryCompression Kind)[] entries)
    {
        var files = entries
            .Select(e => new PkzFile(e.Name, e.Data, e.Kind, CompressionCodec.DefaultLevel(e.Kind)))
            .ToList();
        PkzWriter.WriteFile(path, files, oodleDllPath: null);
    }

    /// <summary>Write an uncompressed PKZ from name/payload pairs.</summary>
    public static void WriteStored(string path, params (string Name, byte[] Data)[] entries) =>
        Write(path, entries.Select(e => (e.Name, e.Data, EntryCompression.None)).ToArray());
}
