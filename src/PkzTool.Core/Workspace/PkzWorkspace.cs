using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Vfs;

namespace PkzTool.Core.Workspace;

/// <summary>
/// The loaded view: several PKZ archives presented as one virtual filesystem, with every edit staged in
/// memory until <see cref="Save"/> writes the modified archives back.
/// <para>
/// Loading replaces the whole view; there is no way to add a single archive to an existing one. A file
/// that fails to load is reported through <see cref="LoadResult.Failures"/> and skipped, so one bad
/// archive cannot cost the user the rest of a multi-select.
/// </para>
/// </summary>
public sealed class PkzWorkspace : IDisposable
{
    private readonly List<LoadedArchive> _archives = new();
    private readonly Dictionary<string, VfsNode> _folderIndex = new(VirtualPath.Comparer);
    private readonly Dictionary<string, VfsNode> _nodeIndex = new(StringComparer.Ordinal);

    /// <summary>
    /// Every row holding a given virtual path, so a collision test does not have to scan the tree. A
    /// path can be held by several archives at once, which is exactly the case a collision test exists
    /// to catch.
    /// </summary>
    private readonly Dictionary<string, List<VfsNode>> _pathIndex = new(VirtualPath.Comparer);

    /// <summary>The root of the merged tree. Its path is the empty string.</summary>
    public VfsNode Root { get; private set; } = VfsNode.Folder(string.Empty, string.Empty);

    /// <summary>The loaded archives, in load order.</summary>
    public IReadOnlyList<LoadedArchive> Archives => _archives;

    /// <summary>Path the configured Oodle dll was loaded from, or null. Required to read or write
    /// Oodle entries.</summary>
    public string? OodlePath { get; set; }

    /// <summary>
    /// When true, a save keeps the compression each entry asks for even when it does not reach the
    /// worthwhile threshold. This is the CLI's <c>--force-compression</c>; the GUI never sets it,
    /// because the rule is what the user sees in the compression column.
    /// </summary>
    public bool ForceCompression { get; set; }

    /// <summary>True when at least one archive has staged edits.</summary>
    public bool IsModified => _archives.Any(a => a.IsModified);

    /// <summary>How many files the merged view holds.</summary>
    public int FileCount => _nodeIndex.Count;

    /// <summary>The archives that would be written by <see cref="Save"/> or <see cref="SaveAs"/>.</summary>
    public IEnumerable<LoadedArchive> ModifiedArchives => _archives.Where(a => a.IsModified);

    /// <summary>Every file row in the tree, in display order.</summary>
    public IEnumerable<VfsNode> AllFiles => VfsTree.AllNodes(Root).Where(n => n.Kind == VfsNodeKind.File);

    /// <summary>Raised whenever the tree or the staged-edit set changes, so the shell can refresh.</summary>
    public event Action? Changed;

    private void RaiseChanged() => Changed?.Invoke();

    // --- loading --------------------------------------------------------------------------

    /// <summary>What happened during a load, so the shell can report skipped files.</summary>
    public sealed record LoadResult(int Loaded, int Failed, IReadOnlyList<(string Path, string Error)> Failures);

