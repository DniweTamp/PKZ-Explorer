namespace PkzTool.Core.Vfs;

/// <summary>
/// Virtual-path arithmetic for the PKZ filesystem. Everything the user sees uses forward slashes, and
/// the comparison is case-insensitive because the engine lower-cases names when it builds its index.
/// Nothing here touches the real filesystem; extraction maps a virtual path to a relative path
/// separately, in <c>ExtractionPlanner</c>.
/// </summary>
public static class VirtualPath
{
    /// <summary>The separator every virtual path uses, in the model and in the UI.</summary>
    public const char Separator = '/';

    /// <summary>Comparison used for every lookup and collision test.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Normalise a path typed or dragged in: backslashes become forward slashes, repeated separators
    /// collapse, and a leading or trailing separator is dropped. Each segment is trimmed of ASCII
    /// whitespace. Returns null when nothing usable remains.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var segments = path.Replace('\\', Separator)
            .Split(Separator, StringSplitOptions.RemoveEmptyEntries);

        var cleaned = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;
            // "." and ".." have no meaning inside an archive; a literal name that happens to be one of
            // them would be unreachable anyway, so they are dropped rather than allowed to escape.
            if (trimmed is "." or "..") continue;
            cleaned.Add(trimmed);
        }

        return cleaned.Count == 0 ? null : string.Join(Separator, cleaned);
    }

    /// <summary>The folder part of a virtual path, or the empty string when it is at the root.</summary>
    public static string GetDirectory(string path)
    {
        int at = path.LastIndexOf(Separator);
        return at < 0 ? string.Empty : path[..at];
    }

    /// <summary>The last segment of a virtual path (its file or folder name).</summary>
    public static string GetName(string path)
    {
        int at = path.LastIndexOf(Separator);
        return at < 0 ? path : path[(at + 1)..];
    }

    /// <summary>The extension of the last segment, including the dot; empty when there is none.</summary>
    public static string GetExtension(string path) => Path.GetExtension(GetName(path));

    /// <summary>
    /// The file extension in upper case and without the dot, which is what the "File format" column
    /// shows. An archive member with no extension reads as "—" rather than an empty cell.
    /// </summary>
    public static string FormatLabel(string path)
    {
        var extension = GetExtension(path);
        if (extension.Length == 0) return "\u2014";
        var trimmed = extension.TrimStart('.').ToUpperInvariant();
        return trimmed.Length == 0 ? "\u2014" : trimmed;
    }

    /// <summary>Combine a folder path and a leaf name, tolerating an empty folder (the root).</summary>
    public static string Combine(string? folder, string leaf) =>
        string.IsNullOrEmpty(folder) ? leaf : folder + Separator + leaf;

    /// <summary>The folder's own path plus a separator, for prefix tests.</summary>
    public static string WithTrailingSeparator(string folder) =>
        folder.Length == 0 ? string.Empty : folder + Separator;

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (or is it).</summary>
    public static bool IsUnderOrEqual(string path, string folder)
    {
        if (folder.Length == 0) return true;
        if (path.Equals(folder, StringComparison.OrdinalIgnoreCase)) return true;
        return path.StartsWith(WithTrailingSeparator(folder), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replace the last segment of <paramref name="path"/> with <paramref name="newName"/>, keeping
    /// its folder. Used by rename, which on a file changes the file name and on a folder a path
    /// segment.
    /// </summary>
    public static string WithName(string path, string newName) =>
        Combine(GetDirectory(path), newName);

    /// <summary>
    /// Rewrite a path that sits under <paramref name="folder"/> so the segment matching that folder's
    /// last segment becomes <paramref name="newSegment"/>. Renaming <c>a/b</c> to <c>a/c</c> therefore
    /// turns <c>a/b/d.dat</c> into <c>a/c/d.dat</c>.
    /// </summary>
    public static string RenameFolderSegment(string path, string folder, string newSegment)
    {
        if (folder.Length == 0) return path;
        return path.Length >= folder.Length &&
               path.AsSpan(0, folder.Length).Equals(folder, StringComparison.OrdinalIgnoreCase)
            ? newSegment + path[folder.Length..]
            : path;
    }

    /// <summary>All ancestor folders of a path, outermost first, excluding the path itself.</summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        int at = path.IndexOf(Separator);
        while (at >= 0)
        {
            yield return path[..at];
            at = path.IndexOf(Separator, at + 1);
        }
    }

    /// <summary>The depth of a path: root entries are 0, one folder down is 1, and so on.</summary>
    public static int Depth(string path)
    {
        int depth = 0;
        foreach (var _ in path)
            if (_ == Separator) depth++;
        return depth;
    }

    /// <summary>Validate a single segment (a file or folder name the user typed). Returns null when it
    /// is acceptable, otherwise the reason. Only genuinely impossible names are rejected, so the tool
    /// never refuses a name the game itself could carry.</summary>
    public static string? ValidateSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "the name is empty";
        if (name.Contains(Separator)) return "the name cannot contain a forward slash";
        if (name.Contains('\\')) return "the name cannot contain a backslash";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "the name contains characters that are not allowed";
        if (name is "." or "..") return "the name cannot be '.' or '..'";
        return null;
    }

    /// <summary>Compare two virtual paths the way every lookup does (case-insensitively).</summary>
    public static bool CompareEqual(string? a, string? b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
