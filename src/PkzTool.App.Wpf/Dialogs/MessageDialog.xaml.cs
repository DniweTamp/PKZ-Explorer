using System.Windows;
using PkzTool.App.Wpf.Theming;

namespace PkzTool.App.Wpf.Dialogs;

/// <summary>What a message dialog is asking or telling.</summary>
internal enum MessageKind
{
    /// <summary>Something the user should read before continuing.</summary>
    Information,

    /// <summary>Something may be lost or did not work.</summary>
    Warning,

    /// <summary>The operation failed.</summary>
    Error,

    /// <summary>A yes or no question.</summary>
    Question,
}

/// <summary>Which buttons a message dialog offers.</summary>
internal enum MessageButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

/// <summary>
/// A themed replacement for <see cref="MessageBox"/>.
/// <para>
/// Every prompt in this app went through the system dialog, which ignores the palette and looks nothing
/// like the rest of the tool. This draws the same shapes as the other dialogs instead.
/// </para>
/// <para>
/// It also makes prompts testable. A system dialog blocks its thread until someone clicks it, which is
/// why a headless run used to hang on any error path; this returns a default answer immediately when the
/// window is running headlessly, and records what it was asked so a check can assert on it.
/// </para>
/// </summary>
public partial class MessageDialog : ThemedDialog
{
    private MessageButtons _buttons = MessageButtons.Ok;

    internal MessageDialog()
    {
        // A message box is as tall as its message: a one-line question must not sit in the space a
        // ten-line error needed, and a long error must not clip its own buttons.
        ShrinksToContent = true;

        InitializeComponent();
        Title = "Notice";
    }

    /// <summary>
    /// Set by a headless run so no prompt is ever shown. The answer is the default for the button set,
    /// which is what a user pressing Enter would give.
    /// </summary>
    internal static bool SuppressPrompts { get; set; }

    /// <summary>Every prompt raised this run, so a check can assert on what the user would have seen.</summary>
    internal static List<string> History { get; } = new();

    /// <summary>Forget the recorded prompts, so a check starts from a known state.</summary>
    internal static void ClearHistory() => History.Clear();

    /// <summary>
    /// Show a message and return the button the user chose. In a headless run nothing is shown and the
    /// default is returned.
    /// </summary>
    internal static MessageBoxResult Show(Window? owner, string heading, string body, string detail,
        MessageKind kind, MessageButtons buttons)
    {
        string summary = string.IsNullOrEmpty(detail)
            ? $"{kind}: {heading} -- {body}"
            : $"{kind}: {heading} -- {body} -- {detail}";
        History.Add(summary);

        if (SuppressPrompts)
        {
            // The safe answer, not Enter's: a headless run must never take a prompt as permission to do
            // something destructive. Cancel and No both mean "do not go ahead", which is why a prompt
            // reaching this path aborts its operation rather than performing it.
            return buttons switch
            {
                MessageButtons.OkCancel => MessageBoxResult.Cancel,
                MessageButtons.YesNoCancel => MessageBoxResult.Cancel,
                MessageButtons.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.OK,
            };
        }

        var dialog = new MessageDialog { Owner = owner };

        dialog.Heading.Text = heading;
        dialog.Intro.Text = body;
        dialog.Detail.Text = detail;
        dialog.Apply(kind, buttons);
        dialog.ShowDialog();
        return dialog.Answer;
    }

    /// <summary>
    /// A message with no separate detail: the heading is the first line and the rest of the text becomes
    /// the body, which is how the callers that only have one string are written.
    /// </summary>
    internal static MessageBoxResult Show(Window? owner, string text, string heading,
        MessageKind kind, MessageButtons buttons) =>
        Show(owner, heading, text, string.Empty, kind, buttons);

    /// <summary>
    /// Fill the dialog in without showing it, so a check can capture or lay it out. Mirrors what
    /// <see cref="Show(Window?, string, string, string, MessageKind, MessageButtons)"/> does before
    /// calling <c>ShowDialog</c>.
    /// </summary>
    internal void UiTestConfigure(string heading, string body, string detail,
        MessageKind kind, MessageButtons buttons)
    {
        Heading.Text = heading;
        Intro.Text = body;
        Detail.Text = detail;

        // Only show the detail card when there is something in it. An empty one is a large blank box
        // under a one-line question.
        DetailCard.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;

        Apply(kind, buttons);
    }