    /// <summary>
    /// Open every archive in <paramref name="paths"/> and replace the current view. An archive that
    /// cannot be read is skipped and its error collected, so the rest still load.
    /// <para>
    /// The new archives are opened <em>before</em> the current view is torn down, and the swap only
    /// happens once at least one of them opened. A load that fails completely therefore leaves the
    /// previous view — and any work staged in it — exactly as it was, instead of destroying it to
    /// replace it with nothing.
    /// </para>
    /// </summary>
    public LoadResult Load(IEnumerable<string> paths, string? oodlePath = null,
        CancellationToken cancellationToken = default)
    {
        if (oodlePath is not null) OodlePath = oodlePath;

        var failures = new List<(string, string)>();
        var opened = new List<LoadedArchive>();

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var archive = PkzArchive.Open(path);
                opened.Add(new LoadedArchive(archive, opened.Count) { TargetPath = archive.Path });
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                                           or UnauthorizedAccessException or EndOfStreamException
                                           or ArgumentException or NotSupportedException)
            {
                failures.Add((path, ex.Message));
            }
        }

        // Nothing opened: report the failures and keep what the user already had.
        if (opened.Count == 0) return new LoadResult(0, failures.Count, failures);

        // At least one opened, so this is the replacement the caller asked for.
        Clear();
        _archives.AddRange(opened);
        Rebuild();
        RaiseChanged();
        return new LoadResult(opened.Count, failures.Count, failures);
    }

    /// <summary>Close every archive and empty the view.</summary>
    public void Clear()
    {
        foreach (var archive in _archives) archive.Dispose();
        _archives.Clear();
        _nodeIndex.Clear();
        _folderIndex.Clear();
        Root = VfsNode.Folder(string.Empty, string.Empty);
    }

    /// <summary>
    /// Rebuild the merged tree from the archives and project the staged edits onto it, so a row's
    /// modified/compression/size state always matches what a save would write.
    /// </summary>
    private void Rebuild()
    {
        _nodeIndex.Clear();
        _folderIndex.Clear();
        Root = VfsTree.Build(_archives.Select(a => a.Archive).ToList());
        _folderIndex[string.Empty] = Root;
        foreach (var folder in VfsTree.AllFolders(Root))
            _folderIndex[folder.Path] = folder;

        foreach (var node in VfsTree.AllNodes(Root))
            _nodeIndex[node.Key] = node;

        // The tree only knows stored entries; this gives it the changes the user staged on top.
        ApplyStagedState();
        RebuildPathIndex();
    }

    /// <summary>
    /// Add a row to the path lookup. Used when a single row appears, so the caller does not have to
    /// rebuild the whole index to keep collision tests correct.
    /// </summary>
    private void IndexPath(VfsNode node)
    {
        if (!_pathIndex.TryGetValue(node.Path, out var rows))
        {
            rows = new List<VfsNode>(1);
            _pathIndex[node.Path] = rows;
        }
        if (!rows.Contains(node)) rows.Add(node);
    }

    /// <summary>
    /// Rebuild the path-to-rows lookup from the finished tree. It is built after the staged state is
    /// applied, so a row a rename moved is indexed under its new path and collision tests see the tree
    /// as the user does.
    /// </summary>
    private void RebuildPathIndex()
    {
        _pathIndex.Clear();
        foreach (var node in AllFiles) IndexPath(node);
    }

    /// <summary>
    /// Project every archive's staged state onto the tree. This is the only place a row's staged
    /// appearance is decided, so the view and the writer cannot disagree about what is pending: a row's
    /// bytes, compression and modified flag all come from its <see cref="StagedEntry"/>.
    /// </summary>
    private void ApplyStagedState()
    {
        foreach (var loaded in _archives)
        {
            foreach (var staged in loaded.Edits.Values)
            {
                if (staged.IsUntouched) continue;

                // The row is always found by the path the entry was *loaded* under, because that is the
                // row the tree was built with. A rename then moves that row rather than adding a second
                // one beside it, which is what would otherwise leave the old name on screen.
                var node = FindRow(loaded, staged.StoredPath);

                if (staged.IsDeleted)
                {
                    if (node is not null) RemoveRow(node);
                    continue;
                }

                if (node is null)
                {
                    // No row at the stored path means this is a brand-new file, so it gets one.
                    node = AddRow(loaded, staged.CurrentPath);
                    if (node is null) continue;
                }
                else if (!string.Equals(node.Path, staged.CurrentPath, StringComparison.Ordinal))
                {
                    // The entry has been renamed or moved, so the row is re-filed under its new path.
                    // Compared ordinally, so a spelling-only rename still shows.
                    node = MoveRow(loaded, node, staged.CurrentPath);
                    if (node is null) continue;
                }

                node.IsModified = true;
                node.Compression = staged.Compression;
                // The bytes are carried on the row only when they replace the stored ones; a recompress
                // keeps reading through to the archive.
                node.StagedData = staged.Data;
            }
        }
    }

    /// <summary>
    /// Re-file one existing row under a new path, creating any folder it now lives in. The row keeps its
    /// archive entry, so it still reads its bytes from the same place; only its name and position move.
    /// Returns null when a row already occupies the destination.
    /// </summary>
    private VfsNode? MoveRow(LoadedArchive loaded, VfsNode node, string newPath)
    {
        var folder = VfsTree.EnsureFolder(Root, _folderIndex, newPath);
        bool occupied = folder.ChildFilesCore.Any(f =>
            !ReferenceEquals(f, node) &&
            VirtualPath.Comparer.Equals(f.Path, newPath) &&
            ReferenceEquals(WrapperOf(f.Owner), loaded));
        if (occupied) return null;

        node.Parent?.ChildFilesCore.Remove(node);

        var moved = VfsNode.File(VfsNode.KeyForFile(newPath, null), newPath,
            VirtualPath.GetName(newPath));
        moved.Owner = loaded.Archive;
        moved.Entry = node.Entry;
        moved.Parent = folder;
        moved.IsShadowed = node.IsShadowed;
        folder.ChildFilesCore.Add(moved);

        _nodeIndex.Remove(node.Key);
        _nodeIndex[moved.Key] = moved;
        return moved;
    }

    /// <summary>The row that a staged path addresses within one archive.</summary>
    private VfsNode? FindRow(LoadedArchive loaded, string path)
    {
        // A suffix only exists when the path is duplicated, so the plain key is tried first.
        var plain = _nodeIndex.GetValueOrDefault(VfsNode.KeyForFile(path, null));
        if (plain is not null && ReferenceEquals(WrapperOf(plain.Owner), loaded)) return plain;

        foreach (var node in AllFiles)
            if (VirtualPath.Comparer.Equals(node.Path, path) &&
                ReferenceEquals(WrapperOf(node.Owner), loaded))
                return node;
        return null;
    }

    /// <summary>
    /// Remove one exact row from the tree. The node is passed in rather than looked up by path, because
    /// a path held by two archives has two rows and only the named one may go.
    /// </summary>
    private void RemoveRow(VfsNode node)
    {
        if (node.Parent is { } parent)
        {
            parent.ChildFilesCore.Remove(node);
        }
        else
        {
            // A node without a parent link would otherwise be recorded as deleted while still being
            // listed, so the fallback searches the tree rather than dropping the removal silently.
            foreach (var folder in VfsTree.AllFolders(Root))
                if (folder.ChildFilesCore.Remove(node))
                    break;
        }

        _nodeIndex.Remove(node.Key);
    }

    /// <summary>
    /// Create a row for a path that the built tree does not have yet: a brand-new file, or an entry a
    /// rename has moved elsewhere. The row's staged look is filled in by <see cref="ApplyStagedState"/>,
    /// so nothing about the edit is decided here. Returns null when a row for that path already exists
    /// in this archive, which would be a duplicate.
    /// </summary>
    private VfsNode? AddRow(LoadedArchive loaded, string path)
    {
        var folder = VfsTree.EnsureFolder(Root, _folderIndex, path);
        bool clash = folder.ChildFilesCore.Any(f =>
            VirtualPath.Comparer.Equals(f.Path, path) && ReferenceEquals(WrapperOf(f.Owner), loaded));
        if (clash) return null;

        var node = VfsNode.File(VfsNode.KeyForFile(path, null), path, VirtualPath.GetName(path));
        node.Owner = loaded.Archive;
        node.Parent = folder;
        folder.ChildFilesCore.Add(node);
        _nodeIndex[node.Key] = node;
        return node;
    }

    // --- queries --------------------------------------------------------------------------

    /// <summary>
    /// The <see cref="LoadedArchive"/> that owns an open <see cref="PkzArchive"/>. A node points at the
    /// archive it came from, while staging needs the wrapper that holds the edits, so this is the one
    /// place the two are bridged.
    /// </summary>
    private LoadedArchive? WrapperOf(PkzArchive? archive) =>
        archive is null ? null : _archives.FirstOrDefault(a => ReferenceEquals(a.Archive, archive));

    /// <summary>The row for a key, or null when it is gone.</summary>
    public VfsNode? FindByKey(string key) => _nodeIndex.GetValueOrDefault(key);

    /// <summary>The folder node for a path, or null when the tree has no such folder.</summary>
    public VfsNode? FindFolder(string path) => _folderIndex.GetValueOrDefault(path);

    /// <summary>
    /// Whether writing <paramref name="path"/> into <paramref name="archive"/> would collide with an
    /// existing entry there. Used by add and rename, which must ask before overwriting.
    /// <paramref name="ignoreKey"/> names a row to exempt, which is what a replace of one specific
    /// shadowed row does.
    /// <para>
    /// This is answered from <see cref="_pathIndex"/>, which maps a virtual path to the rows holding it.
    /// Scanning every row instead would make adding N files cost N², which a bulk add notices.
    /// </para>
    /// </summary>
    public bool WouldCollide(LoadedArchive archive, string path, string? ignoreKey = null)
    {
        if (!_pathIndex.TryGetValue(path, out var rows)) return false;

        foreach (var node in rows)
        {
            if (ignoreKey is not null && node.Key == ignoreKey) continue;
            if (ReferenceEquals(WrapperOf(node.Owner), archive)) return true;
        }
        return false;
    }

    /// <summary>
    /// The node a "the selected rows live in this folder" action should add into: the first selected
    /// folder, or the folder of the first selected file, or the root when nothing is selected.
    /// </summary>
    public VfsNode AddTargetFolder(IEnumerable<VfsNode> selection)
    {
        foreach (var node in selection)
            return node.Kind == VfsNodeKind.Folder
                ? node
                : FindFolder(node.Directory) ?? Root;
        return Root;
    }

    // --- staged mutations -----------------------------------------------------------------

    /// <summary>
    /// Stage an add or replace. <paramref name="replaceKey"/> names the exact row to replace; when null
    /// the path decides whether an entry is added or an existing one is overwritten. Returns false when
    /// the target archive already holds the path and <paramref name="allowOverwrite"/> is false, so the
    /// caller can prompt.
    /// </summary>
    public bool StageAddOrReplace(LoadedArchive archive, string path, byte[] data,
        EntryCompression compression, int level, bool allowOverwrite, string? replaceKey = null)
    {
        path = VirtualPath.Normalize(path) ?? throw new ArgumentException("the path is empty", nameof(path));
        bool exists = WouldCollide(archive, path, replaceKey);
        if (exists && !allowOverwrite) return false;

        // The row the edit applies to: the named one, else whatever row this archive has at the path.
        var node = replaceKey is not null ? FindByKey(replaceKey) : FindByPath(archive, path);

        if (node is not null)
        {
            // Editing an entry the archive already stores. The edit is filed under the entry's *stored*
            // path, because that is the entry's identity for the whole session no matter how often the
            // user renames it.
            if (StoredPathOf(archive, node) is { } stored)
            {
                archive.EntryFor(stored).Replace(data, compression, level);
                archive.Prune(stored);

                // The row is refreshed in place so the caller's node stays valid; a full rebuild would
                // replace every node in the tree and detach the reference it passed in.
                node.StagedData = data;
                node.Compression = compression;
                node.IsModified = true;
                RaiseChanged();
                return true;
            }
        }

        // A path the archive does not have. When an entry was deleted at this path earlier in the
        // session the stored entry still exists, so the add goes back into its place rather than
        // appearing twice.
        var deletedEntry = archive.Edits.Values.FirstOrDefault(e =>
            e.IsDeleted && VirtualPath.CompareEqual(e.CurrentPath, path));

        if (deletedEntry is not null)
        {
            // Bringing a deleted entry back means re-creating its row, which changes the shape of the
            // tree, so this is the one add that rebuilds.
            deletedEntry.ReAdd(data, compression, level, existedInArchive: true);
            Rebuild();
        }
        else
        {
            // A genuinely new file. Only a new row is needed, so the tree is extended rather than
            // rebuilt: a rebuild per added file would cost O(entries) each and make a bulk add of a few
            // thousand files take minutes.
            var staged = archive.EntryFor(path);
            staged.ReAdd(data, compression, level, existedInArchive: false);
            staged.StoredPathIsNew = true;

            var row = AddRow(archive, path);
            if (row is not null)
            {
                row.StagedData = data;
                row.Compression = compression;
                row.IsModified = true;
                IndexPath(row);
            }
        }

        RaiseChanged();
        return true;
    }

    /// <summary>
    /// The stored path an entry is filed under: the path it had in the archive, which is the key its
    /// edits use. A row knows its current path, so this maps back through the staged state.
    /// </summary>
    private static string? StoredPathOf(LoadedArchive archive, VfsNode node)
    {
        if (node.Entry is not null) return node.Entry.Name;

        // A row with no archive entry is either brand new or the result of a rename; both are found by
        // their current path in the staged state.
        var match = archive.Edits.Values.FirstOrDefault(e =>
            VirtualPath.CompareEqual(e.CurrentPath, node.Path));
        return match?.StoredPath;
    }

    /// <summary>The row for a path within one archive, or null.</summary>
    private VfsNode? FindByPath(LoadedArchive archive, string path)
    {
        if (!_pathIndex.TryGetValue(path, out var rows)) return null;
        foreach (var node in rows)
            if (ReferenceEquals(WrapperOf(node.Owner), archive))
                return node;
        return null;
    }

    /// <summary>Stage a recompress: the bytes stay as they are, the storage changes.</summary>
    public void StageRecompress(VfsNode node, EntryCompression compression, int level)
    {
        if (node.Kind != VfsNodeKind.File || WrapperOf(node.Owner) is not { } archive) return;
        if (StoredPathOf(archive, node) is not { } stored) return;

        // The stored compression is needed to tell a real change from one that asks for the storage the
        // entry already has, which is the case that must not force the archive to be rewritten.
        var storedCompression = node.Entry?.Compression ?? archive.Archive.Find(stored)?.Compression
            ?? EntryCompression.None;

        var staged = archive.EntryFor(stored);
        staged.Recompress(compression, level, storedCompression);
        archive.Prune(stored);

        // The row is updated in place rather than by rebuilding the tree, so the caller's node stays
        // valid: a full rebuild would replace every node and leave the reference it passed in detached.
        node.Compression = staged.Compression;
        node.IsModified = true;
        RaiseChanged();
    }

    /// <summary>Read a row's uncompressed bytes, decompressing through its archive when needed.</summary>
    public byte[] ReadData(VfsNode node)
    {
        if (node.StagedData is not null) return node.StagedData;
        if (node.Entry is null || node.Owner is null)
            throw new InvalidOperationException($"'{node.Path}' has no data to read");
        return node.Owner.Extract(node.Entry, OodlePath);
    }

    /// <summary>Stage a delete of exactly the rows given; folders take everything under them.</summary>
    public int StageDelete(IEnumerable<VfsNode> nodes)
    {
        int removed = 0;
        // Materialise first: deleting mutates the tree the enumerable walks.
        var files = nodes.SelectMany(n => n.DescendantFiles()).Distinct().ToList();
        foreach (var node in files)
        {
            if (WrapperOf(node.Owner) is not { } archive) continue;
            if (StoredPathOf(archive, node) is not { } stored) continue;
            archive.EntryFor(stored).Delete();
            RemoveRow(node);
            removed++;
        }
        if (removed > 0) RaiseChanged();
        return removed;
    }

    /// <summary>
    /// Stage a rename. On a file the last segment changes; on a folder the matching segment changes for
    /// every file under it. Returns the number of entries affected.
    /// <para>
    /// A rename is recorded against the entry's <em>stored</em> path and only changes its current path,
    /// so it composes with later edits instead of replacing them: renaming a file and then replacing its
    /// content keeps both, and renaming twice in a row simply moves the entry twice.
    /// </para>
    /// </summary>
    public int StageRename(VfsNode node, string newName)
    {
        if (VirtualPath.ValidateSegment(newName) is { } error)
            throw new ArgumentException(error, nameof(newName));

        int renamed = 0;
        if (node.Kind == VfsNodeKind.File)
        {
            if (WrapperOf(node.Owner) is not { } archive) return 0;
            if (StoredPathOf(archive, node) is not { } stored) return 0;

            var entry = archive.EntryFor(stored);
            var newPath = VirtualPath.WithName(entry.CurrentPath, newName);
            // Compared ordinally, so a case-only rename (Data.dat -> data.dat) is a real change rather
            // than being refused as a no-op.
            if (string.Equals(newPath, entry.CurrentPath, StringComparison.Ordinal)) return 0;

            entry.Rename(newPath);
            archive.Prune(stored);
            Rebuild();
            renamed++;
        }
        else
        {
            // A folder is a path prefix shared by many entries, and those entries may live in different
            // archives, so each owner gets its own rename.
            var newFolderPath = VirtualPath.WithName(node.Path, newName);
            var files = node.DescendantFiles().ToList();
            foreach (var file in files)
            {
                if (WrapperOf(file.Owner) is not { } archive) continue;
                if (StoredPathOf(archive, file) is not { } stored) continue;

                var entry = archive.EntryFor(stored);
                var newPath = VirtualPath.RenameFolderSegment(entry.CurrentPath, node.Path, newFolderPath);
                if (string.Equals(newPath, entry.CurrentPath, StringComparison.Ordinal)) continue;

                entry.Rename(newPath);
                archive.Prune(stored);
                renamed++;
            }

            if (renamed > 0) Rebuild();
        }

        if (renamed > 0) RaiseChanged();
        return renamed;
    }

    /// <summary>
    /// Stage a move of one file to a new virtual path. This is the drag-within-the-tree operation: the
    /// bytes are untouched and only the path changes, which inside a single archive is a rename.
    /// </summary>
    public void StageMove(VfsNode node, string newPath)
    {
        if (node.Kind != VfsNodeKind.File) return;
        if (WrapperOf(node.Owner) is not { } archive) return;
        if (StoredPathOf(archive, node) is not { } stored) return;

        newPath = VirtualPath.Normalize(newPath)
            ?? throw new ArgumentException("the destination path is empty", nameof(newPath));

        var entry = archive.EntryFor(stored);
        if (string.Equals(newPath, entry.CurrentPath, StringComparison.Ordinal)) return;

        entry.Rename(newPath);
        archive.Prune(stored);
        Rebuild();
        RaiseChanged();
    }

    /// <summary>Drop every staged edit, restoring the archives' stored state.</summary>
    public void DiscardEdits()
    {
        foreach (var archive in _archives) archive.ClearEdits();
        Rebuild();
        RaiseChanged();
    }

    /// <summary>
    /// Revert one row to the state it has in its archive, leaving every other staged change alone. This
    /// is the per-row undo a destructive tool needs: discarding everything just to undo one delete would
    /// lose unrelated work.
    /// </summary>
    public void RevertEdits(IEnumerable<VfsNode> nodes)
    {
        bool reverted = false;

        // Materialised first, because reverting a delete re-creates rows as it goes.
        foreach (var node in nodes.SelectMany(n => n.DescendantFiles()).Distinct().ToList())
        {
            if (WrapperOf(node.Owner) is not { } archive) continue;
            if (StoredPathOf(archive, node) is not { } stored) continue;
            if (archive.Unstage(stored)) reverted = true;
        }

        if (!reverted) return;

        // A revert can bring back a deleted entry or drop an added one, so the tree is rebuilt.
        Rebuild();
        RaiseChanged();
    }

    // --- saving ---------------------------------------------------------------------------

    /// <summary>How one archive's save went.</summary>
    public sealed record SaveOutcome(int Written, IReadOnlyList<(string Path, string Error)> Failures);

    /// <summary>
    /// Write every modified archive to its target, building each one in memory first and then replacing
    /// the original through a temp file. No backup is kept, and an archive with no edits is not touched
    /// at all, so its timestamp survives.
    /// <para>
    /// An archive whose write fails keeps its handle and, crucially, keeps its staged edits, so the user
    /// can fix the cause and save again. Only an archive that was actually written is reloaded from
    /// disk; discarding a failed archive's edits would silently destroy the very work the save was
    /// unable to write.
    /// </para>
    /// </summary>
    public SaveOutcome Save(Action<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targets = ModifiedArchives.ToList();
        var failures = new List<(string, string)>();
        int written = 0;

        for (int i = 0; i < targets.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loaded = targets[i];
            progress?.Invoke(new OperationProgress(i, targets.Count, $"Saving {loaded.FileName}"));
            try
            {
                // Every payload is read into memory while the handle is still open, because the archive
                // is then streamed to disk and the write needs no access to the source file.
                var packed = BuildArchiveEntries(loaded, (done, total, message) =>
                    progress?.Invoke(new OperationProgress(done, total, $"{loaded.FileName}: {message}")),
                    cancellationToken);

                string target = loaded.TargetPath;
                // Where the archive can still be read from if this write does not happen: its original
                // file, which for a Save As is not the same as the target.
                string source = loaded.Archive.Path;

                // Windows refuses to move a file over one this process still has open, even when shared
                // for delete, so the handle is released immediately before the replace. If the write
                // then fails the archive is reopened so the user is not left with a dead entry.
                loaded.Dispose();
                try
                {
                    PkzWriter.LayoutToFile(target, packed, null, cancellationToken);
                }
                catch
                {
                    // Reopen from the original file, keeping the staged edits, so the save can be fixed
                    // and retried rather than silently losing the work.
                    Reopen(loaded, source);
                    throw;
                }

                // The archive is now on disk, so the edits it carried are no longer pending. This is
                // the only place edits are cleared: a failed write above leaves them staged.
                Reopen(loaded, target);
                loaded.ClearEdits();
                written++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add((loaded.TargetPath, ex.Message));
            }
        }

        Rebuild();
        progress?.Invoke(new OperationProgress(targets.Count, targets.Count, "Save complete"));
        RaiseChanged();
        return new SaveOutcome(written, failures);
    }

    /// <summary>
    /// Point a loaded archive at a freshly opened handle for <paramref name="path"/>, keeping its staged
    /// edits and its position in the load order. A reopen that fails leaves the archive's handle as it
    /// was, so a failed save cannot also cost the user the view they were working in.
    /// </summary>
    private void Reopen(LoadedArchive loaded, string path)
    {
        try
        {
            loaded.Archive = PkzArchive.Open(path);
        }
        catch (Exception)
        {
            // The archive keeps whatever handle it had; a save failure is reported through the outcome
            // rather than being compounded by a reopen failure.
        }
    }

    /// <summary>
    /// Resolve the storage for one loaded archive, folding its staged entries into its entry order.
    /// <para>
    /// An entry the user did not touch is copied through with its stored bytes and its stored
    /// compression label, never re-encoded: a save must not silently re-compress files, because that
    /// would change bytes the user never asked to change and can make an archive much slower to build.
    /// Only an entry a staged change names goes through the codec.
    /// </para>
    /// <para>
    /// Each stored entry is looked up by the path it had when the archive was loaded, which is the key
    /// its staged state is filed under. That is what makes a rename compose: the entry's identity never
    /// changes, only the path it will be written to.
    /// </para>
    /// </summary>
    private List<PackedFile> BuildArchiveEntries(LoadedArchive loaded, Action<int, int, string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var packed = new List<PackedFile>();
        var edits = loaded.Edits;
        int total = loaded.Archive.Entries.Count + edits.Count;
        int done = 0;

        void Step(string message) => progress?.Invoke(++done, total, message);

        // Entry order is preserved: the archive's own order, with an edit applied where it lands and a
        // brand-new file appended in the order it was added. Sorting is a view concern and never
        // reaches the archive.
        foreach (var entry in loaded.Archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (edits.TryGetValue(entry.Name, out var staged) && !staged.IsUntouched)
            {
                if (staged.IsDeleted)
                {
                    Step("removing");
                    continue;
                }

                // A rename or a move leaves the stored bytes alone, so they are copied through with
                // their compression label: re-encoding to move a file would be slower, would need the
                // codec (and so the Oodle dll), and could change bytes nobody edited.
                bool bytesChanged = staged.HasNewData;
                bool storageChanged = staged.Compression != entry.Compression;
                string name = staged.CurrentPath;

                if (!bytesChanged && !storageChanged)
                {
                    packed.Add(new PackedFile(name,
                        loaded.Archive.ReadRaw(entry.DataOffset, entry.CompressedSize),
                        checked((int)entry.DecompressedSize), entry.Compression));
                    Step("moving");
                    continue;
                }

                // The bytes are re-encoded: either they were replaced, or the storage changed and the
                // original bytes have to be inflated and packed again.
                byte[] data = bytesChanged
                    ? staged.Data!
                    : loaded.Archive.Extract(entry, OodlePath);
                var (bytes, used) = CompressionCodec.Pack(data, staged.Compression, staged.Level,
                    OodlePath, ForceCompression, cancellationToken);
                packed.Add(new PackedFile(name, bytes, data.Length, used));
                Step("rewriting");
                continue;
            }

            packed.Add(new PackedFile(entry.Name, loaded.Archive.ReadRaw(entry.DataOffset, entry.CompressedSize),
                checked((int)entry.DecompressedSize), entry.Compression));
            Step("copying");
        }

        // A staged entry with no stored counterpart is a brand-new file, so it goes on the end in the
        // order it was added.
        foreach (var staged in edits.Values.Where(e => !e.IsUntouched && e.StoredPathIsNew))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var data = staged.Data ?? Array.Empty<byte>();
            var (bytes, used) = CompressionCodec.Pack(data, staged.Compression, staged.Level, OodlePath,
                ForceCompression, cancellationToken);
            packed.Add(new PackedFile(staged.CurrentPath, bytes, data.Length, used));
            Step("adding");
        }

        progress?.Invoke(total, total, "packed");
        return packed;
    }

    /// <summary>
    /// Save As: write each archive to the chosen path. The path only becomes the archive's target once
    /// the write has succeeded, so a Save As that fails leaves the archive still pointing at its
    /// original file and a plain Save does not then write to the failed destination.
    /// </summary>
    public SaveOutcome SaveAs(IReadOnlyDictionary<LoadedArchive, string> targets,
        Action<OperationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var previous = targets.Keys.ToDictionary(a => a, a => a.TargetPath);
        foreach (var (loaded, path) in targets)
            loaded.TargetPath = path;

        var outcome = Save(progress, cancellationToken);

        // A path that appears in the failure list did not get written, so that archive goes back to the
        // file it came from. Every other archive keeps its new location, which is what makes a later
        // plain Save write back to the Save As destination.
        var failed = outcome.Failures.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (loaded, path) in targets)
            if (failed.Contains(path) && previous.TryGetValue(loaded, out var was))
                loaded.TargetPath = was;

        return outcome;
    }

    public void Dispose() => Clear();
}
