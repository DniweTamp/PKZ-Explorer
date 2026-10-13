using System.Text;
using System.Text.RegularExpressions;
using PkzTool.Core.Vfs;

namespace PkzTool.Core.Operations;

/// <summary>
/// Resolves the CLI's <c>--entry</c> arguments against the loaded view. An entry is a virtual path, a
/// folder, or a glob, always with forward slashes:
/// <list type="bullet">
/// <item><c>pl/pl0000.dat</c> — one exact path.</item>
/// <item><c>pl</c> — that folder and everything under it.</item>
/// <item><c>pl/*.dat</c> — a glob; <c>*</c> does not cross a separator, <c>**</c> does.</item>
/// </list>
/// </summary>
public static class EntrySelector
{
    /// <summary>
    /// The name a row is listed and addressed under: its full virtual path, plus the same
    /// " - name.pkz" suffix the GUI shows when another loaded archive also holds that path. The path is
    /// always included, because a listing of leaf names alone could not say which folder an entry is in.
    /// </summary>
    public static string QualifiedName(VfsNode node) => node.Path + (node.IsShadowed && node.Owner is not null
        ? VfsNode.ShadowSuffix(node.Owner.FileName)
        : string.Empty);

    /// <summary>
    /// Every name a selector may use for this row: the listed name, and — always — the bare path with
    /// this archive's suffix appended. The suffix form is accepted even for a row that is not shadowed,
    /// so <c>pl/pl0000.dat - data000.pkz</c> addresses the first copy of a duplicated path just as it
    /// addresses the second. Without that, the first copy would be unreachable by its archive name,
    /// because its listed name is the bare path.
    /// </summary>
    private static IEnumerable<string> NamesFor(VfsNode node)
    {
        yield return QualifiedName(node);
        yield return node.Path + VfsNode.ShadowSuffix(node.Owner?.FileName ?? string.Empty);
    }

    /// <summary>
    /// Match <paramref name="rows"/> against one selection string. Exact paths and folder prefixes win
    /// over glob interpretation, so a file whose name happens to contain a bracket is still selectable.
    /// </summary>
    public static List<VfsNode> Match(IEnumerable<VfsNode> rows, string selector)
    {
        var files = rows.Where(r => r.Kind == VfsNodeKind.File).ToList();
        var normalized = VirtualPath.Normalize(selector);
        if (normalized is null) return new List<VfsNode>();

        // An archive-qualified name addresses exactly one row. This is checked before the bare path so
        // that "pl/pl0000.dat - data000.pkz" selects the first copy rather than being read as a path.
        if (LooksArchiveQualified(normalized))
        {
            var byQualified = files
                .Where(f => NamesFor(f).Any(n => string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            return byQualified;
        }

        // An exact bare path matches every archive that holds it, which is what makes a plain
        // "--entry pl/pl0000.dat" act on all copies.
        var exact = files
            .Where(f => VirtualPath.Comparer.Equals(f.Path, normalized))
            .ToList();
        if (exact.Count > 0) return exact;

        // A folder selector takes the subtree; a path that names no folder but has no wildcard either
        // simply matches nothing, which the caller reports.
        if (!HasWildcard(normalized))
        {
            var under = files
                .Where(f => VirtualPath.IsUnderOrEqual(f.Path, normalized))
                .ToList();
            return under;
        }

        var regex = new Regex(GlobToRegex(normalized), RegexOptions.IgnoreCase);
        return files.Where(f => regex.IsMatch(f.Path)).ToList();
    }

    /// <summary>
    /// True when a selector carries the " - name.pkz" suffix. Detected by the separator rather than by
    /// looking the name up, so a genuine virtual path containing " - " is still tried as a path first
    /// when no row matches the qualified form.
    /// </summary>
    private static bool LooksArchiveQualified(string selector) =>
        selector.Contains(VfsNode.ShadowSuffix(string.Empty), StringComparison.Ordinal);

    /// <summary>Match any of several selectors, preserving the row order and dropping duplicates.</summary>
    public static List<VfsNode> MatchAll(IEnumerable<VfsNode> rows, IEnumerable<string> selectors)
    {
        var all = rows.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<VfsNode>();
        foreach (var selector in selectors)
            foreach (var node in Match(all, selector))
                if (seen.Add(node.Key))
                    result.Add(node);
        return result;
    }

    /// <summary>Report which selectors matched nothing, so the CLI can fail loudly.</summary>
    public static List<string> Unmatched(IEnumerable<VfsNode> rows, IEnumerable<string> selectors)
    {
        var all = rows.ToList();
        return selectors.Where(s => Match(all, s).Count == 0).ToList();
    }

    private static bool HasWildcard(string selector) =>
        selector.Contains('*') || selector.Contains('?') || selector.Contains('[');

    /// <summary>
    /// Translate a forward-slash glob into a regex. <c>**</c> crosses separators, <c>*</c> and <c>?</c>
    /// stay within one path segment, which is what a user typing <c>pl/*.dat</c> expects. Public so the
    /// glob semantics can be tested directly.
    /// </summary>
    public static string GlobToRegex(string glob)
    {
        var pattern = new StringBuilder("^");
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        pattern.Append(".*");
                        i++;
                    }
                    else
                    {
                        pattern.Append("[^/]*");
                    }
                    break;

                case '?':
                    pattern.Append("[^/]");
                    break;

                case '[':
                {
                    int close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        pattern.Append(Regex.Escape("["));
                        break;
                    }
                    string inner = glob[(i + 1)..close];
                    // A leading ! or ^ negates, matching the common shell convention.
                    if (inner.StartsWith('!')) inner = "^" + inner[1..];
                    pattern.Append('[').Append(inner).Append(']');
                    i = close;
                    break;
                }

                default:
                    pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        pattern.Append('$');
        return pattern.ToString();
    }
}
