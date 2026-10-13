using PkzTool.Core.Archives;
using PkzTool.Core.Vfs;
using PkzTool.Core.Workspace;

namespace PkzTool.Core.Operations;

/// <summary>What to do when the destination file already exists.</summary>
public enum OverwriteAction
{
    Overwrite,
    Skip,
    Cancel,
}

/// <summary>Asked for a decision when an extract would overwrite; returning Cancel stops the run.</summary>
/// <param name="DestinationPath">The file that already exists.</param>
public delegate OverwriteAction OverwritePrompt(string destinationPath);

/// <summary>The tally of an extract run.</summary>
public readonly record struct ExtractSummary(int Written, int Skipped, int Failed, IReadOnlyList<(string Path, string Error)> Errors)
{
    /// <summary>A one-line summary for the status bar and the console.</summary>
    public string Describe() =>
        $"{Written} extracted" +
        (Skipped > 0 ? $", {Skipped} skipped" : string.Empty) +
        (Failed > 0 ? $", {Failed} failed" : string.Empty);
}

/// <summary>
/// Writes files out of the virtual filesystem onto disk, recreating the folder structure the virtual
/// paths describe. Shared by "Extract selected", "Extract all", the drag-out gesture and the CLI, so
/// the overwrite rules are identical everywhere.
/// </summary>
public static class ExtractionService
{
    /// <summary>
    /// Extract <paramref name="files"/> under <paramref name="destinationRoot"/>. Each node's virtual
    /// path becomes a relative path beneath the root, with forward slashes mapped to the platform
    /// separator. <paramref name="prompt"/> decides what to do about an existing file; when it is null
    /// existing files are overwritten, which is the CLI's documented behaviour.
    /// </summary>
    public static ExtractSummary Extract(PkzWorkspace workspace, IEnumerable<VfsNode> files,
        string destinationRoot, OverwritePrompt? prompt = null,
        Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var list = files.Where(f => f.Kind == VfsNodeKind.File).ToList();
        string root = Path.GetFullPath(destinationRoot);

        int written = 0, skipped = 0;
        var errors = new List<(string, string)>();

        for (int i = 0; i < list.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = list[i];
            progress?.Invoke(new OperationProgress(i, list.Count, $"Extracting {node.Path}"));

            // The virtual path is relative, so it is combined with the root and then re-checked: a
            // crafted name must not be able to walk out of the destination folder.
            string? target = ResolveWithin(root, node.Path);
            if (target is null)
            {
                errors.Add((node.Path, "the path escapes the destination folder"));
                continue;
            }

            try
            {
                if (File.Exists(target))
                {
                    var choice = prompt?.Invoke(target) ?? OverwriteAction.Overwrite;
                    if (choice == OverwriteAction.Cancel) break;
                    if (choice == OverwriteAction.Skip)
                    {
                        skipped++;
                        continue;
                    }
                }

                byte[] data = workspace.ReadData(node);
                string? directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllBytes(target, data);
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One unwritable file must not abandon the rest of the selection.
                errors.Add((node.Path, ex.Message));
            }
        }

        progress?.Invoke(new OperationProgress(list.Count, list.Count, "Extract complete"));
        return new ExtractSummary(written, skipped, errors.Count, errors);
    }

