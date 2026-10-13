using System.Windows;
using PkzTool.Core.Workspace;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Asks which loaded pkz a set of files should be added to. The list shows each archive's file name and
/// its path, plus what the target folder inside it will be, so the choice is unambiguous when several
/// loaded archives hold similar content.
/// </summary>
public partial class PkzPickDialog : ThemedDialog
{
    private readonly List<LoadedArchive> _archives;

    /// <summary>The chosen archive. Only meaningful after the dialog returns true.</summary>
    public LoadedArchive? Selected { get; private set; }

    public PkzPickDialog(IReadOnlyList<LoadedArchive> archives, string targetFolder)
    {
        InitializeComponent();

        _archives = archives.ToList();

        string where = targetFolder.Length == 0 ? "the root" : $"'{targetFolder}'";
        Hint.Text = $"Every loaded pkz is listed. The files are added to {where} of the one you pick.";

        // The path is part of the label because two archives can share a file name when loaded from
        // different folders.
        foreach (var archive in _archives)
            ArchiveList.Items.Add($"{archive.FileName}    \u2014    {archive.Path}");

        ArchiveList.SelectedIndex = 0;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        int index = ArchiveList.SelectedIndex;
        if (index < 0 || index >= _archives.Count) return;

        Selected = _archives[index];
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Ask which pkz to add into, or return null when the user cancelled.</summary>
    public static LoadedArchive? Ask(Window owner, IReadOnlyList<LoadedArchive> archives, string targetFolder)
    {
        if (archives.Count == 0) return null;
        // With one archive loaded there is nothing to choose, so the dialog would be pure friction.
        if (archives.Count == 1) return archives[0];

        var dialog = new PkzPickDialog(archives, targetFolder) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Selected : null;
    }
}
