using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Vfs;

namespace PkzTool.Core.Operations;

/// <summary>
/// Builds a brand-new PKZ archive from a folder on disk. This is "PKZ from folder": the folder's
/// contents become the archive's virtual paths, with the folder itself contributing nothing to the
/// path (so <c>...\romfs_mod\pl\pl0000.dat</c> becomes <c>pl/pl0000.dat</c>).
/// </summary>
public static class PkzFolderBuilder
{
    /// <summary>What a folder build produced.</summary>
    public readonly record struct BuildResult(int FileCount, long TotalBytes, long ArchiveBytes,
        IReadOnlyList<(string Path, string Error)> Errors);

    /// <summary>
    /// Read every file under <paramref name="sourceFolder"/>, compress it with one chosen type, and
    /// write the archive to <paramref name="destination"/>.
    /// <para>
    /// The 90% rule applies to every file exactly as it does elsewhere, so a file that does not shrink
    /// enough is stored uncompressed even though one type was chosen for the whole run.
    /// </para>
    /// </summary>
    public static BuildResult Build(string sourceFolder, string destination, EntryCompression compression,
        int level, string? oodleDllPath, bool forceCompression = false,
        Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = Path.GetFullPath(sourceFolder);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"the folder does not exist: {root}");

        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            throw new InvalidOperationException("the folder has no files to pack");

        var pkzFiles = new List<PkzFile>(files.Count);
        var errors = new List<(string, string)>();
        long total = 0;

        for (int i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = files[i];
            progress?.Invoke(new OperationProgress(i, files.Count, $"Reading {Path.GetFileName(file)}"));

            try
            {
                byte[] data = File.ReadAllBytes(file);
                string name = VirtualPath.Normalize(Path.GetRelativePath(root, file))!;
                pkzFiles.Add(new PkzFile(name, data, compression, level));
                total += data.Length;
            }
            catch (Exception ex)
            {
                errors.Add((file, ex.Message));
            }
        }

        if (pkzFiles.Count == 0)
            throw new InvalidOperationException("every file in the folder failed to read");

        int fileCount = pkzFiles.Count;
        progress?.Invoke(new OperationProgress(files.Count, files.Count, "Compressing"));
        var packed = PkzWriter.Pack(pkzFiles, oodleDllPath, forceCompression,
            (done, count) => progress?.Invoke(new OperationProgress(done, count, "Compressing")),
            cancellationToken);
        pkzFiles.Clear();

        progress?.Invoke(new OperationProgress(0, packed.Count, "Writing"));
        PkzWriter.LayoutToFile(destination, packed,
            (done, count) => progress?.Invoke(new OperationProgress(done, count, "Writing")),
            cancellationToken);
        progress?.Invoke(new OperationProgress(files.Count, files.Count, "Done"));

        long archiveBytes = new FileInfo(Path.GetFullPath(destination)).Length;
        return new BuildResult(fileCount, total, archiveBytes, errors);
    }
}
