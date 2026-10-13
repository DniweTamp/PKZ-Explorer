using System.Windows;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>What the user chose when extracted files already existed.</summary>
public enum OverwriteAnswer
{
    /// <summary>Write over the existing files.</summary>
    Overwrite,

    /// <summary>Leave the existing files alone and extract only what is missing.</summary>
    Skip,
}

/// <summary>
/// The overwrite / skip / cancel question, asked once per extract run rather than once per file. A
/// per-file prompt would make extracting over a large existing tree unusable, which is why the brief
/// asks for a single decision.
/// </summary>
public partial class OverwriteDialog : ThemedDialog
{
    /// <summary>The answer. Only meaningful after the dialog returns true.</summary>
    public OverwriteAnswer Answer { get; private set; }

    public OverwriteDialog(int existingCount)
    {
        InitializeComponent();

        Body.Text = existingCount == 1
            ? "1 file in the destination folder already exists."
            : $"{existingCount} files in the destination folder already exist.";
    }

    private void OnOverwrite(object sender, RoutedEventArgs e)
    {
        Answer = OverwriteAnswer.Overwrite;
        DialogResult = true;
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        Answer = OverwriteAnswer.Skip;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Ask about existing files. Returns the answer, or null when the user cancelled the operation.
    /// </summary>
    public static OverwriteAnswer? Ask(Window owner, int existingCount)
    {
        var dialog = new OverwriteDialog(existingCount) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Answer : null;
    }
}
