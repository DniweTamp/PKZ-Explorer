using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Workspace;

namespace PkzTool.Core.Vfs;

/// <summary>
/// Whether a row in the list view is a folder or a file.
/// </summary>
public enum VfsNodeKind
{
    Folder,
    File,
}

/// <summary>
/// One row of the virtual filesystem: either a folder in the merged tree or a single file.
/// <para>
/// A file row is one entry of one archive. When several loaded archives hold the same path, each gets
/// its own row and every one but the first is <see cref="VfsNode.IsShadowed"/> with
/// <see cref="VfsNode.DisplayName"/> carrying the " - name.pkz" suffix. The node's
/// <see cref="VfsNode.Key"/> is the identity used everywhere in the UI and by staged edits.
/// </para>
/// </summary>
public sealed class VfsNode
{
    private VfsNode(string key, string path, string displayName, VfsNodeKind kind)
    {
        Key = key;
        Path = path;
        DisplayName = displayName;
        Kind = kind;
    }

    /// <summary>
    /// Stable identity of this row, unique across the whole loaded view. For a file this is
    /// <c>path</c> joined with the owning archive's file name when several archives share the path, so
    /// that an operation aimed at one row touches exactly that row's entry and nothing else.
    /// </summary>
    public string Key { get; }

    /// <summary>The virtual path, forward slashes, no archive suffix. This is what is written to an
    /// archive and what the folder tree is built from.</summary>
    public string Path { get; }

    /// <summary>What the list view shows in the name column: the leaf name, plus the " - name.pkz"
    /// suffix when this row is shadowed by an identically named entry in another archive.</summary>
    public string DisplayName { get; private init; }

    /// <summary>Folder or file.</summary>
    public VfsNodeKind Kind { get; }

    /// <summary>The folder this node lives in; the empty string at the root.</summary>
    public string Directory => VirtualPath.GetDirectory(Path);

    /// <summary>The leaf name without any suffix.</summary>
    public string LeafName => VirtualPath.GetName(Path);

    /// <summary>The archive backing this row, or null for a file added in this session.</summary>
    public PkzArchive? Owner { get; internal set; }

    /// <summary>Stable wrapper reference across archive handle reopens during Save.</summary>
    internal LoadedArchive? LoadedOwner { get; set; }

    /// <summary>The archive entry backing this row, or null for an added file.</summary>
    public PkzEntry? Entry { get; internal set; }

    /// <summary>True when another loaded archive also holds <see cref="Path"/>.</summary>
    public bool IsShadowed { get; internal set; }

    /// <summary>True when this row has staged changes that are not yet written to disk.</summary>
    public bool IsModified { get; internal set; }

    /// <summary>The staged bytes for an added or replaced file; null when unchanged.</summary>
    public byte[]? StagedData { get; internal set; }

    /// <summary>The compression this row would be written with after staging; for an unchanged row
    /// this is the archive's own value.</summary>
    public EntryCompression Compression { get; internal set; }

    /// <summary>
    /// Uncompressed size, which is what the size column shows. A staged replacement uses the staged
    /// length immediately so the column reflects the edit before it is saved.
    /// </summary>
    public long UncompressedSize => StagedData?.LongLength ?? Entry?.DecompressedSize ?? 0;

    /// <summary>Stored size in the archive; 0 for a node that has no archive backing yet.</summary>
    public long CompressedSize => Entry?.CompressedSize ?? 0;

    /// <summary>The "File format" column: the file extension only, upper case, no dot.</summary>
    public string Format => Kind == VfsNodeKind.Folder ? string.Empty : VirtualPath.FormatLabel(Path);

    /// <summary>The compression label shown in the list, or empty for a folder.</summary>
    public string CompressionLabel => Kind == VfsNodeKind.Folder
        ? string.Empty
        : CompressionCodec.DisplayName(Compression);

    /// <summary>Child folders, keyed by their full path, in the merge's insertion order.</summary>
    internal List<VfsNode> ChildFoldersCore { get; } = new();

    /// <summary>Child files, in the order the merge produced them (i.e. PKZ order).</summary>
    internal List<VfsNode> ChildFilesCore { get; } = new();

    /// <summary>Child folders, in insertion order. Read-only to callers; the tree owns the list.</summary>
    public IReadOnlyList<VfsNode> ChildFolders => ChildFoldersCore;

    /// <summary>Child files, in the order the archives stored them. Read-only to callers.</summary>
    public IReadOnlyList<VfsNode> ChildFiles => ChildFilesCore;

    /// <summary>The parent folder, or null at the root.</summary>
    public VfsNode? Parent { get; internal set; }

    /// <summary>True when the folder has no children at all.</summary>
    public bool IsEmptyFolder => Kind == VfsNodeKind.Folder &&
                                 ChildFoldersCore.Count == 0 && ChildFilesCore.Count == 0;

    /// <summary>Every file under this node, depth first, in stored order. A file yields itself.</summary>
    public IEnumerable<VfsNode> DescendantFiles()
    {
        if (Kind == VfsNodeKind.File)
        {
            yield return this;
            yield break;
        }

        foreach (var file in ChildFilesCore) yield return file;
        foreach (var folder in ChildFoldersCore)
            foreach (var file in folder.DescendantFiles())
                yield return file;
    }

    /// <summary>Every folder under this node, depth first, excluding the node itself.</summary>
    public IEnumerable<VfsNode> DescendantFolders()
    {
        foreach (var folder in ChildFoldersCore)
        {
            yield return folder;
            foreach (var nested in folder.DescendantFolders()) yield return nested;
        }
    }

    /// <summary>
    /// A folder row for an addition target that exists on disk but not yet in the tree. Present so a
    /// drag onto an expanded, genuinely empty folder works; the merge normally creates folders only
    /// where an entry needs one.
    /// </summary>
    internal static VfsNode Folder(string path, string displayName) =>
        new(KeyForFolder(path), path, displayName, VfsNodeKind.Folder);

    /// <summary>Create a file row.</summary>
    internal static VfsNode File(string key, string path, string displayName) =>
        new(key, path, displayName, VfsNodeKind.File);

    /// <summary>The key a folder is identified by; folders are unique per path by construction.</summary>
    public static string KeyForFolder(string path) => "dir:" + path;

    /// <summary>
    /// The key for a file row. The first archive to contribute a path owns the plain key, so an
    /// operation on an unsuffixed row addresses the entry the load order put first.
    /// <paramref name="archiveFileName"/> is null for the first occurrence.
    /// </summary>
    public static string KeyForFile(string path, string? archiveFileName) =>
        archiveFileName is null ? "file:" + path : "file:" + path + " - " + archiveFileName;

    /// <summary>The suffix appended to a shadowed row's name.</summary>
    public static string ShadowSuffix(string archiveFileName) => " - " + archiveFileName;

    /// <summary>Folder names carry a trailing separator in the display so they read as folders.</summary>
    public override string ToString() =>
        Kind == VfsNodeKind.Folder ? DisplayName + VirtualPath.Separator : DisplayName;
}
