using System.Windows;
using System.Windows.Controls;
using PkzTool.Core.Archives;
using PkzTool.Core.Compression;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>
/// Picks a compression type and level. Used by Replace (each time), Recompress (once for the whole
/// selection), Add and PKZ from folder, so the choice is presented identically everywhere.
/// </summary>
public partial class CompressionDialog : ThemedDialog
{
    private readonly List<RadioButton> _buttons = new();

    /// <summary>The chosen compression. Only meaningful after the dialog returns true.</summary>
    public EntryCompression Compression { get; private set; }

    /// <summary>The chosen codec level.</summary>
    public int Level { get; private set; }

    public CompressionDialog(string title, string hint, EntryCompression current, int level)
    {
        InitializeComponent();

        Title = title;
        Headline.Text = title;
        if (!string.IsNullOrEmpty(hint)) Hint.Text = hint;

        Compression = current;
        Level = level;

        // One radio per type. Built in code because the list comes from the codec, not from XAML, so a
        // type added later appears here without touching this file.
        foreach (var kind in CompressionCodec.All)
        {
            var radio = new RadioButton
            {
                Style = (Style)FindResource("DialogRadio"),
                GroupName = "CompressionKind",
                Margin = new Thickness(0, 0, 0, 8),
                IsChecked = kind == current,
                Content = BuildLabel(kind),
                Tag = kind,
            };
            radio.Checked += OnTypeChecked;
            _buttons.Add(radio);
            TypeList.Children.Add(radio);
        }

        ApplyLevelRange();
    }

    /// <summary>A label with the codec's honest description, so the choice is informed.</summary>
    private static StackPanel BuildLabel(EntryCompression kind)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = CompressionCodec.DisplayName(kind) });
        string detail = kind switch
        {
            EntryCompression.None => "Store every file as it is. Fastest, largest archives.",
            EntryCompression.ZStandard => "Built in, no extra dll needed. A good default.",
            EntryCompression.OodleKraken => "What the game's own archives use. Needs the Oodle dll in Settings.",
            _ => string.Empty,
        };
        if (detail.Length > 0)
            panel.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Theming.ThemeManager.Brush("Brush.Muted"),
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    private void OnTypeChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: EntryCompression kind }) return;
        Compression = kind;
        ApplyLevelRange();
    }

    /// <summary>
    /// Re-range the level slider for the selected codec and snap the current value into it, so a level
    /// meant for one codec cannot silently be sent to another.
    /// </summary>
    private void ApplyLevelRange()
    {
        bool meaningful = Compression != EntryCompression.None;
        LevelSlider.IsEnabled = meaningful;
        LevelSlider.Minimum = CompressionCodec.MinLevel(Compression);
        LevelSlider.Maximum = CompressionCodec.MaxLevel(Compression);
        LevelSlider.Value = CompressionCodec.ClampLevel(Compression, Level);
        Level = (int)LevelSlider.Value;
        LevelText.Text = Level.ToString();

        RuleText.Text = Compression == EntryCompression.None
            ? "Nothing is compressed: the type above already says so, and no level applies."
            : $"Files that stay at {CompressionCodec.CompressionWorthwhileThreshold:P0} or more of their " +
              "original size are stored uncompressed, whichever type is chosen here. That keeps a " +
              "requested compression from making a file bigger.";
    }

    private void OnLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Level = (int)e.NewValue;
        if (LevelText is not null) LevelText.Text = Level.ToString();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        Level = (int)LevelSlider.Value;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// Ask for a compression type, or return null when the user cancelled. The convenience overload
    /// keeps every call site to one line.
    /// </summary>
    public static (EntryCompression Compression, int Level)? Ask(Window owner, string title,
        EntryCompression current, int level)
    {
        var dialog = new CompressionDialog(title, string.Empty, current, level) { Owner = owner };
        return dialog.ShowDialog() == true ? (dialog.Compression, dialog.Level) : null;
    }
}
