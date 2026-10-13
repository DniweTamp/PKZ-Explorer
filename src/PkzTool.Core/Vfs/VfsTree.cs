using PkzTool.Core.Archives;

namespace PkzTool.Core.Vfs;

/// <summary>
/// Builds one continuous virtual filesystem out of several loaded PKZ archives. There is deliberately
/// no per-archive separation in the tree: a path that exists in two archives is two rows in the same
/// folder, the second and later ones suffixed with " - name.pkz".
/// <para>
/// Folder rows are synthesised from the paths; an archive's entries are visited in stored order, which
/// is the order the list view shows by default and the order a save preserves.
/// </para>
/// </summary>
public static class VfsTree
{
    /// <summary>
    /// Merge <paramref name="archives"/> into a tree. The root node's <see cref="VfsNode.Path"/> is
    /// the empty string and its children are the top level of the virtual filesystem.
    /// </summary>
    public static VfsNode Build(IReadOnlyList<PkzArchive> archives)
    {
        var root = VfsNode.Folder(string.Empty, string.Empty);
        var folderIndex = new Dictionary<string, VfsNode>(VirtualPath.Comparer) { [string.Empty] = root };
        // Which archive file names have already claimed each path, so later ones get the suffix.
        var claimed = new Dictionary<string, HashSet<string>>(VirtualPath.Comparer);

        foreach (var archive in archives)
        {
            foreach (var entry in archive.Entries)
            {
                var path = VirtualPath.Normalize(entry.Name);
                // An entry whose name normalises away (empty, or only separators) cannot be addressed
                // or extracted; skipping it keeps the tree sound rather than inventing a nameless row.
                if (path is null) continue;

                var folder = EnsureFolder(root, folderIndex, path);
                bool firstOwner = !claimed.TryGetValue(path, out var owners);
                if (firstOwner)
                {
                    owners = new HashSet<string>(VirtualPath.Comparer);
                    claimed[path] = owners;
                }

                var display = VirtualPath.GetName(path);
                // The first archive to hold a path keeps the plain name; every later one is suffixed so
                // the user can tell the rows apart and aim an edit at exactly one of them.
                string? suffixOwner = firstOwner ? null : archive.FileName;
                if (suffixOwner is not null) display += VfsNode.ShadowSuffix(suffixOwner);
                owners!.Add(archive.FileName);

                var node = VfsNode.File(VfsNode.KeyForFile(path, suffixOwner), path, display);
                node.Owner = archive;
                node.Entry = entry;
                node.IsShadowed = suffixOwner is not null;
                node.Compression = entry.Compression;
                // The parent link is what lets a later edit remove or re-file this exact row; without it
                // a delete would be recorded but the tree would still list the entry.
                node.Parent = folder;
                folder.ChildFilesCore.Add(node);
            }
        }

        // A path held by more than one archive marks the *first* row as shadowed too, so the UI can
        // colour every row of a duplicated path and the user knows a save writes both.
        var duplicated = new HashSet<string>(claimed.Where(p => p.Value.Count > 1).Select(p => p.Key),
            VirtualPath.Comparer);
        if (duplicated.Count > 0)
            foreach (var folder in AllFolders(root))
                foreach (var file in folder.ChildFiles)
                    if (duplicated.Contains(file.Path))
                        file.IsShadowed = true;

        return root;
    }

    /// <summary>Every folder in the tree, depth first, including the root.</summary>
    public static IEnumerable<VfsNode> AllFolders(VfsNode root)
    {
        yield return root;
        foreach (var folder in root.DescendantFolders()) yield return folder;
    }

    /// <summary>Every row in the tree, folders and files, in display order.</summary>
    public static IEnumerable<VfsNode> AllNodes(VfsNode root)
    {
        foreach (var folder in AllFolders(root))
        {
            yield return folder;
            foreach (var file in folder.ChildFiles) yield return file;
        }
    }

    /// <summary>
    /// Find the folder node for <paramref name="path"/>, creating any missing ancestors so a file can
    /// be added into a folder the tree has not seen yet.
    /// </summary>
    public static VfsNode EnsureFolder(VfsNode root, Dictionary<string, VfsNode> index, string path)
    {
        var directory = VirtualPath.GetDirectory(path);
        return EnsureFolderPath(root, index, directory);
    }

    /// <summary>Find (or create) the folder node with exactly this path.</summary>
    public static VfsNode EnsureFolderPath(VfsNode root, Dictionary<string, VfsNode> index, string folderPath)
    {
        if (folderPath.Length == 0) return root;
        if (index.TryGetValue(folderPath, out var existing)) return existing;

        var parent = EnsureFolderPath(root, index, VirtualPath.GetDirectory(folderPath));
        var node = VfsNode.Folder(folderPath, VirtualPath.GetName(folderPath));
        node.Parent = parent;
        parent.ChildFoldersCore.Add(node);
        index[folderPath] = node;
        return node;
    }
}
