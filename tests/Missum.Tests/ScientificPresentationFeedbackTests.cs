using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.App.Services;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Research;

namespace Missum.Tests;

public sealed class ScientificPresentationFeedbackTests
{
    private const string BadFormula = "$$x=\\notACommand{1}$$";
    private static readonly string[] MissingExperimentIds = ["missing-experiment"];
    private const string ParserError = "KaTeX parse error: Undefined control sequence: \\notACommand. Abschnitt \"Modell\", Ausdruck: $$x=\\notACommand{1}$$, Ursache: Undefined control sequence: \\notACommand";

    [Fact]
    public async Task StoredUpdateReturnsParserErrorToModelAndItsTargetedRepairPublishesCurrentPdf()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        var renders = 0;
        using var publications = fixture.Publications(async (path, token) =>
        {
            Interlocked.Increment(ref renders);
            if ((await File.ReadAllTextAsync(path, token)).Contains(BadFormula, StringComparison.Ordinal))
                throw new InvalidOperationException(ParserError);
            return await PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = fixture.Broker(publications, coordinator);
        var first = await broker.ExecuteAsync(fixture.Update("initial", 0, "$$x=1$$", includeOther: true), fixture.SessionId, null);
        Assert.Equal("completed", first.Status);
        Assert.Equal("ready", Presentation(first).GetProperty("status").GetString());
        var priorPdf = coordinator.GetSnapshot(fixture.ProjectId)!.Publication!;

        var failed = await broker.ExecuteAsync(fixture.Update("bad-equation", 1, BadFormula), fixture.SessionId, null);
        Assert.Equal("completed", failed.Status); // The durable mutation really succeeded.
        Assert.True(failed.Result.GetProperty("stored").GetBoolean());
        Assert.True(failed.Result.GetProperty("success").GetBoolean());
        Assert.Equal("failed", Presentation(failed).GetProperty("status").GetString());
        Assert.Contains("Forschungsstand gespeichert; PDF oder Simulation", failed.Message, StringComparison.Ordinal);
        var issue = PublicationError(failed);
        Assert.Equal("research.publication_content_invalid", issue.GetProperty("code").GetString());
        Assert.Contains(ParserError, issue.GetProperty("message").GetString(), StringComparison.Ordinal);
        var target = Assert.Single(issue.GetProperty("sections").EnumerateArray());
        Assert.Equal("model", target.GetProperty("id").GetString());
        Assert.Equal(2, target.GetProperty("revision").GetInt64());
        Assert.Equal(priorPdf, coordinator.GetSnapshot(fixture.ProjectId)!.Publication);
        Assert.True(File.Exists(priorPdf.PdfPath));

        // Receive the real serialized tool receipt, rather than a separate prompt hint.
        var model = new RepairingModelStub(fixture.ProjectId);
        var repair = model.NextTool(JsonSerializer.SerializeToElement(failed, MissumAiProtocol.CreateJsonOptions()));
        var fixedResult = await broker.ExecuteAsync(repair, fixture.SessionId, null);
        await coordinator.WaitForIdleAsync(fixture.ProjectId);

        Assert.Equal("completed", fixedResult.Status);
        Assert.Equal("ready", Presentation(fixedResult).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Presentation(fixedResult).GetProperty("publication").GetProperty("error").ValueKind);
        var state = await fixture.States.LoadWorkingStateAsync(fixture.ProjectId);
        Assert.Equal(3, state.Items.Single(item => item.Id == "model").Revision);
        Assert.Equal(1, state.Items.Single(item => item.Id == "other").Revision);
        Assert.Equal("Unveränderter wissenschaftlicher Abschnitt.", state.Items.Single(item => item.Id == "other").Data.GetProperty("contentMarkdown").GetString());
        Assert.Equal(state.PublicationRevision, coordinator.GetSnapshot(fixture.ProjectId)!.Publication!.Revision);
        Assert.Null(coordinator.GetSnapshot(fixture.ProjectId)!.PublicationError);
        Assert.Equal(3, renders);
    }

    [Fact]
    public async Task UnchangedResearchReadStillCarriesCurrentFailureAndSuccessfulRepairClearsIt()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        using var publications = fixture.Publications(async (path, token) =>
        {
            if ((await File.ReadAllTextAsync(path, token)).Contains(BadFormula, StringComparison.Ordinal))
                throw new InvalidOperationException(ParserError);
            return await PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = fixture.Broker(publications, coordinator);
        _ = await broker.ExecuteAsync(fixture.Update("bad-first", 0, BadFormula), fixture.SessionId, null);
        var overview = await broker.ExecuteAsync(fixture.Read("overview"), fixture.SessionId, null);
        var stamp = overview.Result.GetProperty("stateStamp").GetString();
        var unchanged = await broker.ExecuteAsync(fixture.Read("unchanged", stamp), fixture.SessionId, null);

        Assert.True(unchanged.Result.GetProperty("unchanged").GetBoolean());
        Assert.Equal("failed", Presentation(unchanged).GetProperty("status").GetString());
        Assert.Single(PublicationError(unchanged).GetProperty("sections").EnumerateArray());
        _ = await broker.ExecuteAsync(fixture.Update("correct-first", 1, "$$x=1$$"), fixture.SessionId, null);
        var corrected = await broker.ExecuteAsync(fixture.Read("corrected", stamp), fixture.SessionId, null);
        Assert.False(corrected.Result.GetProperty("unchanged").GetBoolean());
        Assert.Equal("ready", Presentation(corrected).GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Presentation(corrected).GetProperty("publication").GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task BoundedPendingReceiptDoesNotClaimPdfSuccessOrRepeatResearchMutation()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renders = 0;
        using var publications = fixture.Publications(async (path, token) =>
        {
            Interlocked.Increment(ref renders);
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return await PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var stored = await fixture.SaveAsync("pending", 0, "$$x=1$$");
            var pending = await coordinator.ObserveFeedbackAsync(stored.State, refresh: true, TimeSpan.Zero, timeout.Token);
            await entered.Task.WaitAsync(timeout.Token);
            Assert.Equal("pending", pending.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("publication").GetProperty("error").ValueKind);
            Assert.Null(coordinator.GetSnapshot(fixture.ProjectId)?.Publication);
            Assert.Equal(stored.State.Revision, (await fixture.States.LoadWorkingStateAsync(fixture.ProjectId)).Revision);
            finish.TrySetResult();
            var current = await coordinator.ObserveFeedbackAsync(stored.State, refresh: false, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.Equal("ready", current.GetProperty("status").GetString());
            Assert.Equal(1, renders);
        }
        finally { finish.TrySetResult(); await coordinator.WaitForIdleAsync(fixture.ProjectId, timeout.Token); }
    }

    [Fact]
    public async Task SupersededContentErrorNeverTargetsNewerSectionRevision()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failOld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishNew = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var publications = fixture.Publications(async (path, token) =>
        {
            if ((await File.ReadAllTextAsync(path, token)).Contains(BadFormula, StringComparison.Ordinal))
            {
                oldEntered.TrySetResult();
                await failOld.Task.WaitAsync(token);
                throw new InvalidOperationException(ParserError);
            }
            newEntered.TrySetResult();
            await finishNew.Task.WaitAsync(token);
            return await PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var publicationsSeen = new ConcurrentQueue<ScientificPresentationSnapshot>();
        coordinator.SnapshotPublished += (_, item) => publicationsSeen.Enqueue(item.Snapshot);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var old = await fixture.SaveAsync("old", 0, BadFormula);
            _ = await coordinator.ObserveFeedbackAsync(old.State, refresh: true, TimeSpan.Zero, timeout.Token);
            await oldEntered.Task.WaitAsync(timeout.Token);
            var current = await fixture.SaveAsync("new", 1, "$$x=2$$");
            _ = await coordinator.ObserveFeedbackAsync(current.State, refresh: true, TimeSpan.Zero, timeout.Token);
            failOld.TrySetResult();
            await newEntered.Task.WaitAsync(timeout.Token);
            var pending = await coordinator.ObserveFeedbackAsync(current.State, refresh: false, TimeSpan.Zero, timeout.Token);
            Assert.Equal("pending", pending.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, pending.GetProperty("publication").GetProperty("error").ValueKind);
            Assert.DoesNotContain(publicationsSeen, snapshot => snapshot.PublicationError?.Contains("notACommand", StringComparison.Ordinal) == true);
            finishNew.TrySetResult();
            var ready = await coordinator.ObserveFeedbackAsync(current.State, refresh: false, TimeSpan.FromSeconds(5), timeout.Token);
            Assert.Equal("ready", ready.GetProperty("status").GetString());
            Assert.Equal(current.State.PublicationRevision, ready.GetProperty("publicationRevision").GetInt64());
        }
        finally { failOld.TrySetResult(); finishNew.TrySetResult(); await coordinator.WaitForIdleAsync(fixture.ProjectId, timeout.Token); }
    }

    [Fact]
    public async Task CancellationStopsReceiptWaitButPreservesSavedUpdateAndIndependentRenderer()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renders = 0;
        using var publications = fixture.Publications(async (path, token) =>
        {
            Interlocked.Increment(ref renders);
            entered.TrySetResult();
            await finish.Task.WaitAsync(token);
            return await PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = fixture.Broker(publications, coordinator);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = new CancellationTokenSource();
        try
        {
            var update = broker.ExecuteAsync(fixture.Update("cancel-after-commit", 0, "$$x=1$$"), fixture.SessionId, null,
                cancellationToken: stop.Token);
            await entered.Task.WaitAsync(timeout.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => update);
            var operation = await fixture.States.ReadWorkingOperationAsync(fixture.ProjectId, "run:cancel-after-commit", null);
            Assert.NotNull(operation);
            Assert.True(operation.Success);
            finish.TrySetResult();
            await coordinator.WaitForIdleAsync(fixture.ProjectId, timeout.Token);
            Assert.NotNull(coordinator.GetSnapshot(fixture.ProjectId)!.Publication);
            Assert.Equal(1, renders);
        }
        finally { finish.TrySetResult(); await coordinator.WaitForIdleAsync(fixture.ProjectId, timeout.Token); }
    }

    [Fact]
    public async Task ReplayedOldMutationReportsCurrentPresentationWithoutRestoringOldContent()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        using var publications = fixture.Publications(PdfAsync);
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = fixture.Broker(publications, coordinator);
        var original = fixture.Update("original", 0, "$$x=1$$");
        _ = await broker.ExecuteAsync(original, fixture.SessionId, null);
        _ = await broker.ExecuteAsync(fixture.Update("next", 1, "$$x=2$$"), fixture.SessionId, null);

        var replayed = await broker.ExecuteAsync(original, fixture.SessionId, null);

        Assert.True(replayed.Result.GetProperty("replayed").GetBoolean());
        Assert.True(replayed.Result.GetProperty("stored").GetBoolean());
        Assert.False(replayed.Result.GetProperty("publicationChanged").GetBoolean());
        var current = await fixture.States.LoadWorkingStateAsync(fixture.ProjectId);
        Assert.Equal("$$x=2$$", current.Items.Single().Data.GetProperty("contentMarkdown").GetString());
        Assert.Equal(current.PublicationRevision, Presentation(replayed).GetProperty("publicationRevision").GetInt64());
        Assert.Equal("ready", Presentation(replayed).GetProperty("status").GetString());
    }

    [Fact]
    public async Task FirstResearchReadAfterCoordinatorRestartReconstructsDurableFailure()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        _ = await fixture.SaveAsync("persisted-before-restart", 0, BadFormula);
        using var publications = fixture.Publications(static (_, _) => throw new InvalidOperationException(ParserError));
        using var simulations = fixture.Simulations();
        using var restarted = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var read = await fixture.Broker(publications, restarted).ExecuteAsync(fixture.Read("resume-read"), fixture.SessionId, null);

        Assert.Equal("completed", read.Status);
        Assert.Equal("failed", Presentation(read).GetProperty("status").GetString());
        Assert.Equal("model", Assert.Single(PublicationError(read).GetProperty("sections").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(1, (await fixture.States.LoadWorkingStateAsync(fixture.ProjectId)).Items.Single().Revision);
    }

    [Fact]
    public async Task PreRenderFigureValidationReportsExactSnapshotAndSectionWithoutCallingRenderer()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var fixture = await Fixture.CreateAsync(environment);
        var renders = 0;
        using var publications = fixture.Publications((path, token) =>
        {
            Interlocked.Increment(ref renders);
            return PdfAsync(path, token);
        });
        using var simulations = fixture.Simulations();
        using var coordinator = new ScientificPresentationCoordinator(publications, simulations, static (_, _) => Task.CompletedTask);
        var broker = fixture.Broker(publications, coordinator);
        // The reference itself is durable and valid. It deliberately has no successful
        // measured image hash, so the publication validator (not the storage validator) rejects it.
        var now = DateTimeOffset.UtcNow;
        await fixture.Repository.SaveExperimentAsync(new("missing-experiment", fixture.ProjectId, "test", "[]", "[]", "{}",
            "research.code.execute failed-figure.py", "{}", "{\"runs\":[{\"exitCode\":1,\"inputHashes\":{},\"outputHashes\":{}}]}",
            "Image generation failed.", "[\"artifacts/figure.png\"]", "ProcessFailed", now, now));
        var proposal = Proposal("figure-without-receipt", ClientToolNames.ResearchUpdate, new
        {
            projectId = fixture.ProjectId, title = "Erdmagnetfeld", changes = new[] { new { id = "model", kind = "section",
                expectedRevision = 0, data = new { title = "Modell", contentMarkdown = "Fachlicher Text.", status = "unresolved",
                    experimentIds = MissingExperimentIds,
                    figureCaptions = new[] { new { experimentId = "missing-experiment", artifactPath = "artifacts/figure.png", caption = "Modell" } } } } },
        });

        var result = await broker.ExecuteAsync(proposal, fixture.SessionId, null);

        Assert.True(result.ErrorCode is null, $"{result.ErrorCode}: {result.Message}\n{result.Result.GetRawText()}");
        Assert.Equal("completed", result.Status);
        Assert.True(result.Result.GetProperty("stored").GetBoolean());
        var error = PublicationError(result);
        Assert.True(error.GetProperty("contentRepairRequired").GetBoolean());
        Assert.Contains("keinen zugeordneten erfolgreichen Ausführungsbeleg", error.GetProperty("message").GetString(), StringComparison.Ordinal);
        var section = Assert.Single(error.GetProperty("sections").EnumerateArray());
        Assert.Equal("model", section.GetProperty("id").GetString());
        Assert.Equal(1, section.GetProperty("revision").GetInt64());
        var state = await fixture.States.LoadWorkingStateAsync(fixture.ProjectId);
        Assert.Equal(state.PublicationRevision, Presentation(result).GetProperty("publicationRevision").GetInt64());
        Assert.Equal(0, renders);
    }

    private static JsonElement Presentation(ClientToolResult receipt) => receipt.Result.GetProperty("presentation");
    private static JsonElement PublicationError(ClientToolResult receipt) => Presentation(receipt).GetProperty("publication").GetProperty("error");

    private static async Task<string?> PdfAsync(string source, CancellationToken token)
    {
        var pdf = Path.ChangeExtension(source, ".pdf");
        await File.WriteAllBytesAsync(pdf, Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string(' ', 2048) + "\n%%EOF\n"), token);
        return pdf;
    }

    private sealed class RepairingModelStub(string projectId)
    {
        internal ToolProposal NextTool(JsonElement serializedReceipt)
        {
            var receipt = serializedReceipt.GetProperty("result");
            var feedback = receipt.GetProperty("presentation");
            Assert.True(receipt.GetProperty("stored").GetBoolean());
            Assert.Equal("failed", feedback.GetProperty("status").GetString());
            var issue = feedback.GetProperty("publication").GetProperty("error");
            Assert.True(issue.GetProperty("contentRepairRequired").GetBoolean());
            Assert.Contains("Undefined control sequence", issue.GetProperty("message").GetString(), StringComparison.Ordinal);
            var target = Assert.Single(issue.GetProperty("sections").EnumerateArray());
            return Proposal("model-targeted-fix", ClientToolNames.ResearchUpdate, new
            {
                projectId, changes = new[] { new { id = target.GetProperty("id").GetString(), kind = "section",
                    expectedRevision = target.GetProperty("revision").GetInt64(),
                    data = new { title = "Modell", contentMarkdown = "$$x=1$$", status = "unresolved", order = 10 } } },
            });
        }
    }

    private sealed record Fixture(TestEnvironment Environment, Guid SessionId, string ProjectId,
        IScientificResearchRepository Repository, IScientificResearchStateRepository States, EmptySandbox Sandbox)
    {
        internal static async Task<Fixture> CreateAsync(TestEnvironment environment)
        {
            var session = await environment.Get<IChatRepository>().CreateSessionAsync("Erdmagnetfeld", ChatMode.ClaudeScience);
            var projectId = "research-" + session.Id.ToString("N");
            var repository = environment.Get<IScientificResearchRepository>();
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertProjectAsync(new(projectId, session.Id, "scientificEvidence", "Erdmagnetfeld", "Erdmagnetfeld",
                "sandboxResearch", "multiPath", "active", 2, 1, now, now));
            return new(environment, session.Id, projectId, repository,
                Assert.IsAssignableFrom<IScientificResearchStateRepository>(repository), new(Path.Combine(environment.Directory, "sandbox")));
        }

        internal ScientificPublicationService Publications(Func<string, CancellationToken, Task<string?>> render) =>
            new(Repository, render, Path.Combine(Environment.Directory, "publications"));
        internal ScientificSimulationService Simulations() => new(Repository, Sandbox);
        internal LocalToolBroker Broker(ScientificPublicationService publications, ScientificPresentationCoordinator coordinator) =>
            new(null!, null!, null!, Environment.Get<IChatRepository>(), sciencePresentation: coordinator,
                scientificResearch: Repository, scientificPublications: publications);
        internal ToolProposal Update(string id, long revision, string markdown, bool includeOther = false)
        {
            var changes = new List<ResearchWorkingChange> { Section("model", revision, markdown) };
            if (includeOther) changes.Add(Section("other", 0, "Unveränderter wissenschaftlicher Abschnitt."));
            return Proposal(id, ClientToolNames.ResearchUpdate, new { projectId = ProjectId, title = "Erdmagnetfeld", changes });
        }
        internal ToolProposal Read(string id, string? knownStateStamp = null) => knownStateStamp is null
            ? Proposal(id, ClientToolNames.ResearchRead, new { projectId = ProjectId, view = "overview" })
            : Proposal(id, ClientToolNames.ResearchRead, new { projectId = ProjectId, view = "overview", knownStateStamp });
        internal Task<ResearchWorkingUpdateResult> SaveAsync(string operation, long revision, string markdown) =>
            States.ApplyWorkingUpdateAsync(ProjectId, operation, null, "Erdmagnetfeld", [Section("model", revision, markdown)]);
        private static ResearchWorkingChange Section(string id, long revision, string markdown) =>
            new(id, "section", revision, JsonSerializer.SerializeToElement(new { title = id == "model" ? "Modell" : "Einordnung",
                contentMarkdown = markdown, status = "unresolved", order = id == "model" ? 10 : 20 }));
    }

    private static ToolProposal Proposal(string id, string name, object arguments) => new(id, "run", name,
        JsonSerializer.SerializeToElement(arguments, MissumAiProtocol.CreateJsonOptions()),
        name == ClientToolNames.ResearchRead ? ToolRiskClass.ReadOnly : ToolRiskClass.LocalMutation,
        "Science presentation feedback", DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class EmptySandbox(string root) : IResearchSandboxService
    {
        public Task<ResearchSandboxLayout> EnsureProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            var directory = Path.Combine(root, projectId);
            foreach (var child in new[] { "inputs", "work", "artifacts", "notebooks", "manuscripts", "env", "runs", "snapshots" })
                Directory.CreateDirectory(Path.Combine(directory, child));
            return Task.FromResult(new ResearchSandboxLayout(projectId, directory, Path.Combine(directory, "inputs"),
                Path.Combine(directory, "work"), Path.Combine(directory, "artifacts"), Path.Combine(directory, "notebooks"),
                Path.Combine(directory, "manuscripts"), Path.Combine(directory, "env"), Path.Combine(directory, "runs"),
                Path.Combine(directory, "snapshots"), DateTimeOffset.UtcNow, "test"));
        }
        public Task<ResearchSandboxRuntimeStatus> PrepareRuntimeAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxFileChange> WriteTextAsync(string projectId, string relativePath, string content,
            string? expectedSha256 = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreChangeSetAsync(string projectId, string changeSetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ResearchSandboxRunResult> RunPythonAsync(string projectId, string relativeScriptPath,
            IReadOnlyList<string>? arguments = null, int timeoutSeconds = 7200, string? relativeWorkingDirectory = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ArchiveProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RestoreProjectAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
