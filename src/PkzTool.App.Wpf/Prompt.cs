using System.Windows;
using PkzTool.App.Wpf.Dialogs;

namespace PkzTool.App.Wpf;

/// <summary>
/// The app's prompt, replacing the system <see cref="MessageBox"/>.
/// <para>
/// It keeps the same call shape as <see cref="MessageBox.Show(Window, string, string, MessageBoxButton, MessageBoxImage)"/>
/// so every call site reads the same as before, but draws the themed <see cref="MessageDialog"/> instead
/// of the generic Windows dialog. It also stops a prompt from blocking a headless run: nothing is shown,
/// the safe answer is returned, and what was asked is recorded for a check to assert on.
/// </para>
/// <para>
/// The heading and the body are separate here, unlike <c>MessageBox</c> where the caption is the window
/// title. A caption is what a title bar shows; these dialogs show it as a heading inside the window, which
/// is what the rest of the app's dialogs do.
/// </para>
/// </summary>
internal static class Prompt
{
    /// <summary>True while this process is running headlessly, so nothing may be shown.</summary>
    internal static bool Suppress { get => MessageDialog.SuppressPrompts; set => MessageDialog.SuppressPrompts = value; }

    /// <summary>Every prompt raised this run, in order, for a check to assert on.</summary>
    internal static IReadOnlyList<string> History => MessageDialog.History;

    /// <summary>Forget the recorded prompts.</summary>
    internal static void ClearHistory() => MessageDialog.ClearHistory();

    /// <summary>Show a message and return the chosen button.</summary>
    internal static MessageBoxResult Show(Window? owner, string text, string heading,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        MessageDialog.Show(owner, text, heading, ToKind(image), ToButtons(buttons));

    /// <summary>Show a message with a separate, lengthy detail block that scrolls.</summary>
    internal static MessageBoxResult ShowDetail(Window? owner, string heading, string text, string detail,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None) =>
        MessageDialog.Show(owner, heading, text, detail, ToKind(image), ToButtons(buttons));

    private static MessageKind ToKind(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error or MessageBoxImage.Hand or MessageBoxImage.Stop => MessageKind.Error,
        MessageBoxImage.Warning or MessageBoxImage.Exclamation => MessageKind.Warning,
        MessageBoxImage.Question => MessageKind.Question,
        _ => MessageKind.Information,
    };

    private static MessageButtons ToButtons(MessageBoxButton buttons) => buttons switch
    {
        MessageBoxButton.OKCancel => MessageButtons.OkCancel,
        MessageBoxButton.YesNo => MessageButtons.YesNo,
        MessageBoxButton.YesNoCancel => MessageButtons.YesNoCancel,
        _ => MessageButtons.Ok,
    };
}
