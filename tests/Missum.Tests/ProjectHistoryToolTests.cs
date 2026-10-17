using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Memory;
using Missum.Core.Models;

namespace Missum.Tests;

public sealed class ProjectHistoryToolTests
{
    [Fact]
    public async Task ReadUsesThePersistedProjectAndPagesOnlyRequestedOriginalText()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var workspace = Path.Combine(environment.Directory, "project");
        var forged = Path.Combine(environment.Directory, "other");
        Directory.CreateDirectory(workspace); Directory.CreateDirectory(forged);
        var session = await chats.CreateSessionAsync("Project history");
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace, false);
        var source = new ProjectContextContribution("project", session.Id, Guid.NewGuid(), 3,
            new string('a', 10_000), DateTimeOffset.UtcNow, new string('x', 100_000));
        var history = new History(source);
        var broker = new LocalToolBroker(null!, null!, null!, chats, projectContexts: history);
        var proposal = new ToolProposal("history-read", "run-history", ClientToolNames.ProjectHistory,
            JsonSerializer.SerializeToElement(new { operation = "read", messageId = source.MessageId, offset = 4096, maximumCharacters = 1000 }),
            ToolRiskClass.ReadOnly, "Read original project history", DateTimeOffset.UtcNow.AddMinutes(5));
        var result = await broker.ExecuteAsync(proposal, session.Id, null, forged);
        Assert.Equal("completed", result.Status);
        Assert.Equal(workspace, history.Workspace);
        Assert.Equal(1000, result.Result.GetProperty("source").GetProperty("text").GetString()!.Length);
        Assert.Equal(5096, result.Result.GetProperty("nextOffset").GetInt32());
        Assert.DoesNotContain("originalText", result.Result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Result.GetRawText().Length < 2000);
        var invalid = proposal with { Arguments = JsonSerializer.SerializeToElement(new { operation = "read", messageId = source.MessageId, projectKey = "other" }) };
        Assert.Equal("failed", (await broker.ExecuteAsync(invalid, session.Id, null, forged)).Status);
        Assert.Equal(1, history.Reads);
    }

    private sealed class History(ProjectContextContribution source) : IProjectContextService
    {
        internal string? Workspace { get; private set; }
        internal int Reads { get; private set; }
        public Task<ProjectContextContribution?> ReadAsync(string workspace, Guid messageId, CancellationToken cancellationToken = default)
        { Workspace = workspace; Reads++; return Task.FromResult<ProjectContextContribution?>(source); }
        public Task<IReadOnlyList<ProjectContextContribution>> SearchAsync(string workspace, string query, int limit = 5, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<ProjectContextSnapshot?> GetAsync(string workspace, int contextTokens, string modelId, string role, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task CaptureAsync(string workspace, ChatMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
