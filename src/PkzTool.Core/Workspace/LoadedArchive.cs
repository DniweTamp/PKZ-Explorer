using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Vfs;

namespace PkzTool.Core.Workspace;

/// <summary>
/// One loaded PKZ archive plus the list of edits staged against it. A workspace owns one of these per
/// archive; the merged tree in <see cref="PkzWorkspace"/> spans all of them.
/// <para>
/// Nothing here writes to disk. Every mutation records a <see cref="StagedEdit"/> keyed by the virtual
/// path, and <see cref="PkzWorkspace.Save"/> folds the edits back into the archive's entry order.
/// </para>
/// </summary>
public sealed class LoadedArchive : IDisposable
{
    /// <summary>
    /// The edits staged against this archive, each identified by the original stored path of the entry
    /// it applies to. A path maps to one <see cref="StagedEntry"/>, which holds the *composed* result of
    /// every edit applied to that entry, so there is one source of truth for what a save will write.
    /// </summary>
    private readonly Dictionary<string, StagedEntry> _edits = new(Vfs.VirtualPath.Comparer);

    internal LoadedArchive(PkzArchive archive, int loadIndex)
    {
        Archive = archive;
        LoadIndex = loadIndex;
    }

    /// <summary>
    /// The open archive. Settable only from inside the workspace, and only so a successful save can swap
    /// in a handle on the freshly written file; the staged edits live on this object, so replacing the
    /// handle must never mean replacing the wrapper.
    /// </summary>
    public PkzArchive Archive { get; internal set; }

    /// <summary>Position in the load order, which decides which archive wins a duplicate path.</summary>
    public int LoadIndex { get; }

    /// <summary>Full path of the archive on disk.</summary>
    public string Path => Archive.Path;

    /// <summary>Bare file name, used for the shadowing suffix and the window title.</summary>
    public string FileName => Archive.FileName;

    /// <summary>
    /// The path the archive would be written to. "Save As" changes this; the archive's own read handle
    /// keeps pointing at the original until the next save, which is fine because saves rewrite whole
    /// files through a temp file.
    /// </summary>
    public string TargetPath { get; set; } = string.Empty;

    /// <summary>True when any edit is staged against this archive.</summary>
    public bool IsModified => _edits.Count > 0;

    /// <summary>
    /// The staged entries, keyed by the path the entry had when it was loaded. The value describes the
    /// final state, not an individual operation.
    /// </summary>
    public IReadOnlyDictionary<string, StagedEntry> Edits => _edits;

    /// <summary>How many individual operations have been applied across every staged entry.</summary>
    public int EditCount => _edits.Values.Sum(e => e.Operations);

    /// <summary>
    /// Get the staged state for an entry, creating an empty one when the entry has not been touched.
    /// </summary>
    internal StagedEntry EntryFor(string storedPath)
    {
        if (!_edits.TryGetValue(storedPath, out var entry))
        {
            entry = new StagedEntry(storedPath);
            _edits[storedPath] = entry;
        }
        return entry;
    }

    /// <summary>
    /// Drop the staged state for a path when it has become a no-op, so an archive with nothing to write
    /// is not rewritten and its timestamp survives.
    /// </summary>
    internal void Prune(string storedPath)
    {
        if (_edits.TryGetValue(storedPath, out var entry) && entry.IsNoOp) _edits.Remove(storedPath);
    }

    /// <summary>
    /// Drop the staged state for one entry, so it reverts to what the archive stores. Returns false when
    /// nothing was staged against it, which lets a caller tell whether anything actually changed.
    /// </summary>
    internal bool Unstage(string storedPath) => _edits.Remove(storedPath);

    /// <summary>Discard every staged edit.</summary>
    internal void ClearEdits() => _edits.Clear();

    /// <summary>The staged entries that actually change something, for the writer and the counts.</summary>
    internal IEnumerable<StagedEntry> EffectiveEdits => _edits.Values.Where(e => !e.IsNoOp);

    public void Dispose() => Archive.Dispose();
}

/// <summary>
/// The staged result for one stored entry, built by applying edit operations in the order the user made
/// them. This is the single source of truth for what a save writes: the tree's rows are a view of these
/// values, never a parallel copy.
/// <para>
/// Composing rather than overwriting is what makes a sequence like "replace, then recompress" mean both
/// things — the new bytes stored with the new compression — instead of the second edit silently
/// discarding the first.
/// </para>
/// </summary>
public sealed class StagedEntry
{
    internal StagedEntry(string storedPath)
    {
        StoredPath = storedPath;
        CurrentPath = storedPath;
        Compression = EntryCompression.None;
    }

    /// <summary>The path the entry had in the archive; this is the key the edit is filed under.</summary>
    public string StoredPath { get; }

    /// <summary>The path the entry will have after the save, following any rename or move.</summary>
    public string CurrentPath { get; private set; }

