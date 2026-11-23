using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ClaudeMeetingsModeTests
{
    [Fact]
    public async Task MeetingsModeHasItsOwnSessionsAndProjectGroups()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var science = await chats.CreateSessionAsync("Science session", ChatMode.ClaudeScience);
        var meeting = await chats.CreateSessionAsync("Kundenveranstaltung", ChatMode.ClaudeMeetings);

        Assert.Equal([science.Id], (await chats.ListSessionsAsync(ChatMode.ClaudeScience)).Select(item => item.Id));
        Assert.Equal([meeting.Id], (await chats.ListSessionsAsync(ChatMode.ClaudeMeetings)).Select(item => item.Id));
        await chats.SetCodingWorkspacePathAsync(meeting.Id, environment.Directory);
        var saved = await chats.GetSessionAsync(meeting.Id);
        Assert.Equal(ChatMode.ClaudeMeetings, saved?.ChatMode);
        var group = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.ClaudeMeetings));
        Assert.Equal(ChatMode.ClaudeMeetings, group.ChatMode);
        Assert.Equal(environment.Directory, group.WorkspacePath);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v59-*.bak"));
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }

    [Theory]
    [InlineData("claudemeetings")]
    [InlineData("claude-meetings")]
    [InlineData("Meetings")]
    public void WireNamesParseToMeetingsMode(string value) =>
        Assert.Equal(ChatMode.ClaudeMeetings, AssistantCoordinator.ParseChatMode(value));
}
