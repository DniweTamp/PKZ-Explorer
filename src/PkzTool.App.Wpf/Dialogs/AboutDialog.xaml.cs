using System.Windows;
using PkzTool.Core.Settings;
using Xt1.Core.Util;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// The About box: what the tool is for, the format and the compression rule it implements, and the state
/// of the current session so a support question can be answered from the window itself.
/// </summary>
public partial class AboutDialog : ThemedDialog
{
    public AboutDialog(int archiveCount, int fileCount, PkzSettings settings)
    {
        // The body scrolls between a fixed header and a fixed button row, so the window keeps the
        // height it declares instead of growing to fit all of the prose at once.
        SizesItself = true;

        InitializeComponent();

        Subtitle.Text = "Bayonetta 3 pkz archives \u2014 browse, extract and edit";

        SessionText.Text = archiveCount == 0
            ? "No archive is loaded."
            : $"{archiveCount} archive(s) loaded, {fileCount} entries in the merged view.";

        string oodle = settings.ResolveOodleDll() is { } path
            ? path
            : "not found (Oodle compression will ask for one)";
        SettingsText.Text = $"Oodle dll: {oodle}";
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;
}
