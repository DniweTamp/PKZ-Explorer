using System.Windows;
using System.Windows.Controls;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;
using PkzTool.Core.Settings;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// The Oodle dll path and the compression defaults the dialogs start on. Edits a copy of the live
/// settings; the caller reads <see cref="Result"/> after OK.
/// </summary>
public partial class SettingsDialog : ThemedDialog
{
    /// <summary>A copy of the settings passed in, updated in place when the user accepts.</summary>
    public PkzSettings Result { get; }

    /// <summary>True when the Oodle path was changed, so the shell can reload the dll.</summary>
    public bool OodleChanged { get; private set; }

    public SettingsDialog(PkzSettings current)
    {
        Result = CopyOf(current);
        InitializeComponent();

        OodleBox.Text = Result.OodleDllPath ?? string.Empty;
        SettingsPathText.Text = $"Written next to the executable: {PkzSettings.SettingsPath}";
        SettingsFallbackText.Text =
            $"If that location is not writable, the roaming copy is used instead: {PkzSettings.FallbackSettingsPath}";

        BuildDefaultTypeList();
        ApplyLevelRange();

        Loaded += (_, _) => UpdateOodleStatus();
    }

    /// <summary>
    /// One radio per compression type for the dialog default. Built in code because the list comes from
    /// the codec rather than from XAML.
    /// </summary>
    private void BuildDefaultTypeList()
    {
        foreach (var kind in CompressionCodec.All)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("DialogRadio"),
                GroupName = "DefaultCompression",
                Content = CompressionCodec.DisplayName(kind),
                Tag = kind,
                Margin = new Thickness(0, 0, 8, 0),
                IsChecked = kind == Result.DefaultCompression,
            };
            radio.Checked += OnDefaultTypeChecked;
            DefaultTypeList.Children.Add(radio);
        }
    }

    private void OnDefaultTypeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: EntryCompression kind }) return;
        Result.DefaultCompression = kind;
        ApplyLevelRange();
    }

    /// <summary>Re-range the level for the selected default so the recorded value is always valid.</summary>
    private void ApplyLevelRange()
    {
        bool meaningful = Result.DefaultCompression != EntryCompression.None;
        LevelSlider.IsEnabled = meaningful;
        LevelSlider.Minimum = CompressionCodec.MinLevel(Result.DefaultCompression);
        LevelSlider.Maximum = CompressionCodec.MaxLevel(Result.DefaultCompression);
        LevelSlider.Value = CompressionCodec.ClampLevel(Result.DefaultCompression, Result.DefaultLevel);
        Result.DefaultLevel = (int)LevelSlider.Value;
        LevelText.Text = Result.DefaultLevel.ToString();
    }

    private void OnLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Result.DefaultLevel = (int)e.NewValue;
        if (LevelText is not null) LevelText.Text = Result.DefaultLevel.ToString();
    }

    private void OnOodleChanged(object sender, TextChangedEventArgs e) => UpdateOodleStatus();

    /// <summary>
    /// Say whether the typed path actually loads. The check runs the real loader, so a decompress-only
    /// dll is reported as unusable for compression rather than silently accepted.
    /// </summary>
    private void UpdateOodleStatus()
    {
        if (OodleStatus is null) return;

        string typed = OodleBox.Text.Trim();
        string? resolved = typed.Length == 0 ? OodleLocator.Find(null) : OodleLocator.Find(typed);

        if (typed.Length > 0 && !File.Exists(typed))
        {
            OodleStatus.Text = resolved is not null
                ? $"Not found at that path. Auto-detection would use {resolved}."
                : "Not found at that path, and auto-detection found nothing.";
            OodleStatus.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        if (resolved is null)
        {
            OodleStatus.Text = "No oo2core*.dll found. Oodle compression will ask for one when it is needed.";
            OodleStatus.SetResourceReference(ForegroundProperty, "Brush.Muted");
            return;
        }

        if (!Oodle.TryValidate(resolved, out var error))
        {
            OodleStatus.Text = error ?? "That dll cannot be loaded.";
            OodleStatus.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        if (!Oodle.CanCompress)
        {
            OodleStatus.Text = $"{resolved} loads but cannot compress (no OodleLZ_Compress), so it only " +
                               "helps with reading Oodle entries.";
            OodleStatus.SetResourceReference(ForegroundProperty, "Brush.WarningText");
            return;
        }

        OodleStatus.Text = typed.Length == 0
            ? $"Auto-detected {resolved}. It loads and can compress."
            : $"{resolved} loads and can compress.";
        OodleStatus.SetResourceReference(ForegroundProperty, "Brush.SuccessText");
    }

    private void OnBrowseOodle(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the Oodle dll",
            Filter = "Oodle dll (oo2core*.dll)|oo2core*.dll|DLL (*.dll)|*.dll|All files (*.*)|*.*",
            FileName = OodleBox.Text,
        };
        if (dialog.ShowDialog(this) == true) OodleBox.Text = dialog.FileName;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        string path = OodleBox.Text.Trim();
        string? newPath = path.Length == 0 ? null : path;

        OodleChanged = !string.Equals(newPath ?? string.Empty, Result.OodleDllPath ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        Result.OodleDllPath = newPath;
        Result.DefaultLevel = (int)LevelSlider.Value;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>A copy, so a cancelled dialog cannot have changed the live settings.</summary>
    private static PkzSettings CopyOf(PkzSettings s) => new()
    {
        Theme = s.Theme,
        OodleDllPath = s.OodleDllPath,
        LastDirectory = s.LastDirectory,
        LastSourceFolder = s.LastSourceFolder,
        RecentFiles = s.RecentFiles is null ? null : new List<string>(s.RecentFiles),
        DefaultCompression = s.DefaultCompression,
        DefaultLevel = s.DefaultLevel,
        ColumnNameWidth = s.ColumnNameWidth,
        ColumnSizeWidth = s.ColumnSizeWidth,
        ColumnFormatWidth = s.ColumnFormatWidth,
        ColumnCompressionWidth = s.ColumnCompressionWidth,
        Maximized = s.Maximized,
    };
}
