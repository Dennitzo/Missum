using System.Text.Json;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Missum.App.Pages;

public sealed partial class NativeAssistantPage
{
    private async Task VerifySourceAutoloadSmokeAsync()
    {
        var isolatedRoot = Environment.GetEnvironmentVariable("ASSISTANT_DATA_ROOT");
        if (!IsMessageFooterSmoke || string.IsNullOrWhiteSpace(isolatedRoot)
            || Environment.GetEnvironmentVariable("ASSISTANT_DISABLE_RUNTIME_AUTOSTART") != "1"
            || !string.Equals(Path.GetFullPath(isolatedRoot), Path.GetFullPath(App.Current.DataDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Der Quellen-Smoke benötigt eine isolierte Datenbank ohne Modellstart.");
        var originalSession = _session;
        var chats = App.Current.GetService<IChatRepository>();
        var fixture = await Task.Run(async () =>
        {
            var owner = await chats.CreateSessionAsync("Quellen automatisch laden", _lifetime.Token);
            var answer = await chats.AddMessageAsync(owner.Id, ChatRole.Assistant, "Älterer Beleg", MessageStatus.Completed,
                cancellationToken: _lifetime.Token);
            await chats.SaveToolStepAsync(answer.Id, new("old-source", "web.search", "completed",
                OutputJson: "{\"results\":[{\"title\":\"Historische Originalquelle\",\"url\":\"https://example.org/original\"}]}",
                UpdatedAt: DateTimeOffset.UtcNow), _lifetime.Token);
            for (var index = 0; index < 30; index++)
                await chats.AddMessageAsync(owner.Id, ChatRole.User, "Späterer Beitrag " + index, MessageStatus.Completed,
                    cancellationToken: _lifetime.Token);
            var empty = await chats.CreateSessionAsync("Andere Sitzung ohne Quellen", _lifetime.Token);
            return (owner.Id, MessageId: answer.Id, Empty: empty.Id);
        }, _lifetime.Token);
        bool HasSource(string title) => _sessionSources.GetValueOrDefault(_session, [])
            .Any(source => source.PageTitles?.Values.Contains(title, StringComparer.Ordinal) == true);
        try
        {
            if (!await CommandAsync("session.open", new { sessionId = fixture.Id }))
                throw new InvalidOperationException("Die Quellensitzung konnte nicht geöffnet werden.");
            await WaitForPagingSmokeAsync(() => !_sourceIndexLoading.Contains(fixture.Id) && HasSource("Historische Originalquelle"),
                "Die historische Quelle wurde ohne Öffnen des Quellen-Tabs nicht automatisch geladen.");
            VerifySourcesSummarySmoke(1);
            if (_sourceTabs.Contains(fixture.Id) || _activeSourcesSession == fixture.Id || _messages.ContainsKey(fixture.MessageId.ToString()))
                throw new InvalidOperationException("Die automatische Quelle benötigt unzulässig einen Tab oder die alte Nachricht.");
            var loadedMessages = _messages.Count;
            ApplyEvent("state.snapshot", _snapshot.Clone());
            if (_sourceIndexLoading.Contains(fixture.Id))
                throw new InvalidOperationException("Ein unveränderter Snapshot lädt die Quellen erneut.");

            // A reconnect of the same session must refresh a newer durable receipt.
            await Task.Run(() => chats.SaveToolStepAsync(fixture.MessageId, new("new-source", "web.fetch", "completed",
                OutputJson: "{\"title\":\"Neu geladene Quelle\",\"url\":\"https://example.org/new\"}",
                UpdatedAt: DateTimeOffset.UtcNow.AddSeconds(1)), _lifetime.Token), _lifetime.Token);
            if (!await CommandAsync("app.ready", new { })) throw new InvalidOperationException("Wiederverbindung fehlgeschlagen.");
            await WaitForPagingSmokeAsync(() => !_sourceIndexLoading.Contains(fixture.Id) && HasSource("Neu geladene Quelle"),
                "Die Wiederverbindung hat den Quellenindex nicht aktualisiert.");
            VerifySourcesSummarySmoke(2);
            if (_messages.Count != loadedMessages || _messages.ContainsKey(fixture.MessageId.ToString()))
                throw new InvalidOperationException("Die Quellenaktualisierung hat historische Nachrichten nachgeladen.");

            await CommandAsync("session.open", new { sessionId = fixture.Empty });
            await WaitForPagingSmokeAsync(() => !_sourceIndexLoading.Contains(fixture.Empty), "Die zweite Sitzung lädt weiterhin.");
            VerifySourcesSummarySmoke(0);
            ApplyEvent("sources.snapshot", JsonSerializer.SerializeToElement(new { sessionId = fixture.Id,
                sources = new[] { new { id = "late-source", tool = "web.fetch", title = "Verspätete Quelle",
                    url = "https://example.org/late", time = DateTimeOffset.UtcNow } } }));
            if (HasSource("Verspätete Quelle") || HasSource("Neu geladene Quelle"))
                throw new InvalidOperationException("Eine verspätete Antwort hat Quellen in der falschen Sitzung angezeigt.");
            VerifySourcesSummarySmoke(0);
            await File.WriteAllTextAsync(Path.Combine(App.Current.DataDirectory, "native-source-autoload-validation.json"),
                JsonSerializer.Serialize(new { passed = true, historicalSourceWithoutTab = true, reconnectUpdated = true,
                    unchangedSnapshotSkipped = true, historyNotHydrated = true, lateReplyIsolated = true,
                    compactSummaryOnly = true, initialCount = 1, reconnectedCount = 2, otherSessionCount = 0, modelRequests = 0 }), _lifetime.Token);
        }
        finally
        {
            if (!_disposed && originalSession != Guid.Empty) await CommandAsync("session.open", new { sessionId = originalSession });
        }
    }
}
