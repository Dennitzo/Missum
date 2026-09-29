using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Coding;
using Missum.Core.Contracts;
using System.Text.Json;

namespace Missum.Tests;

public sealed class WorkspaceToolTests
{
    [Fact]
    public async Task ClearingSessionsRemovesOnlyEmptyProjectsForTheSelectedMode()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var retained = await chats.CreateSessionAsync("Behalten", Missum.Core.Models.ChatMode.Coding);
        var deleted = await chats.CreateSessionAsync("Entfernen", Missum.Core.Models.ChatMode.General);
        await chats.SetCodingWorkspacePathAsync(retained.Id, Path.Combine(environment.Directory, "A"));
        await chats.SetCodingWorkspacePathAsync(deleted.Id, Path.Combine(environment.Directory, "B"));
        Assert.Equal(1, await chats.DeleteSessionsAsync(Missum.Core.Models.ChatMode.General));
        var remaining = Assert.Single(await chats.ListSessionsAsync());
        Assert.Equal(retained.Id, remaining.Id);
        Assert.Equal(remaining.SessionGroupId, Assert.Single(await chats.ListSessionGroupsAsync()).Id);
    }

}
