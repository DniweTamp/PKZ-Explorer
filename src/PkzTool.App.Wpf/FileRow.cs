using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using PkzTool.App.Wpf.Theming;
using PkzTool.Core.Vfs;
using Xt1.Core.Util;

namespace PkzTool.App.Wpf;

/// <summary>
/// One row of the list. The grid is a flat list, so a folder's place in the tree is carried by its
/// <see cref="Depth"/> (indentation) and its expansion state, not by a nested control.
/// <para>
/// The properties are read-only projections of the underlying <see cref="VfsNode"/>; the row raises
/// change notifications when either the node or the view's sort/filter settings change, so the grid
/// re-reads them.
/// </para>
/// </summary>
public sealed class FileRow : INotifyPropertyChanged
{
    private const double IndentPerLevel = 15;

    /// <summary>The tree node this row shows.</summary>
    public VfsNode Node { get; }

    /// <summary>How deep the node sits, used for the indentation.</summary>
    public int Depth { get; }

    /// <summary>True while this folder's children are on screen.</summary>
    public bool IsExpanded { get; private set; }

    public FileRow(VfsNode node, int depth, bool isExpanded)
    {
        Node = node;
        Depth = depth;
        IsExpanded = isExpanded;
    }

    /// <summary>True when the row is a folder.</summary>
    public bool IsFolder => Node.Kind == VfsNodeKind.Folder;

    /// <summary>The name column's text: the leaf name, with the archive suffix when shadowed.</summary>
    public string DisplayName => Node.DisplayName;

    /// <summary>The uncompressed size, formatted as a binary unit.</summary>
    public string SizeText => IsFolder ? string.Empty : ByteSize.Format(Node.UncompressedSize);

    /// <summary>The file extension only, e.g. "DAT"; empty for a folder.</summary>
    public string Format => IsFolder ? string.Empty : Node.Format;

    /// <summary>The compression label; empty for a folder.</summary>
    public string Compression => IsFolder ? string.Empty : Node.CompressionLabel;

    /// <summary>Left margin that turns the flat grid into a folder tree.</summary>
    public Thickness Indent => new(Depth * IndentPerLevel + 2, 0, 0, 0);

    /// <summary>Folder glyph, or a file glyph matched loosely to the extension.</summary>
    public string IconGlyph => IsFolder ? "\uE8B7" : GlyphForExtension(Node.Format);

    /// <summary>The expand/collapse chevron drawn beside a folder.</summary>
    public string ChevronGlyph => IsFolder ? (IsExpanded ? "\uE70D" : "\uE76C") : string.Empty;

    /// <summary>Only a folder that has children can be expanded, so only those show a chevron.</summary>
    public Visibility ChevronVisibility =>
        IsFolder && !Node.IsEmptyFolder ? Visibility.Visible : Visibility.Hidden;

    /// <summary>The "*" that marks a staged, unsaved change.</summary>
    public string ModifiedMark => Node.IsModified ? "*" : string.Empty;

    /// <summary>A muted name for a folder, the normal text colour for a file.</summary>
    public Brush NameBrush => IsFolder ? ThemeManager.Brush("Brush.Heading") : ThemeManager.Brush("Brush.Text");

    /// <summary>Staged rows are emphasised, so an unsaved edit is visible at a glance.</summary>
    public FontWeight NameWeight => Node.IsModified ? FontWeights.SemiBold : FontWeights.Normal;

    /// <summary>A staged row's size is tinted, matching the name's emphasis.</summary>
    public Brush SizeBrush => Node.IsModified
        ? ThemeManager.Brush("Brush.Accent")
        : ThemeManager.Brush("Brush.Muted");

    /// <summary>The tooltip: the full virtual path plus the owning archive.</summary>
    public string Tooltip
    {
        get
        {
            if (IsFolder) return Node.Path.Length == 0 ? "(root)" : Node.Path + "/";
            var owner = Node.Owner?.FileName;
            var text = owner is null ? Node.Path : $"{Node.Path}\nin {owner}";
            if (Node.StagedData is not null)
                text += $"\nstaged: {ByteSize.Format(Node.UncompressedSize)}";
            return text;
        }
    }

    /// <summary>Set the expansion state; returns true when it actually changed.</summary>
    public bool SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return false;
        IsExpanded = expanded;
        Raise(nameof(IsExpanded));
        Raise(nameof(ChevronGlyph));
        return true;
    }

    /// <summary>Re-read every projected value, after an edit or a theme change.</summary>
    public void Refresh()
    {
        Raise(nameof(DisplayName));
        Raise(nameof(SizeText));
        Raise(nameof(Format));
        Raise(nameof(Compression));
        Raise(nameof(ModifiedMark));
        Raise(nameof(NameBrush));
        Raise(nameof(NameWeight));
        Raise(nameof(SizeBrush));
        Raise(nameof(IconGlyph));
        Raise(nameof(Tooltip));
        Raise(nameof(ChevronVisibility));
    }

    /// <summary>
    /// A glyph for the file's extension. This is decoration only and drives nothing: an unknown
    /// extension gets the generic document glyph rather than hiding or altering the row.
    /// </summary>
    private static string GlyphForExtension(string format) => format switch
    {
        "DAT" => "\uE7C3",
        "WTB" or "WTA" or "WTP" => "\uE91B",
        "DTT" => "\uE8A5",
        "TXT" or "XML" or "JSON" => "\uE8A5",
        "BNTX" or "PNG" or "DDS" => "\uEB9F",
        _ => "\uE8A5",
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
