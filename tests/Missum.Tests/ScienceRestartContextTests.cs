using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScienceRestartContextTests
{
    [Fact]
    public async Task ReopenedPublicationUsesEditedProjectFileInsteadOfHistoricalResearchState()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<IChatRepository>();
        var repository = environment.Get<IScientificResearchRepository>();
        var session = await chats.CreateSessionAsync("Quantengravitation", ChatMode.ClaudeScience);
        var workspace = Directory.CreateDirectory(Path.Combine(environment.Directory, "workspace")).FullName;
        await chats.SetCodingWorkspacePathAsync(session.Id, workspace);
        var now = DateTimeOffset.UtcNow;
        var id = "research-" + session.Id.ToString("N");
        await repository.UpsertProjectAsync(new ScientificResearchProject(id, session.Id, "openProblem", "Frage", "Historischer Stand",
            "codingWorkspaceResearch", "multiPath", "active", 1, 1, now, now, workspace));
        var source = Path.Combine(workspace, "Publikation.md");
        const string original = "# Quantengravitation\n\nDie klassische Grenze ist hergeleitet. Die Korrektur bleibt offen.";
        await File.WriteAllTextAsync(source, original);
        using (var first = CreatePublications(repository, workspace))
        {
            var publication = await first.EnsureCurrentAsync(id);
            Assert.NotNull(publication);
            Assert.Equal(source, publication.MarkdownPath);
        }
        const string correction = "# Quantengravitation\n\nDie bisherige Korrektur war falsch. Der verbesserte Ansatz folgt direkt hier.";
        await File.WriteAllTextAsync(source, correction);
        using var reopened = CreatePublications(repository, workspace);
        var current = await reopened.EnsureCurrentAsync(id);
        Assert.NotNull(current);
        Assert.Equal(correction, await File.ReadAllTextAsync(current.MarkdownPath));
        Assert.Equal(Path.ChangeExtension(source, ".pdf"), current.PdfPath);
        Assert.Equal(0, current.Revision);
        Assert.Empty(Directory.EnumerateDirectories(workspace));
        Assert.Null(await repository.GetLatestCheckpointAsync(id));
    }

    private static ScientificPublicationService CreatePublications(IScientificResearchRepository repository, string workspace) =>
        new(repository, async (path, token) =>
        {
            var target = Path.ChangeExtension(path, ".pdf");
            await File.WriteAllTextAsync(target, "%PDF-1.7\n" + new string(' ', 2048), token);
            return target;
        }, workspace);
}
