using System.Globalization;
using System.Text.Json;
using Missum.App.Services;
using Missum.App.ViewModels;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Missum.Tests;

public sealed class SidebarSessionOrderingTests
{
    private static readonly DateTimeOffset Earlier = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Earlier.AddDays(1);

    [Theory]
    [InlineData(ChatMode.General)]
    [InlineData(ChatMode.Coding)]
    [InlineData(ChatMode.ClaudeScience)]
    public async Task NativeDraftFlushAndRepeatedProjectNavigationPreserveModificationOrderAndLastActivity(ChatMode mode)
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("Erste Sitzung", mode);
        var second = await chats.CreateSessionAsync("Zweite Sitzung", mode);
        var project = Directory.CreateDirectory(Path.Combine(environment.Directory, "project")).FullName;
        await chats.SetCodingWorkspacePathAsync(first.Id, project);
        await chats.SetCodingWorkspacePathAsync(second.Id, project);
        await SetTimesAsync(environment, first.Id, Earlier, Earlier);
        await SetTimesAsync(environment, second.Id, Earlier, Later);
        var before = await chats.ListSessionsAsync(mode);
        var order = before.Select(item => item.Id).ToArray();
        Assert.Equal(second.Id, order[0]);
        using var settings = new SettingsCoordinator(environment.Get<ISettingsStore>());
        await settings.InitializeAsync();
        await settings.UpdateAsync(current => current with
        {
            SelectedChatMode = mode, ActiveSessionId = second.Id,
            ActiveGeneralSessionId = mode == ChatMode.General ? second.Id : null,
            ActiveCodingSessionId = mode == ChatMode.Coding ? second.Id : null,
            ActiveClaudeScienceSessionId = mode == ChatMode.ClaudeScience ? second.Id : null,
            LastActivityAt = Earlier, LastActivityText = "Letzte tatsächliche Änderung",
        });
        var recent = new RecentActivityService(settings, new ShellViewModel(), NullLogger<RecentActivityService>.Instance);
        var coordinator = new AssistantCoordinator(chats, environment.Get<IDocumentIngestor>(),
            environment.Get<IContextAssembler>(), environment.Get<IPromptTriggerRepository>(),
            environment.Get<IAssistantAttachmentRepository>(), environment.Get<IChatArtifactRepository>(),
            environment.Get<IConversationSnapshotRepository>(), null, settings, recent);

