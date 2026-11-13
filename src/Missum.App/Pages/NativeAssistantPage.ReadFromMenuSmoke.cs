using System.Text.Json;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifyReadFromMenuSmokeAsync(JsonElement original)
    {
        if (!IsMessageFooterSmoke
            || !Uri.TryCreate(Environment.GetEnvironmentVariable("MISSUM_SMOKE_SPEECH_GATEWAY_URL"), UriKind.Absolute, out var gateway)
            || !gateway.IsLoopback || gateway.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("Der Vorlese-Smoke benötigt ein isoliertes Profil und einen lokalen Test-Gateway.");

        const string introduction = "Diese Einleitung darf nicht vorgelesen werden.";
        const string first = "Hier beginnt der gewählte Satz.";
        const string second = "Der nächste Satz folgt.";
        const string selected = "Hier beginnt **der gewählte Satz**. " + second;
        var app = App.Current;
        var chats = app.GetService<IChatRepository>();
        var speech = app.GetService<MissumAiAssistantService>();
        var previousAccent = app.AccentColor;
        var previousFixture = _selectionInputSmokeActive;
        var previousGateway = _settings.Current.MissumAiServerUrl;
        var session = await chats.CreateSessionAsync("Vorlesen ab Rechtsklick", ChatMode.General, _lifetime.Token);
        _selectionInputSmokeActive = true;
        try
        {
            app.ApplyAccentColor("#E9B557");
            await _settings.UpdateAsync(current => current with { MissumAiServerUrl = gateway.AbsoluteUri.TrimEnd('/') });
            using var input = new NativeSelectionInputSmoke();
            await input.EnsureForegroundAsync();
            foreach (var virtualized in new[] { false, true })
            {
                var prefix = virtualized
                    ? string.Concat(Enumerable.Repeat("Ein älterer Absatz im virtuellen Nachrichtenverlauf.\n\n", 175))
                    : "";
                var markdown = prefix + introduction + (virtualized ? "\n" : "\n\n") + selected;
                var status = virtualized ? MessageStatus.Streaming : MessageStatus.Completed;
                var message = await chats.AddMessageAsync(session.Id, ChatRole.Assistant, markdown, status,
                    cancellationToken: _lifetime.Token);
                var id = message.Id.ToString();
                var state = original.Deserialize<Dictionary<string, JsonElement>>(JsonOptions)!;
                state["activeSessionId"] = JsonSerializer.SerializeToElement(session.Id);
                state["chatMode"] = JsonSerializer.SerializeToElement("general");
                state["isRunning"] = JsonSerializer.SerializeToElement(virtualized);
                if (virtualized)
                {
                    state["activeRunSessionId"] = JsonSerializer.SerializeToElement(session.Id);
                    state["activeRunId"] = JsonSerializer.SerializeToElement("run-native-speech-read-from");
                    state["runMessageId"] = JsonSerializer.SerializeToElement(message.Id);
                }
                state["messages"] = JsonSerializer.SerializeToElement(new[]
                {
                    new { id = message.Id, sessionId = session.Id, role = "assistant", content = markdown,
                        status = virtualized ? "streaming" : "completed",
                        createdAt = message.CreatedAt, updatedAt = message.UpdatedAt },
                }, JsonOptions);
                ApplyEvent("state.snapshot", JsonSerializer.SerializeToElement(state));
                RenderMessagesNow();
                if (virtualized)
                {
                    await WaitForPagingSmokeAsync(() => _virtualMessages.TryGetValue(id, out var current) && !current.Preparing,
                        "Die virtuelle Nachricht wurde für den Rechtsklick nicht vorbereitet.");
                    OnScrollToBottom(this, new RoutedEventArgs());
                    RefreshVirtualConversationViewport();
                }

                var body = MessageBody(_messageViews[id].View);
                TextBlock? clicked = null;
                var clickPoint = new Point();
                var stableLayout = 0;
                var windowContent = app.MainWindow?.Content as FrameworkElement
                    ?? throw new InvalidOperationException("Der Rechtsklick-Smoke besitzt kein sichtbares Hauptfenster.");
                await WaitForPagingSmokeAsync(() =>
                {
                    LayoutRoot.UpdateLayout();
                    clicked = Descendants(body).OfType<TextBlock>().SingleOrDefault(block => ReadFromSmokeText(block) == first + " " + second);
                    if (clicked is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 })
                    {
                        stableLayout = 0;
                        ConversationScroll.ChangeView(null, ConversationScroll.ScrollableHeight, null, true);
                        RefreshVirtualConversationViewport();
                        return false;
                    }

                    var run = clicked.Inlines.OfType<Run>().First();
                    var rect = run.ContentStart.GetPositionAtOffset(2, LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
                    clickPoint = new Point(rect.Left + .2, rect.Top + rect.Height / 2);
                    var viewportPoint = clicked.TransformToVisual(ConversationScroll).TransformPoint(clickPoint);
                    var windowPoint = clicked.TransformToVisual(windowContent).TransformPoint(clickPoint);
                    if (rect.Height > 0 && viewportPoint.Y > 4 && viewportPoint.Y < ConversationScroll.ViewportHeight - 4
                        && windowPoint.X > 4 && windowPoint.X < windowContent.ActualWidth - 4
                        && windowPoint.Y > 4 && windowPoint.Y < windowContent.ActualHeight - 4)
                        return ++stableLayout >= 2;

                    stableLayout = 0;
                    _scrollNewSessionToLatest = false;
                    var contentPoint = clicked.TransformToVisual(ConversationContent).TransformPoint(clickPoint);
                    var offset = Math.Clamp(contentPoint.Y - ConversationScroll.ViewportHeight / 2, 0, ConversationScroll.ScrollableHeight);
                    ConversationScroll.ChangeView(null, offset, null, true);
                    RefreshVirtualConversationViewport();
                    return false;
                }, "Der gewählte Absatz für den Rechtsklick ist nicht sichtbar.");
                var target = clicked!;
                if (virtualized != _virtualMessages.ContainsKey(id))
                    throw new InvalidOperationException("Der Rechtsklick verwendet nicht die erwartete Nachrichtendarstellung.");
                var root = target.XamlRoot ?? throw new InvalidOperationException("Dem Vorleseziel fehlt sein XAML-Fenster.");
                await input.MoveToAsync(target, clickPoint);
                await Task.Delay(40, _lifetime.Token);
                await input.RightClickAsync();

                MenuFlyoutItem? action = null;
                await WaitForPagingSmokeAsync(() =>
                {
                    action = VisualTreeHelper.GetOpenPopupsForXamlRoot(root)
                        .Where(popup => popup.Child is not null)
                        .SelectMany(popup => Descendants(popup.Child))
                        .OfType<MenuFlyoutItem>()
                        .FirstOrDefault(item => item.Text == "Ab hier vorlesen");
                    return action is not null;
                }, "Der echte Rechtsklick hat das Menü „Ab hier vorlesen“ nicht geöffnet.");
                var invoke = (IInvokeProvider)new MenuFlyoutItemAutomationPeer(action!).GetPattern(PatternInterface.Invoke);
                invoke.Invoke();

                bool MarksSentence(string expected, string? previous)
                {
                    if (ErrorBar.IsOpen) throw new InvalidOperationException(ErrorBar.Message);
                    var ranges = Descendants(body).OfType<TextBlock>()
                        .SelectMany(block => block.TextHighlighters.SelectMany(highlighter => highlighter.Ranges
                            .Select(range => (Block: block, Highlighter: highlighter, Range: range)))).ToArray();
                    if (ranges.Length == 0) return false;
                    if (ranges.Length != 1 || !ReferenceEquals(ranges[0].Block, target))
                        throw new InvalidOperationException("„Ab hier vorlesen“ hat Text vor dem gewählten Absatz markiert.");
                    var (_, highlighter, range) = ranges[0];
                    var sentence = ReadFromSmokeText(target).Substring(range.StartIndex, range.Length);
                    if (sentence == previous) return false;
                    if (sentence != expected || _messageSpeechSessionId != session.Id || _messageSpeechMessageId != message.Id
                        || highlighter.Background is not SolidColorBrush background || background.Color != ThemeBrush("MissumAccentBrush", 0).Color
                        || !_microphone.Current.IsSpeaking)
                        throw new InvalidOperationException("Unerwarteter markierter Satz nach dem Rechtsklick: " + sentence);
                    return true;
                }

                await WaitForPagingSmokeAsync(() => MarksSentence(first, null), "Der erste vorgelesene Satz wurde nach dem Rechtsklick nicht markiert.");
                if (!virtualized) await SaveMathPreviewAsync(body, "native-speech-read-from-preview.png");
                await WaitForPagingSmokeAsync(() => MarksSentence(second, first), "Die Markierung ist nicht zum nächsten Satz gewandert.");
                if (virtualized) await StopMessageSpeechAsync();
                await WaitForPagingSmokeAsync(() => !_speaking && !_microphone.Current.IsSpeaking && target.TextHighlighters.Count == 0,
                    "Die Satzmarkierung ist nach dem Vorlesen sichtbar geblieben.");
                if (virtualized && (!_running || S(DisplayMessages[id], "status") != "streaming"))
                    throw new InvalidOperationException("Der Rechtsklick oder Vorlesestopp hat die laufende Antwort verändert.");
            }
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-speech-read-from-validation.json"),
                JsonSerializer.Serialize(new
                {
                    passed = true, processId = Environment.ProcessId, nativeRightClick = true,
                    normalMessage = true, virtualizedMessage = true, streamingMessage = true, firstAndSecondSentence = true,
                    completionClearsHighlight = true, stopClearsHighlight = true, audioDeviceStarted = true,
                }, JsonOptions), _lifetime.Token);
        }
        finally
        {
            if (speech.IsSpeaking || _microphone.Current.IsSpeaking) await StopMessageSpeechAsync();
            await _settings.UpdateAsync(current => current with { MissumAiServerUrl = previousGateway });
            app.ApplyAccentColor(previousAccent);
            ApplyEvent("state.snapshot", original);
            RenderMessagesNow();
            await chats.DeleteSessionAsync(session.Id, CancellationToken.None);
            _selectionInputSmokeActive = previousFixture;
        }
    }

    private static string ReadFromSmokeText(TextBlock block) => block.Inlines.Count == 0
        ? block.Text : string.Concat(block.Inlines.Select(InlineText));

    private static string InlineText(Inline inline) => inline switch
    {
        Run run => run.Text,
        Span span => string.Concat(span.Inlines.Select(InlineText)),
        LineBreak => "\n",
        _ => "",
    };
}
