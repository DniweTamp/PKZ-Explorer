using System.Windows;
using System.Windows.Controls;
using PkzTool.Core.Compression;
using PkzTool.Core.Settings;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Prompts for the Oodle dll when one is needed and none is configured or loadable. Every candidate
/// found on the machine is offered, so the usual case is one click.
/// </summary>
public partial class OodleDialog : ThemedDialog
{
    /// <summary>The chosen dll path. Only meaningful after the dialog returns true.</summary>
    public string Result { get; private set; }

    public OodleDialog(string? current)
    {
        InitializeComponent();

        Result = current ?? string.Empty;
        PathBox.Text = Result;

        var candidates = OodleLocator.All();
        CandidateList.ItemsSource = candidates;

        Validate();
    }

    private void OnPathChanged(object sender, TextChangedEventArgs e) => Validate();

    private void OnPickCandidate(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) PathBox.Text = path;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the Oodle dll",
            Filter = "Oodle dll (oo2core*.dll)|oo2core*.dll|DLL (*.dll)|*.dll|All files (*.*)|*.*",
            FileName = PathBox.Text,
        };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FileName;
    }

    /// <summary>
    /// Load the typed path immediately, so the verdict is honest: a dll that cannot supply compression
    /// is rejected here rather than failing at the end of a long pack.
    /// </summary>
    private void Validate()
    {
        if (OkButton is null || StatusText is null) return;

        string path = PathBox.Text.Trim();
        if (path.Length == 0)
        {
            OkButton.IsEnabled = false;
            StatusText.Text = "Enter a path, pick a detected dll, or browse.";
            StatusText.SetResourceReference(ForegroundProperty, "Brush.Muted");
            return;
        }

        if (!File.Exists(path))
        {
            OkButton.IsEnabled = false;
            StatusText.Text = "That file does not exist.";
            StatusText.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        if (!Oodle.TryValidate(path, out var error))
        {
            OkButton.IsEnabled = false;
            StatusText.Text = error ?? "That dll cannot be used.";
            StatusText.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        // A decompress-only dll is not enough to write an archive, so it is called out specifically.
        if (!Oodle.CanCompress)
        {
            OkButton.IsEnabled = false;
            StatusText.Text = "This dll can decompress but does not export OodleLZ_Compress, so it cannot " +
                              "be used for compression.";
            StatusText.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        OkButton.IsEnabled = true;
        StatusText.Text = "This dll loads and can compress.";
        StatusText.SetResourceReference(ForegroundProperty, "Brush.SuccessText");
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Result = PathBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