        for (var index = 0; index < 6; index++)
        {
            var target = index % 2 == 0 ? first : second;
            var outgoing = await chats.GetSessionAsync(settings.Current.ActiveSessionId!.Value);
            Assert.NotNull(outgoing);
            // Native navigation always flushes the current composer first.
            await coordinator.SaveDraftAsync(outgoing.Id, outgoing.Draft);
            JsonElement emitted = default;
            await coordinator.HandleAsync(new(2, "session.open", "navigation-" + index,
                JsonSerializer.SerializeToElement(new { sessionId = target.Id })), (type, data, _) =>
            {
                Assert.Equal("session.changed", type);
                emitted = JsonSerializer.SerializeToElement(data, JsonSerializerOptions.Web);
                return Task.CompletedTask;
            });

            Assert.Equal(order, (await chats.ListSessionsAsync(mode)).Select(item => item.Id));
            var group = Assert.Single(emitted.GetProperty("sessionGroups").EnumerateArray());
            Assert.Equal(order, group.GetProperty("sessionIds").EnumerateArray().Select(item => item.GetGuid()));
            Assert.Equal(order, AssistantSidebarSessionOrder.Sort(emitted.GetProperty("sessions").EnumerateArray())
                .Select(item => item.GetProperty("id").GetGuid()));
            Assert.Equal(Earlier, settings.Current.LastActivityAt);
            Assert.Equal("Letzte tatsächliche Änderung", settings.Current.LastActivityText);
        }
        Assert.All(await chats.ListSessionsAsync(mode), current =>
        {
            var previous = before.Single(item => item.Id == current.Id);
            Assert.Equal(previous.UpdatedAt, current.UpdatedAt);
            Assert.Equal(previous.ConversationRevision, current.ConversationRevision);
        });
    }

    [Fact]
    public async Task ChangedDraftPersistsWithoutPretendingTheConversationChanged()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var session = await chats.CreateSessionAsync("Ungesendeter Entwurf");
        await SetTimesAsync(environment, session.Id, Earlier, Earlier);
        var before = (await chats.GetSessionAsync(session.Id))!;

        await chats.SaveDraftAsync(session.Id, "Dieser Text ist noch nicht gesendet.");
        await chats.SaveDraftAsync(session.Id, "Dieser Text ist noch nicht gesendet.");

        var saved = (await environment.Get<IConversationSnapshotRepository>().GetAsync(session.Id))!.Session;
        Assert.Equal("Dieser Text ist noch nicht gesendet.", saved.Draft);
        Assert.Equal(before.UpdatedAt, saved.UpdatedAt);
        Assert.Equal(before.ConversationRevision, saved.ConversationRevision);
        await chats.ClearDraftIfMatchesAsync(session.Id, saved.Draft);
        Assert.Equal(Earlier, (await chats.GetSessionAsync(session.Id))!.UpdatedAt);
    }

    [Fact]
    public async Task ActualNewMessageMovesItsSessionByModificationDate()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var old = await chats.CreateSessionAsync("Älterer Chat");
        var recent = await chats.CreateSessionAsync("Neuerer Chat");
        await SetTimesAsync(environment, old.Id, Earlier, Earlier);
        await SetTimesAsync(environment, recent.Id, Earlier, Later);
        Assert.Equal(recent.Id, (await chats.ListSessionsAsync())[0].Id);

        await chats.AddMessageAsync(old.Id, ChatRole.User, "Eine neue echte Nachricht", MessageStatus.Completed);

        Assert.Equal(old.Id, (await chats.ListSessionsAsync())[0].Id);
        Assert.True((await chats.GetSessionAsync(old.Id))!.UpdatedAt > Later);
    }

    [Fact]
    public async Task RepositoryAndNativeSidebarUseTheSameDeterministicDateTieBreaks()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var first = await chats.CreateSessionAsync("A");
        var second = await chats.CreateSessionAsync("B");
        var third = await chats.CreateSessionAsync("C");
        await SetTimesAsync(environment, first.Id, Earlier, Later);
        await SetTimesAsync(environment, second.Id, Later, Later);
        await SetTimesAsync(environment, third.Id, Later, Later);
        var expected = new[] { second, third }.OrderByDescending(item => item.Id.ToString("D"), StringComparer.Ordinal)
            .Select(item => item.Id).Append(first.Id).ToArray();
        var stored = await chats.ListSessionsAsync();
        Assert.Equal(expected, stored.Select(item => item.Id));
        var shuffled = stored.Reverse().Select(item => JsonSerializer.SerializeToElement(new
        {
            id = item.Id, item.CreatedAt, item.UpdatedAt, isActiveSession = item.Id == first.Id,
        }, JsonSerializerOptions.Web));

        Assert.Equal(expected, AssistantSidebarSessionOrder.Sort(shuffled).Select(item => item.GetProperty("id").GetGuid()));
    }

    [Fact]
    public void MissingLegacyDatesHaveAStableIdFallback()
    {
        var first = JsonSerializer.SerializeToElement(new { id = "a", updatedAt = "invalid" });
        var second = JsonSerializer.SerializeToElement(new { id = "b", updatedAt = 123 });
        var known = JsonSerializer.SerializeToElement(new { id = "known", updatedAt = Later });

        var sorted = AssistantSidebarSessionOrder.Sort([first, known, second]);

        Assert.Equal("known", sorted[0].GetProperty("id").GetString());
        Assert.Equal("b", sorted[1].GetProperty("id").GetString());
        Assert.Equal("a", sorted[2].GetProperty("id").GetString());
    }

    private static async Task SetTimesAsync(TestEnvironment environment, Guid id, DateTimeOffset created, DateTimeOffset updated)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = environment.DatabasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chat_sessions SET created_at=$created,updated_at=$updated WHERE id=$id;";
        command.Parameters.AddWithValue("$created", created.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", updated.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }
}
