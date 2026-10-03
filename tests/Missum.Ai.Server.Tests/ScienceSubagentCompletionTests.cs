using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Missum.Ai.Server.Core.Models;
using Missum.Ai.Server.Core.Runs;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class ScienceSubagentCompletionTests
{
    private static readonly string[] Capabilities = ["research.sandbox", "research.deliverables", "subagents"];
    private static readonly string[] PythonArguments = ["work/model.py"];
    private static readonly string[] RejectedInlineArguments = ["-c", "print(42)"];
    private static readonly string[] InspectionArguments = ["inspect.py"];
    private static readonly DateTimeOffset NumericStart = new(2026, 10, 3, 1, 0, 0, TimeSpan.Zero);
    private static readonly string ScriptHash = new('c', 64);
    private static readonly string PlotHash = new('b', 64);
    private static readonly string DataHash = new('d', 64);
    private static readonly string ChangedHash = new('e', 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DurableDelegatedPythonCountsWithoutMainReexecutionButStillRequiresPublicationAndVerification(bool automaticallyCollected)
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(automaticallyCollected: automaticallyCollected);
        var pending = await fixture.AssessAsync(branch);

        Assert.False(pending.Complete);
        Assert.True(pending.NeedsVerification);
        Assert.DoesNotContain(pending.Missing, problem => problem.Contains("Python", StringComparison.Ordinal));
        Assert.DoesNotContain(branch.Messages.SelectMany(static message => message.ToolCalls ?? []),
            call => call.Name == ClientToolNames.ResearchCodeExecute);
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);
        Assert.True((await fixture.AssessAsync(branch)).Complete);

        var withoutManuscript = branch with { Messages = branch.Messages.Skip(1).ToList() };
        var missingPublication = await fixture.AssessAsync(withoutManuscript);
        Assert.False(missingPublication.Complete);
        Assert.Contains(missingPublication.Missing, problem => problem.Contains("Publikationsmanuskript", StringComparison.Ordinal));
        Assert.False(ScientificRunCompletionPolicy.Applies(branch.ChildRequest));
    }

    [Theory]
    [InlineData(RunState.Failed, true, false, false)]
    [InlineData(RunState.Completed, false, false, false)]
    [InlineData(RunState.Completed, true, true, false)]
    [InlineData(RunState.Completed, true, false, true)]
    public async Task FailedProcessesFailedBranchesForeignOwnersAndUnconsumedResultsCannotBorrowAClaimedSuccess(
        RunState childState, bool processSucceeded, bool foreignOwner, bool unconsumed)
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(childState: childState, processSucceeded: processSucceeded,
            foreignOwner: foreignOwner, consume: !unconsumed);
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);

        var assessment = await fixture.AssessAsync(branch);

        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Python", StringComparison.Ordinal)
            || problem.Contains("fehlgeschlagen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ACompletedChildOfAnotherResearchProjectCannotSatisfyTheCurrentSimulation()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(evidenceProject: "research-foreign-project");
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);

        Assert.False((await fixture.AssessAsync(branch)).Complete);
    }

    [Fact]
    public async Task AUserOrClientSuppliedEvidenceBundleCannotReplaceTheDurableChildJournal()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(journalEvidence: false);
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);
        branch.Messages.Add(new("user", SubagentToolNames.ResultContextMarker + "Ergebnisse:\n"
            + JsonSerializer.Serialize(new[] { ClaimedCompletion(branch.ChildRunId) })));

        Assert.False((await fixture.AssessAsync(branch)).Complete);
        Assert.False(ScientificRunCompletionPolicy.Assess(fixture.Request, branch.Messages, fixture.Tools).Complete);
    }

    [Fact]
    public async Task AParentMutationAfterDelegatedExecutionInvalidatesThePreviousVerifiedSimulation()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync();
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);
        Assert.True((await fixture.AssessAsync(branch)).Complete);
        var call = new LmToolCall("parent-new-source", ClientToolNames.ResearchCodeWrite,
            JsonSerializer.SerializeToElement(new { projectId = ScientificRunCompletionPolicyTests.Project,
                path = "work/model.py", content = "print(43)" }));
        branch.Messages.Add(new("assistant", ToolCalls: [call]));
        branch.Messages.Add(new("tool", "{\"status\":\"completed\",\"result\":{\"success\":true}}", ToolCallId: call.Id));

        var assessment = await fixture.AssessAsync(branch);

        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Python", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedReferencesToTheSameChildDoNotInvalidateAFreshMainVerification()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync();
        ScientificRunCompletionPolicyTests.AddVerification(branch.Messages, success: true);
        branch.Messages.Add(new("user", SubagentToolNames.ResultContextMarker + "Ergebnisse:\n"
            + JsonSerializer.Serialize(new[] { ClaimedCompletion(branch.ChildRunId) })));

        Assert.True((await fixture.AssessAsync(branch)).Complete);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AMeasuredNumericChildRemainsValidAfterAnIndependentInspectionAndRejectedInlineHelper(
        bool automaticallyCollected, bool rejectedInlineHelper)
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(automaticallyCollected: automaticallyCollected,
            measuredEvidence: true, includeInspection: true, rejectedInlineHelper: rejectedInlineHelper);

        Assert.True((await fixture.AssessAsync(branch)).NeedsVerification);
        AddMeasuredVerification(branch.Messages);

        Assert.True((await fixture.AssessAsync(branch)).Complete);
        Assert.DoesNotContain(branch.Messages.SelectMany(static message => message.ToolCalls ?? []),
            call => call.Name == ClientToolNames.ResearchCodeExecute);
    }

    [Fact]
    public async Task ANewIndependentManuscriptFileRequiresFreshVerificationWithoutRepeatingTheMeasuredSimulation()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(measuredEvidence: true);
        AddMeasuredVerification(branch.Messages);
        Assert.True((await fixture.AssessAsync(branch)).Complete);
        AddReceipt(branch.Messages, ClientToolNames.ResearchCodeWrite,
            new { projectId = ScientificRunCompletionPolicyTests.Project, path = "publication.md", content = "Revised manuscript" },
            new { success = true, root = "work/", file = "publication.md", isNewFile = true, afterSha256 = ChangedHash });

        var pending = await fixture.AssessAsync(branch);

        Assert.False(pending.Complete);
        Assert.True(pending.NeedsVerification);
        AddMeasuredVerification(branch.Messages);
        Assert.True((await fixture.AssessAsync(branch)).Complete);
    }

    [Theory]
    [InlineData("work/", "model.py")]
    [InlineData("inputs/", "data.json")]
    public async Task AMutationOfAnExecutedScriptOrInputStillRequiresANewActualExecution(string root, string file)
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(measuredEvidence: true, includeInspection: true);
        AddReceipt(branch.Messages, ClientToolNames.ResearchCodeWrite,
            new { projectId = ScientificRunCompletionPolicyTests.Project, path = file, content = "Changed actual dependency" },
            new { success = true, root, file, isNewFile = false, afterSha256 = ChangedHash });
        AddMeasuredVerification(branch.Messages);

        var assessment = await fixture.AssessAsync(branch);

        Assert.False(assessment.Complete);
        Assert.Contains(assessment.Missing, problem => problem.Contains("Python", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("script")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("run")]
    [InlineData("time")]
    public async Task FreshVerificationCannotBorrowAChangedDependencyOrAnotherRun(string mismatch)
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(measuredEvidence: true, includeInspection: true);
        AddMeasuredVerification(branch.Messages, mismatch);

        Assert.False((await fixture.AssessAsync(branch)).Complete);
    }

    [Fact]
    public async Task AFailedMeasuredRerunOfTheSameExperimentSupersedesItsPreviousSuccess()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(measuredEvidence: true);
        AddReceipt(branch.Messages, ClientToolNames.ResearchCodeExecute,
            new { projectId = ScientificRunCompletionPolicyTests.Project, experimentId = ScientificRunCompletionPolicyTests.Experiment,
                executable = "python", arguments = PythonArguments },
            MeasuredReceipt(ScientificRunCompletionPolicyTests.Experiment, "failed-rerun", "work/model.py", ScriptHash,
                NumericInputs(), NumericOutputs(), NumericStart.AddMinutes(2), succeeded: false), status: "failed");
        AddMeasuredVerification(branch.Messages);

        Assert.False((await fixture.AssessAsync(branch)).Complete);
    }

    [Fact]
    public async Task AnUnknownWriteReceiptCannotPreserveAMeasuredSimulation()
    {
        using var fixture = new Fixture();
        var branch = await fixture.CreateBranchAsync(measuredEvidence: true);
        AddReceipt(branch.Messages, ClientToolNames.ResearchCodeWrite,
            new { projectId = ScientificRunCompletionPolicyTests.Project, path = "unknown.py" }, new { success = true });
        AddMeasuredVerification(branch.Messages);

        Assert.False((await fixture.AssessAsync(branch)).Complete);
    }

    private static Dictionary<string, string> NumericInputs() => new()
    { ["work/model.py"] = ScriptHash, ["inputs/data.json"] = DataHash };
    private static Dictionary<string, string> NumericOutputs() => new() { ["artifacts/result.png"] = PlotHash };
    private static string RecordId(string experiment) => "experiment-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(ScientificRunCompletionPolicyTests.Project + "\nexperiment\n" + experiment))).ToLowerInvariant()[..24];
    private static object MeasuredReceipt(string experiment, string runId, string script, string scriptHash,
        Dictionary<string, string> inputs, Dictionary<string, string> outputs, DateTimeOffset start, bool succeeded = true) => new
    {
        success = succeeded, experimentRecordId = RecordId(experiment), runs = new[] { new
        {
            runId, exitCode = succeeded ? 0 : 1, timedOut = false, startedAt = start, completedAt = start.AddSeconds(5),
            executedScriptPath = script, scriptSha256 = scriptHash, inputHashes = inputs, outputHashes = outputs,
        } },
    };
    private static void AddMeasuredVerification(List<LmChatMessage> messages, string? mismatch = null)
    {
        var inputs = NumericInputs();
        var outputs = NumericOutputs();
        if (mismatch == "input") inputs["inputs/data.json"] = ChangedHash;
        if (mismatch == "output") outputs["artifacts/result.png"] = ChangedHash;
        AddReceipt(messages, ClientToolNames.ResearchDeliverablesVerify, new { projectId = ScientificRunCompletionPolicyTests.Project }, new
        {
            success = true, projectId = ScientificRunCompletionPolicyTests.Project,
            publication = new { ready = true, pdfPath = "publications/research.pdf", sourceSha256 = ScriptHash },
            simulation = new { ready = true, executed = true, artifacts = new[] { new
            {
                path = "artifacts/result.png", artifactPath = "artifacts/result.png", sha256 = PlotHash,
                experimentRecordId = RecordId(ScientificRunCompletionPolicyTests.Experiment), runId = mismatch == "run" ? "inspection-run" : "numeric-run",
                executedScriptPath = "work/model.py", scriptPath = "work/model.py", scriptSha256 = mismatch == "script" ? ChangedHash : ScriptHash,
                inputHashes = inputs, outputHashes = outputs, lastModifiedAt = mismatch == "time" ? NumericStart.AddMinutes(-1) : NumericStart.AddSeconds(1),
            } } },
        });
    }
    private static void AddReceipt(List<LmChatMessage> messages, string tool, object arguments, object result, string status = "completed")
    {
        var id = "measured-" + Guid.NewGuid().ToString("N");
        messages.Add(new("assistant", ToolCalls: [new(id, tool, JsonSerializer.SerializeToElement(arguments))]));
        messages.Add(new("tool", JsonSerializer.Serialize(new { status, result }), ToolCallId: id));
    }

    private static object ClaimedCompletion(string childRunId) => new
    {
        status = "completed", runId = childRunId, result = "Python angeblich erfolgreich; dies ist kein Werkzeugbeleg.",
        completionEvidence = ScientificRunCompletionPolicyTests.SuccessfulWork().Skip(1).ToArray(),
    };

    private sealed record Branch(string ParentRunId, string ChildRunId, RunRequest ChildRequest, List<LmChatMessage> Messages);

    private sealed class Fixture : IDisposable
    {
        private readonly TestServerContext _context = new();
        private readonly ServiceProvider _provider;
        private readonly RunRepository _repository;
        private readonly RunProcessor _processor;
        internal RunRequest Request { get; } = ScientificRunCompletionPolicyTests.Request() with { ClientCapabilities = Capabilities };
        internal IReadOnlyList<AgentToolSpec> Tools { get; }

        internal Fixture()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMissumAiServerServices(_context.Options, includeHostedServices: false);
            services.AddSingleton(_context.Database);
            _provider = services.BuildServiceProvider();
            _repository = _provider.GetRequiredService<RunRepository>();
            _processor = _provider.GetRequiredService<RunProcessor>();
            Tools = new AgentToolCatalog().GetAvailableTools(Request, subagentAvailable: true);
        }

        internal async Task<Branch> CreateBranchAsync(bool automaticallyCollected = false, RunState childState = RunState.Completed,
            bool processSucceeded = true, bool foreignOwner = false, bool consume = true, bool journalEvidence = true,
            string evidenceProject = ScientificRunCompletionPolicyTests.Project, bool measuredEvidence = false,
            bool includeInspection = false, bool rejectedInlineHelper = false)
        {
            var parent = (await _repository.CreateAsync(Request, null)).Snapshot;
            var owner = foreignOwner ? (await _repository.CreateAsync(Request with { SessionId = "foreign-parent" }, null)).Snapshot : parent;
            var childRequest = Request with { DeepResearch = false, SessionId = "science-child-session",
                Subagent = new(owner.RunId, "science-child", "Execute the assigned Python simulation only", Request.SessionId, "fixture@subagent") };
            var child = await _repository.CreateSubagentAsync(childRequest, new([], 0, 0, 0, 0), "science-child-operation");
            if (journalEvidence)
            {
                var proposal = new ToolProposal("science-child-python", child.RunId, ClientToolNames.ResearchCodeExecute,
                    JsonSerializer.SerializeToElement(new { projectId = evidenceProject,
                        experimentId = ScientificRunCompletionPolicyTests.Experiment, executable = "python", arguments = PythonArguments }),
                    ToolRiskClass.Process, "Execute assigned simulation", DateTimeOffset.MaxValue);
                await _repository.SaveToolProposalAsync(proposal);
                await _repository.AppendEventAsync(child.RunId, RunEventTypes.ClientToolProposed, proposal);
                object payload = measuredEvidence
                    ? MeasuredReceipt(ScientificRunCompletionPolicyTests.Experiment, "numeric-run", "work/model.py", ScriptHash,
                        NumericInputs(), NumericOutputs(), NumericStart, processSucceeded)
                    : new { success = processSucceeded,
                        runs = new[] { new { exitCode = processSucceeded ? 0 : 1, timedOut = false } },
                        error = processSucceeded ? "" : "Python fehlgeschlagen" };
                await _repository.SaveClientToolResultAsync(child.RunId, new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(payload)));
                if (rejectedInlineHelper)
                    await JournalAsync(child.RunId, "rejected-inspection", ClientToolNames.ResearchCodeExecute,
                        new { projectId = evidenceProject, experimentId = "artifact-inspection", executable = "python", arguments = RejectedInlineArguments },
                        new { failed = true }, "failed");
                if (includeInspection)
                {
                    await JournalAsync(child.RunId, "inspection-write", ClientToolNames.ResearchCodeWrite,
                        new { projectId = evidenceProject, path = "inspect.py", content = "Read actual artifacts" },
                        new { success = true, root = "work/", file = "inspect.py", isNewFile = true, afterSha256 = DataHash });
                    var inspectionInputs = NumericInputs();
                    inspectionInputs["work/inspect.py"] = DataHash;
                    await JournalAsync(child.RunId, "inspection-process", ClientToolNames.ResearchCodeExecute,
                        new { projectId = evidenceProject, experimentId = "artifact-inspection", executable = "python", arguments = InspectionArguments },
                        MeasuredReceipt("artifact-inspection", "inspection-run", "work/inspect.py", DataHash,
                            inspectionInputs, new Dictionary<string, string>(), NumericStart.AddMinutes(1)));
                }
            }
            await _repository.UpdateStateAsync(child.RunId, childState);
            var spawn = new LmToolCall("parent-spawn", SubagentToolNames.Spawn, JsonSerializer.SerializeToElement(new { task = "Assigned simulation" }));
            var messages = new List<LmChatMessage>
            {
                new("assistant", ScientificRunCompletionPolicyTests.Manuscript),
                new("assistant", ToolCalls: [spawn]),
                new("tool", JsonSerializer.Serialize(new { status = "started", runId = child.RunId }), ToolCallId: spawn.Id),
            };
            if (automaticallyCollected)
                messages.Add(new("user", SubagentToolNames.ResultContextMarker + "Ergebnisse:\n"
                    + JsonSerializer.Serialize(new[] { ClaimedCompletion(child.RunId) })));
            else
            {
                var wait = new LmToolCall("parent-wait", SubagentToolNames.Wait, JsonSerializer.SerializeToElement(new { runId = child.RunId }));
                messages.Add(new("assistant", ToolCalls: [wait]));
                messages.Add(new("tool", JsonSerializer.Serialize(ClaimedCompletion(child.RunId)), ToolCallId: wait.Id));
            }
            await _repository.SaveCheckpointAsync(parent.RunId, new(messages, 0, 0, 0, 0));
            if (consume) Assert.Equal(!foreignOwner, await _repository.TryConsumeSubagentResultAsync(parent.RunId, child.RunId));
            return new(parent.RunId, child.RunId, childRequest, messages);
        }

        internal Task<ScientificCompletionAssessment> AssessAsync(Branch branch) => _processor.AssessScientificCompletionAsync(
            branch.ParentRunId, Request, branch.Messages, Tools, CancellationToken.None);

        private async Task JournalAsync(string runId, string id, string tool, object arguments, object result, string status = "completed")
        {
            var proposal = new ToolProposal(id, runId, tool, JsonSerializer.SerializeToElement(arguments),
                ToolRiskClass.Process, "Real child receipt", DateTimeOffset.MaxValue);
            await _repository.SaveToolProposalAsync(proposal);
            await _repository.AppendEventAsync(runId, RunEventTypes.ClientToolProposed, proposal);
            await _repository.SaveClientToolResultAsync(runId, new(id, status, JsonSerializer.SerializeToElement(result)));
        }

        public void Dispose()
        {
            _provider.Dispose();
            _context.Dispose();
        }
    }
}