    /// <summary>
    /// Press a named button without showing the dialog, and report the answer it produced. A check cannot
    /// click a real window, and the answer is the whole point of the dialog.
    /// </summary>
    internal MessageBoxResult UiTestPress(string buttonName)
    {
        var button = (System.Windows.Controls.Button)FindName(buttonName)!;
        button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        return Answer;
    }
    /// <summary>The answer the user gave, or the default when the window was closed.</summary>
    private MessageBoxResult Answer { get; set; } = MessageBoxResult.Cancel;

    /// <summary>Set the glyph, colour and button labels for the requested kind and button set.</summary>
    private void Apply(MessageKind kind, MessageButtons buttons)
    {
        // Set here rather than only by the Show factory, so the handlers and the labels can never
        // disagree about which question is being asked — a mismatch made the Yes button answer OK.
        _buttons = buttons;

        (Glyph.Text, string title) = kind switch
        {
            MessageKind.Information => ("\uE946", "Information"),
            MessageKind.Warning => ("\uE7BA", "Warning"),
            MessageKind.Error => ("\uEA39", "Error"),
            _ => ("\uE897", "Question"),
        };

        Title = title;

        // The heading takes the accent for a question and the muted text colour otherwise, so an error
        // is not shouting in the same colour as a confirmation.
        Heading.Foreground = kind == MessageKind.Question
            ? (System.Windows.Media.Brush)FindResource("Brush.Accent")
            : (System.Windows.Media.Brush)FindResource("Brush.Text");

        // A warning or error keeps its glyph tinted; an information prompt stays neutral.
        Glyph.Foreground = kind switch
        {
            MessageKind.Warning => (System.Windows.Media.Brush)FindResource("Brush.Accent"),
            MessageKind.Error => (System.Windows.Media.Brush)FindResource("Brush.Accent"),
            _ => (System.Windows.Media.Brush)FindResource("Brush.Muted"),
        };

        // Three slots, filled per button set. The previous version had only two controls and a catch-all
        // that mapped YesNoCancel onto "Yes"/"Cancel", so a three-way question silently lost its No
        // answer: the user could save or back out, but not discard.
        (AcceptButton.Content, DismissButton.Content, DismissButton.Visibility,
         CancelButton.Content, CancelButton.Visibility) = buttons switch
        {
            MessageButtons.Ok => ("OK", string.Empty, Visibility.Collapsed, string.Empty, Visibility.Collapsed),
            MessageButtons.OkCancel => ("OK", string.Empty, Visibility.Collapsed, "Cancel", Visibility.Visible),
            MessageButtons.YesNo => ("Yes", "No", Visibility.Visible, string.Empty, Visibility.Collapsed),
            _ => ("Yes", "No", Visibility.Visible, "Cancel", Visibility.Visible),
        };

        // Escape is the cancel button only when there is one; with Yes/No alone, dismissing the window is
        // a No rather than a silent abandonment.
        CancelButton.IsCancel = CancelButton.Visibility == Visibility.Visible;
        if (!CancelButton.IsCancel) CancelButton.Click -= OnCancel;
    }

    /// <summary>
    /// Close with an answer. Setting DialogResult directly throws unless the window was shown with
    /// ShowDialog, which makes the buttons untestable without a real window; this sets it only when the
    /// dialog is actually modal.
    /// </summary>
    private void Close(bool result)
    {
        if (IsLoaded) DialogResult = result;
        else Close();
    }
    /// <summary>The affirmative button: OK, or Yes.</summary>
    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Answer = _buttons is MessageButtons.YesNo or MessageButtons.YesNoCancel
            ? MessageBoxResult.Yes
            : MessageBoxResult.OK;
        Close(true);
    }

    /// <summary>The second answer, which only a yes/no question has.</summary>
    private void OnNo(object sender, RoutedEventArgs e)
    {
        Answer = MessageBoxResult.No;
        Close(false);
    }

    /// <summary>
    /// The cancel button. Dismissing the window without choosing (the title bar cross, or Escape) also
    /// lands here, so a question with no cancel button treats that as the negative answer rather than as
    /// an abandonment the caller cannot distinguish.
    /// </summary>
    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Answer = _buttons switch
        {
            MessageButtons.YesNo => MessageBoxResult.No,
            MessageButtons.YesNoCancel => MessageBoxResult.Cancel,
            _ => MessageBoxResult.Cancel,
        };
        Close(false);
    }
}
