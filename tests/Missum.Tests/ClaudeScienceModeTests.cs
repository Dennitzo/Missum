using Missum.Core.Contracts;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ClaudeScienceModeTests
{
    [Fact]
    public async Task ScienceModeHasItsOwnSessionFilterAndKeepsItsProjectWorkspace()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var general = await chats.CreateSessionAsync("ChatGPT session", ChatMode.General);
        var coding = await chats.CreateSessionAsync("Codex session", ChatMode.Coding);
        var science = await chats.CreateSessionAsync("Science session", ChatMode.ClaudeScience);

        Assert.Equal([general.Id], (await chats.ListSessionsAsync(ChatMode.General)).Select(item => item.Id));
        Assert.Equal([coding.Id], (await chats.ListSessionsAsync(ChatMode.Coding)).Select(item => item.Id));
        Assert.Equal([science.Id], (await chats.ListSessionsAsync(ChatMode.ClaudeScience)).Select(item => item.Id));
        await chats.SetCodingWorkspacePathAsync(science.Id, environment.Directory);
        var savedScience = await chats.GetSessionAsync(science.Id);
        Assert.Equal(environment.Directory, savedScience?.CodingWorkspacePath);
        var scienceGroup = Assert.Single(await chats.ListSessionGroupsAsync(ChatMode.ClaudeScience));
        Assert.Equal(savedScience?.SessionGroupId, scienceGroup.Id);
        Assert.Equal(environment.Directory, scienceGroup.WorkspacePath);
        Assert.Equal(ChatMode.ClaudeScience, scienceGroup.ChatMode);
        Assert.Single(System.IO.Directory.EnumerateFiles(
            Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v49-*.bak"));
        Assert.True(await environment.Get<IMissumDatabase>().CheckIntegrityAsync());
    }
}