    /// <summary>
    /// Map a virtual path onto a real one, returning null when the result would leave
    /// <paramref name="root"/> or would name something other than a plain file.
    /// <para>
    /// Both sides are made absolute before the comparison, so a relative root cannot confuse it. The
    /// checks beyond that are Windows-specific and matter because an archive is an untrusted input: a
    /// colon would open an NTFS alternate data stream, a reserved device name (<c>NUL</c>, <c>CON</c>,
    /// <c>COM1</c>…) would write to a device rather than a file, and a trailing dot or space is silently
    /// stripped by Win32 so the extracted name would not be the name in the archive.
    /// </para>
    /// </summary>
    public static string? ResolveWithin(string root, string virtualPath)
    {
        if (!IsSafeExtractionPath(virtualPath)) return null;

        var relative = virtualPath.Replace(VirtualPath.Separator, Path.DirectorySeparatorChar);
        string combined;
        try
        {
            combined = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (ArgumentException)
        {
            // A path with characters the platform rejects outright cannot be extracted.
            return null;
        }

        string normalizedRoot = Path.GetFullPath(root);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;

        return combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ? combined : null;
    }

    /// <summary>
    /// True when every segment of a virtual path is a name that can safely become a file. Rejects the
    /// Windows hazards described on <see cref="ResolveWithin"/>; on a non-Windows host only the
    /// separator and empty-segment rules apply.
    /// </summary>
    private static bool IsSafeExtractionPath(string virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return false;

        var segments = virtualPath.Replace('\\', VirtualPath.Separator)
            .Split(VirtualPath.Separator, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return false;

        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;

            if (OperatingSystem.IsWindows())
            {
                // A colon is either an alternate data stream or a drive/UNC prefix.
                if (segment.Contains(':')) return false;
                // Win32 strips trailing dots and spaces, so the file on disk would not match the name.
                if (segment.EndsWith('.') || segment.EndsWith(' ')) return false;
                // A reserved device name is a device however it is extended (NUL.txt is still NUL).
                if (IsReservedDeviceName(segment)) return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The DOS device names, which Win32 still resolves ahead of the filesystem in any directory. The
    /// comparison uses the stem before the first dot, because <c>NUL.txt</c> is the device too.
    /// </summary>
    private static bool IsReservedDeviceName(string segment)
    {
        int dot = segment.IndexOf('.');
        string stem = (dot < 0 ? segment : segment[..dot]).TrimEnd(' ');

        // COM0/LPT0 are not reserved; COM1-9 and LPT1-9 are, and Win32 also accepts COM¹-style
        // superscript digits, which are normalised here rather than listed.
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
        {
            char digit = NormalizeDigit(stem[3]);
            return digit is >= '1' and <= '9';
        }

        return false;
    }

    /// <summary>Map the superscript digits Win32 accepts in device names back to ASCII.</summary>
    private static char NormalizeDigit(char c) => c switch
    {
        '\u00B9' => '1', // ¹
        '\u00B2' => '2', // ²
        '\u00B3' => '3', // ³
        _ => c,
    };

    /// <summary>
    /// Stage every file in a folder tree from disk into <paramref name="archive"/>. The folder's own
    /// name is not part of the virtual path: dropping <c>C:\mod\pl</c> into the root adds
    /// <c>pl/pl0000.dat</c>, not <c>pl/pl/pl0000.dat</c>. Returns what was staged.
    /// </summary>
    public static AddSummary AddFolder(PkzWorkspace workspace, LoadedArchive archive, string sourceFolder,
        string? virtualFolder, EntryCompression compression, int level,
        Func<string, bool>? confirmOverwrite = null,
        Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var files = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return AddFiles(workspace, archive, files, sourceFolder, virtualFolder, compression, level,
            confirmOverwrite, progress, cancellationToken);
    }

    /// <summary>
    /// Stage a set of loose files into <paramref name="archive"/>. <paramref name="relativeTo"/> is the
    /// folder the paths are made relative to, so a drag from Explorer preserves the subfolder shape the
    /// user dragged.
    /// </summary>
    public static AddSummary AddFiles(PkzWorkspace workspace, LoadedArchive archive,
        IReadOnlyList<string> sourceFiles, string relativeTo, string? virtualFolder,
        EntryCompression compression, int level, Func<string, bool>? confirmOverwrite = null,
        Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        int added = 0, replaced = 0, declined = 0;
        var errors = new List<(string, string)>();

        for (int i = 0; i < sourceFiles.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = sourceFiles[i];
            progress?.Invoke(new OperationProgress(i, sourceFiles.Count, $"Adding {Path.GetFileName(file)}"));

            try
            {
                string relative = Path.GetRelativePath(relativeTo, file);
                string leaf = relative.Replace(Path.DirectorySeparatorChar, VirtualPath.Separator)
                    .Replace('\\', VirtualPath.Separator);
                string path = VirtualPath.Normalize(
                    virtualFolder is null ? leaf : VirtualPath.Combine(virtualFolder, leaf))!;

                byte[] data = File.ReadAllBytes(file);
                bool exists = workspace.WouldCollide(archive, path);
                if (exists && confirmOverwrite is not null && !confirmOverwrite(path))
                {
                    declined++;
                    continue;
                }

                // An add onto an existing path is a replace of that exact row, which is what the user
                // confirmed in the prompt.
                var existing = workspace.AllFiles.FirstOrDefault(f =>
                    ReferenceEquals(f.Owner, archive.Archive) &&
                    VirtualPath.Comparer.Equals(f.Path, path));
                workspace.StageAddOrReplace(archive, path, data, compression, level,
                    allowOverwrite: true, replaceKey: existing?.Key);
                if (exists) replaced++; else added++;
            }
            catch (Exception ex)
            {
                errors.Add((file, ex.Message));
            }
        }

        progress?.Invoke(new OperationProgress(sourceFiles.Count, sourceFiles.Count, "Add complete"));
        return new AddSummary(added, replaced, declined, errors);
    }
}

/// <summary>The tally of an add run.</summary>
public readonly record struct AddSummary(int Added, int Replaced, int Declined, IReadOnlyList<(string Path, string Error)> Errors)
{
    /// <summary>A one-line summary for the status bar and the console.</summary>
    public string Describe() =>
        $"{Added} added" +
        (Replaced > 0 ? $", {Replaced} replaced" : string.Empty) +
        (Declined > 0 ? $", {Declined} declined" : string.Empty) +
        (Errors.Count > 0 ? $", {Errors.Count} failed" : string.Empty);
}
