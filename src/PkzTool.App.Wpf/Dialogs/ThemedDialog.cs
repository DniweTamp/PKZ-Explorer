using System.Windows;
using PkzTool.App.Wpf.Theming;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Shared shell for every dialog: centred on the owner, hidden from the taskbar, using the themed window
/// background, and repainting the native title bar whenever the palette changes. Per-dialog sizing and
/// behaviour stays in the derived dialog.
/// </summary>
public class ThemedDialog : Window
{
    public ThemedDialog()
    {
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.Manual;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SetResourceReference(BackgroundProperty, "Brush.Window");

        Loaded += (_, _) => ThemeManager.ApplyTitleBar(this);
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged() => ThemeManager.ApplyTitleBar(this);
}