    /// <summary>True when the entry will be removed.</summary>
    public bool IsDeleted { get; private set; }

    /// <summary>
    /// True when the entry existed in the archive and is being put back by an add. Such an entry stays
    /// in its original position rather than being appended, because from the user's point of view they
    /// added a file over one they had just deleted.
    /// </summary>
    public bool IsReAdd { get; private set; }

    /// <summary>The replacement bytes, or null when the stored bytes are kept.</summary>
    public byte[]? Data { get; private set; }

    /// <summary>True when <see cref="Data"/> is set and the bytes must be re-encoded.</summary>
    public bool HasNewData => Data is not null;

    /// <summary>The compression to store with; only meaningful once the entry is touched at all.</summary>
    public EntryCompression Compression { get; private set; }

    /// <summary>The codec level for <see cref="Compression"/>.</summary>
    public int Level { get; private set; }

    /// <summary>
    /// True when this entry has no counterpart in the archive, so it is a brand-new file that must be
    /// appended rather than replacing an existing entry in place. Set when the entry is first staged.
    /// </summary>
    public bool StoredPathIsNew { get; internal set; }

    /// <summary>How many operations have been applied; used for the "N staged changes" count.</summary>
    public int Operations { get; private set; }

    /// <summary>True when the entry has never been touched.</summary>
    public bool IsUntouched => Operations == 0;

    /// <summary>
    /// True when applying this entry would change nothing, so the archive need not be rewritten for it.
    /// A rename back to the original name, or a recompress to the storage the entry already had, leaves
    /// the archive byte-identical, and a save that rewrote it anyway would move the file's timestamp for
    /// no reason.
    /// <para>
    /// The path comparison is ordinal, not the case-insensitive one used for lookups: a rename that only
    /// changes the spelling of a name (<c>Data.dat</c> to <c>data.dat</c>) is a real change, because the
    /// spelling is what the archive stores.
    /// </para>
    /// </summary>
    public bool IsNoOp =>
        !IsDeleted &&
        string.Equals(CurrentPath, StoredPath, StringComparison.Ordinal) &&
        !HasNewData &&
        !TouchChangesStorage;

    /// <summary>
    /// True when this entry's storage differs from what the archive already has. The workspace sets it
    /// when it stages an edit, because it is the only place that knows the entry's stored compression.
    /// A pure recompress is exactly this case, which is why it cannot be inferred from the other fields.
    /// </summary>
    public bool TouchChangesStorage { get; internal set; }

    /// <summary>Replace the bytes and set the storage for this entry.</summary>
    internal void Replace(byte[] data, EntryCompression compression, int level)
    {
        Data = data;
        Compression = compression;
        Level = level;
        IsDeleted = false;
        Operations++;
    }

    /// <summary>
    /// Set the storage without touching the bytes. <paramref name="storedCompression"/> is what the
    /// archive already has, so that asking for the storage an entry already uses is recognised as
    /// changing nothing and the archive is not rewritten for it.
    /// </summary>
    internal void Recompress(EntryCompression compression, int level, EntryCompression storedCompression)
    {
        Compression = compression;
        Level = level;
        IsDeleted = false;
        TouchChangesStorage = compression != storedCompression;
        Operations++;
    }

    /// <summary>Change the path, keeping the bytes and the storage.</summary>
    internal void Rename(string newPath)
    {
        CurrentPath = newPath;
        Operations++;
    }

    /// <summary>Mark the entry removed.</summary>
    internal void Delete()
    {
        IsDeleted = true;
        Data = null;
        Operations++;
    }

    /// <summary>
    /// Put an entry back that was previously deleted, or stage a brand-new one. The caller says whether
    /// the archive ever had this path, which decides whether the entry keeps its original position or is
    /// appended.
    /// </summary>
    internal void ReAdd(byte[] data, EntryCompression compression, int level, bool existedInArchive)
    {
        Data = data;
        Compression = compression;
        Level = level;
        IsDeleted = false;
        IsReAdd = existedInArchive;
        StoredPathIsNew = !existedInArchive;
        CurrentPath = existedInArchive ? StoredPath : CurrentPath;
        Operations++;
    }

    /// <summary>
    /// A one-line description of the entry's staged state, for the status bar and the CLI log. It
    /// describes the *result*, so a row edited twice reads as what will actually be written.
    /// </summary>
    public string Describe()
    {
        if (IsDeleted) return $"delete {StoredPath}";

        var parts = new List<string>();
        if (!VirtualPath.CompareEqual(CurrentPath, StoredPath)) parts.Add($"rename to {CurrentPath}");
        if (HasNewData) parts.Add("replace");
        else if (Operations > 0) parts.Add("recompress");
        parts.Add(CompressionCodec.DisplayName(Compression));

        return $"{StoredPath}: {string.Join(", ", parts)}";
    }
}


