using System.Text.Json;
using Missum.App.Controls;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    // Uses real WinUI template parts in the isolated profile. It does not write
    // to the user's clipboard; the button uses the exact GetCopyText path tested here.
    private async Task VerifyNoticeSelectionSmokeAsync()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MISSUM_SMOKE_INSTANCE_KEY")))
            throw new InvalidOperationException("Der Meldungs-Smoke ist ausschließlich im isolierten Portable-Smoke erlaubt.");
        var host = new StackPanel { Width = 700, Spacing = 8 };
        var notice = NativeNotice.Attach(new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
            Title = "Dokument-PDF", Message = "Die Veröffentlichung ist noch nicht verfügbar.",
        });
        var existingAction = new Button { Content = "Erneut versuchen" };
        var existingMenu = new MenuFlyout();
        var existingItem = new MenuFlyoutItem { Text = "Bestehende Aktion" };
        existingMenu.Items.Add(existingItem);
        var withAction = NativeNotice.Attach(new InfoBar
        {
            IsOpen = true, Message = "Eine vorhandene Aktion bleibt erhalten.",
            ActionButton = existingAction, ContextFlyout = existingMenu,
        });
        host.Children.Add(notice);
        host.Children.Add(withAction);
        MessagesPanel.Children.Add(host);
        try
        {
            UpdateLayout();
            await Task.Delay(50, _lifetime.Token);
            UpdateLayout();
            var initialParts = NativeNotice.NoticeTextBlocks(notice).ToArray();
            var messagePart = initialParts.Single(part => part.Name == "Message");
            var originalCopy = notice.ActionButton;
            NativeNotice.Attach(notice);
            if (!ReferenceEquals(originalCopy, notice.ActionButton)
                || notice.ContextFlyout is not MenuFlyout { Items.Count: 1 })
                throw new InvalidOperationException("Eine erneute Meldungs-Anbindung hat Kopieraktionen dupliziert.");
            var error = "Die PDF wurde nicht erzeugt: Abschnitt \"3.3 Reduzierte a-Ω-Gleichungen\".\r\n"
                + @"$$\frac{d}{dt}\begin{pmatrix}B_p\\B_\phi\end{pmatrix}"
                + @"=\begin{pmatrix}-\eta_p & \alpha\\\omega_\Omega & -\eta_\phi\end{pmatrix}"
                + @"\begin{pmatrix}B_p\\B_\phi\end{pmatrix}\tag{3.9}$$"
                + "\r\nDer bisherige PDF-Stand bleibt erhalten. " + new string('x', 2048);
            notice.Title = "Publikation konnte nicht erstellt werden";
            notice.Message = error;
            withAction.Message += "\r\nAuch nach einer Aktualisierung.";
            UpdateLayout();
            await Task.Delay(50, _lifetime.Token);
            UpdateLayout();
            var parts = NativeNotice.NoticeTextBlocks(notice).ToArray();
            if (!ReferenceEquals(messagePart, parts.Single(part => part.Name == "Message"))
                || parts.Any(part => !part.IsTextSelectionEnabled)
                || parts.Single(part => part.Name == "Message").Text != error
                || notice.Message != error || notice.Content is not null)
                throw new InvalidOperationException("Die Fehlermeldung ist nicht verlustfrei im bestehenden Template markierbar.");
            messagePart.SelectAll();
            if (messagePart.SelectedText.Replace("\r\n", "\n", StringComparison.Ordinal)
                != error.Replace("\r\n", "\n", StringComparison.Ordinal))
                throw new InvalidOperationException("Die native Textauswahl enthält nicht die vollständige Fehlermeldung.");
            var expected = notice.Title + Environment.NewLine + Environment.NewLine + error;
            if (NativeNotice.GetCopyText(notice) != expected)
                throw new InvalidOperationException("Der Kopiertext hat Titel, LaTeX, Zeilenumbrüche oder den langen Meldungstext verändert.");
            if (!ReferenceEquals(withAction.ActionButton, existingAction)
                || !ReferenceEquals(withAction.ContextFlyout, existingMenu)
                || existingMenu.Items.Count != 2 || !existingMenu.Items.Contains(existingItem))
                throw new InvalidOperationException("Die Meldungs-Anbindung hat eine vorhandene Aktion oder ein Kontextmenü ersetzt.");
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-notice-selection-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, textSelectionEnabled = true, fullTextSelected = true,
                    exactCopyText = true, latexAndLineBreaksPreserved = true,
                    runtimeUpdatesRetainTemplate = true, noDuplicateMessage = true,
                    existingActionPreserved = true, existingContextMenuPreserved = true,
                    attachmentIdempotent = true, clipboardModified = false,
                    messageCharacters = error.Length, copyCharacters = expected.Length,
                }), _lifetime.Token);
        }
        finally { MessagesPanel.Children.Remove(host); }
    }
}
