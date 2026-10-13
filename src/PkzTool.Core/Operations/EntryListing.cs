using System.Globalization;
using System.Text;
using PkzTool.Core.Vfs;
using Xt1.Core.Util;

namespace PkzTool.Core.Operations;

/// <summary>
/// The column view for <c>--list</c>, shared with <c>--csv</c> so the console and the export always
/// agree. Formatting lives here rather than in the CLI so the GUI can reuse the same size spelling.
/// </summary>
public static class EntryListing
{
    /// <summary>The column headers, in order.</summary>
    public static readonly string[] Columns = { "Path", "Size", "Format", "Compression" };

    /// <summary>One row, rendered four ways.</summary>
    public readonly record struct Row(string Path, long Size, string Format, string Compression)
    {
        /// <summary>The size as a human-readable binary unit.</summary>
        public string SizeText => ByteSize.Format(Size);
    }

    /// <summary>Build the rows for a set of nodes, in the order given.</summary>
    public static List<Row> Rows(IEnumerable<VfsNode> nodes) =>
        nodes.Where(n => n.Kind == VfsNodeKind.File)
            .Select(n => new Row(EntrySelector.QualifiedName(n), n.UncompressedSize, n.Format,
                n.CompressionLabel))
            .ToList();

    /// <summary>
    /// Render as aligned plain-text columns. The width of each column is derived from the content, so
    /// the output stays readable whatever the paths look like.
    /// </summary>
    public static string ToText(IReadOnlyList<Row> rows)
    {
        var widths = new int[Columns.Length];
        widths[0] = Columns[0].Length;
        for (int i = 0; i < rows.Count; i++)
            widths[0] = Math.Max(widths[0], rows[i].Path.Length);

        // The size column is right-aligned, so its width is the widest formatted value or the header.
        int sizeWidth = Columns[1].Length;
        for (int i = 0; i < rows.Count; i++)
            sizeWidth = Math.Max(sizeWidth, rows[i].SizeText.Length);
        widths[1] = sizeWidth;
        widths[2] = Columns[2].Length;
        widths[3] = Columns[3].Length;
        for (int i = 0; i < rows.Count; i++)
        {
            widths[2] = Math.Max(widths[2], rows[i].Format.Length);
            widths[3] = Math.Max(widths[3], rows[i].Compression.Length);
        }

        var text = new StringBuilder();
        AppendRow(text, widths, Columns[0], Columns[1], Columns[2], Columns[3]);
        text.AppendLine(new string('-', widths[0] + widths[1] + widths[2] + widths[3] + 6));
        foreach (var row in rows)
            AppendRow(text, widths, row.Path, row.SizeText, row.Format, row.Compression);
        return text.ToString();
    }

    private static void AppendRow(StringBuilder text, int[] widths, string path, string size, string format,
        string compression)
    {
        text.Append(path.PadRight(widths[0])).Append("  ")
            .Append(size.PadLeft(widths[1])).Append("  ")
            .Append(format.PadRight(widths[2])).Append("  ")
            .Append(compression.PadRight(widths[3]))
            .AppendLine();
    }

    /// <summary>
    /// Render as CSV with a header row. A path is quoted and its quotes doubled when it contains a
    /// comma, a quote or a newline, which is the whole of the escaping this format needs.
    /// </summary>
    public static string ToCsv(IReadOnlyList<Row> rows)
    {
        var text = new StringBuilder();
        text.AppendLine(string.Join(',', Columns));
        foreach (var row in rows)
            text.AppendLine(string.Join(',',
                Escape(row.Path),
                Escape(row.Size.ToString(CultureInfo.InvariantCulture)),
                Escape(row.Format),
                Escape(row.Compression)));
        return text.ToString();
    }

    private static string Escape(string value) =>
        value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0
            ? value
            : '"' + value.Replace("\"", "\"\"") + '"';
}
