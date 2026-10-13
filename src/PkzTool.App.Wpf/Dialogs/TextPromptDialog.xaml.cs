using System.Windows;
using System.Windows.Controls;
using PkzTool.App.Wpf.Theming;
using PkzTool.Core.Vfs;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Asks for a single name. Used by Rename, for both a file (the file name only) and a folder (the path
/// segment shared by everything under it), so the hint text is what tells the two apart.
/// </summary>
public partial class TextPromptDialog : ThemedDialog
{
    private readonly string _original;

    /// <summary>The typed name. Only meaningful after the dialog returns true.</summary>
    public string Value => NameBox.Text.Trim();

    public TextPromptDialog(string title, string hint, string current)
    {
        InitializeComponent();

        _original = current;
        Title = title;
        Headline.Text = title;
        Hint.Text = hint;
        NameBox.Text = current;

        // Pre-select the stem so typing replaces the name but keeps the extension, matching how
        // Explorer's rename behaves.
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            string stem = System.IO.Path.GetFileNameWithoutExtension(current);
            NameBox.Select(0, stem.Length > 0 ? stem.Length : current.Length);
        };

        Validate();
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => Validate();

    /// <summary>
    /// Check the name as it is typed, so the problem is visible before OK is pressed rather than as an
    /// error afterwards.
    /// </summary>
    private void Validate()
    {
        if (ValidationText is null || OkButton is null) return;

        string name = Value;
        string? problem = name.Length == 0 ? "Enter a name." : VirtualPath.ValidateSegment(name);
        bool unchanged = VirtualPath.CompareEqual(name, _original);

        // Renaming to the current name is not an error worth a warning, but it is not a change either,
        // so OK stays disabled and the hint says why.
        if (problem is null && unchanged) problem = "This is the current name.";

        OkButton.IsEnabled = problem is null;

        ValidationText.Text = problem ?? "The item keeps its folder; only the last part of the path changes.";
        ValidationText.SetResourceReference(ForegroundProperty,
            problem is null ? "Brush.Muted" : "Brush.WarningText");
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string name = Value;
        if (name.Length == 0 || VirtualPath.ValidateSegment(name) is not null) return;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
