using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Missum.Ai.Contracts;
using Missum.Core.Contracts;
using Missum.Core.Models;
using Missum.Core.Coding;
using Missum.Core.Extensions;
using Missum.App.Services.Extensions;

namespace Missum.App.Services;

/// <summary>
/// Executes session document and Coding tools
/// bound to the active run's selected project. Valid tools execute automatically
/// under the user's standing authorization, including local processes and CAD mutations.
/// </summary>
public sealed partial class LocalToolBroker(
    MissumAiConnectionService connection,
    IDocumentIngestor documents,
    LocalDocumentToolService documentTools,
    IChatRepository chats,
    IExtensionActionCatalog? extensionActions = null,
    IExtensionRuntimeService? extensionRuntime = null,
    Missum.Core.Research.IResearchSandboxService? researchSandbox = null)
{
    private const int MaximumResultCharacters = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = MissumAiProtocol.CreateJsonOptions();
    private static readonly SearchValues<char> EvidenceIdCharacters = SearchValues.Create("0123456789abcdef");

    public Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal,
        Guid sessionId,
        Guid? assistantMessageId,
        string? codingWorkspacePath = null,
        Func<CodingCommandProgress, Task>? commandProgress = null,
        CodingRunEvidenceStore? evidenceStore = null,
        PromptTriggerAction? runAction = null,
        Uri? gatewayOverride = null,
        CancellationToken cancellationToken = default) => ExecuteForAgentAsync(proposal, sessionId, assistantMessageId,
            codingWorkspacePath, commandProgress, evidenceStore, runAction, null, gatewayOverride, cancellationToken);

    internal async Task<ClientToolResult> ExecuteForAgentAsync(
        ToolProposal proposal,
        Guid sessionId,
        Guid? assistantMessageId,
        string? codingWorkspacePath = null,
        Func<CodingCommandProgress, Task>? commandProgress = null,
        CodingRunEvidenceStore? evidenceStore = null,
        PromptTriggerAction? runAction = null,
        string? researchActorAgentId = null,
        Uri? gatewayOverride = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtensionToolDescriptor? extensionTool = null;
            if (extensionActions?.TryGetToolByModelName(proposal.Name, out var registeredTool) == true
                && registeredTool is not null
                && !registeredTool.ActionId.StartsWith("builtin.", StringComparison.Ordinal))
                extensionTool = registeredTool;
            ValidateProposal(proposal, extensionTool: extensionTool);
            if (extensionTool is not null)
            {
                if (extensionRuntime is null)
                    throw new InvalidOperationException("Der ExtensionHost ist für das vorgeschlagene Modellwerkzeug nicht verfügbar.");
                var workspaceRoot = await ResolveWorkspaceRootAsync(
                    sessionId, codingWorkspacePath, cancellationToken).ConfigureAwait(false);
                var extensionResult = await extensionRuntime.InvokeAsync(
                    extensionTool.ActionId,
                    proposal.Arguments,
                    workspaceRoot,
                    cancellationToken).ConfigureAwait(false);
                return Result(proposal, "completed", Bounded(extensionResult));
            }

            if (WorkspaceTools.IsLocal(proposal.Name))
            {
                var workspaceResult = await new WorkspaceToolService(connection).ExecuteAsync(
                    proposal,
                    codingWorkspacePath,
                    commandProgress,
                    gatewayOverride,
                    cancellationToken).ConfigureAwait(false);
                var workspaceJson = JsonSerializer.SerializeToElement(workspaceResult, JsonOptions);
                var failed = workspaceJson.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False;
                return Result(proposal, failed ? "failed" : "completed", workspaceResult,
                    failed ? "client.workspace_tool_failed" : null, failed ? "Workspace-Werkzeug fehlgeschlagen; Belege beachten." : null);
            }
            var coding = proposal.Name.StartsWith("coding.", StringComparison.Ordinal);
            var scientific = proposal.Name.StartsWith("math.", StringComparison.Ordinal);
            if (proposal.Name == ClientToolNames.MathFormalProof && researchSandbox is not null
                && await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } scienceSession)
            {
                var scienceResult = await ExecuteIsolatedLeanToolAsync(proposal, scienceSession, cancellationToken).ConfigureAwait(false);
                var scienceJson = JsonSerializer.SerializeToElement(scienceResult, JsonOptions);
                var failed = scienceJson.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False;
                return Result(proposal, failed ? "failed" : "completed", scienceResult,
                    failed ? "client.scientific_tool_failed" : null,
                    failed ? ScientificFailureMessage(scienceJson) : null);
            }
            if ((coding || scientific) && string.IsNullOrWhiteSpace(codingWorkspacePath))
            {
                throw new InvalidOperationException("Ausführbare Forschung benötigt einen ausgewählten Coding-Projektordner.");
            }

            if (coding || scientific)
            {
                if (evidenceStore is not null && (evidenceStore.SessionId != sessionId
                    || (evidenceStore.RootRunId != proposal.RunId && researchActorAgentId is null)))
                    throw new UnauthorizedAccessException("Der Werkzeugbelegspeicher gehört nicht zu dieser Sitzung und diesem Lauf.");
                if (scientific)
                {
                    using var scientificEvidence = evidenceStore?.BeginStep(proposal.ProposalId, proposal.Name, proposal.Arguments);
                    var scientificResult = await new ScientificResearchToolExecutor(codingWorkspacePath!, commandProgress, scientificEvidence)
                        .ExecuteAsync(proposal.Name, proposal.Arguments, cancellationToken).ConfigureAwait(false);
                    scientificEvidence?.SetResult(scientificResult);
                    if (scientificResult.TryGetProperty("success", out var scientificSuccess)
                        && scientificSuccess.ValueKind == JsonValueKind.False)
                        return Result(proposal, "failed", scientificResult, "client.scientific_tool_failed",
                            "Das wissenschaftliche Werkzeug meldete einen Fehler oder Restore-Konflikt.");
                    return Result(proposal, "completed", scientificResult);
                }
                if (proposal.Name == "coding.readOutput")
                {
                    var store = evidenceStore ?? throw new InvalidOperationException("Für diesen Lauf ist kein Werkzeugbelegspeicher verfügbar.");
                    var args = proposal.Arguments;
                    return Result(proposal, "completed", await store.ReadOutputAsync(args.GetProperty("evidenceId").GetString()!,
                        args.TryGetProperty("stream", out var stream) ? stream.GetString()! : "stdout",
                        args.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0,
                        args.TryGetProperty("maximumCharacters", out var characters) ? characters.GetInt32() : 16000, cancellationToken).ConfigureAwait(false));
                }
                if (proposal.Name == "coding.searchRunEvidence")
                {
                    var store = evidenceStore ?? throw new InvalidOperationException("Für diesen Lauf ist kein Werkzeugbelegspeicher verfügbar.");
                    return Result(proposal, "completed", await store.SearchRunEvidenceAsync(proposal.Arguments.GetProperty("query").GetString()!,
                        proposal.Arguments.TryGetProperty("maximumResults", out var maximum) ? maximum.GetInt32() : 8, cancellationToken).ConfigureAwait(false));
                }
                if (proposal.Name == "coding.searchHistory")
                    return Result(proposal, "completed", await CodingSessionTools.SearchHistoryAsync(chats, sessionId, assistantMessageId,
                        proposal.Arguments.GetProperty("query").GetString()!, CodingResultLimit(proposal.Arguments), cancellationToken).ConfigureAwait(false));
                if (proposal.Name == "coding.searchKnowledge")
                {
                    var query = proposal.Arguments.GetProperty("query").GetString()!;
                    var knowledge = await SearchDocumentsAsync(sessionId, JsonSerializer.SerializeToElement(new { query, maximumCharacters = 7_000 }),
                        cancellationToken).ConfigureAwait(false);
                    return Result(proposal, "completed", CodingSessionTools.BoundKnowledgeResult(
                        JsonSerializer.SerializeToElement(knowledge, JsonOptions), query, CodingResultLimit(proposal.Arguments)));
                }
                if (proposal.Name == "coding.renderHtml")
                    return Result(proposal, "completed", CodingSessionTools.RenderReceipt(proposal.Arguments));
                using var evidence = evidenceStore?.BeginStep(proposal.ProposalId, proposal.Name, proposal.Arguments);
                var codingExecution = await ExecuteCodingWithRepositoryFilesAsync(proposal, codingWorkspacePath!,
                    commandProgress, evidence, cancellationToken).ConfigureAwait(false);
                var codingResult = codingExecution.Result;
                if (evidence is not null)
                {
                    var enriched = JsonNode.Parse(codingResult.GetRawText())!.AsObject();
                    enriched["evidence"] = JsonSerializer.SerializeToNode(evidence.Reference, JsonOptions);
                    codingResult = JsonSerializer.SerializeToElement(enriched, JsonOptions);
                }
                if (codingExecution.FailureMessage is not null)
                    return Result(proposal, "failed", codingResult, "client.tool_failed", codingExecution.FailureMessage);
                if (codingResult.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                {
                    return Result(proposal, "failed", codingResult, "client.coding_tool_failed",
                        "Das Coding-Werkzeug meldete einen Fehler. Details stehen im Werkzeugergebnis.");
                }
                return Result(proposal, "completed", codingResult);
            }
            var payload = proposal.Name switch
            {
                ClientToolNames.DocumentRead => await documentTools.ReadAsync(
                    proposal.Arguments,
                    sessionId,
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentCreate => await documentTools.CreateAsync(
                    proposal.Arguments,
                    sessionId,
                    assistantMessageId ?? throw new InvalidOperationException(
                        "Die AI-Nachricht für das Dokumentartefakt fehlt."),
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsList => await ListDocumentsAsync(sessionId, cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsSearch => await SearchDocumentsAsync(
                    sessionId,
                    proposal.Arguments,
                    cancellationToken).ConfigureAwait(false),
                ClientToolNames.DocumentsReadPages => await ReadDocumentPagesAsync(
                    sessionId,
                    proposal.Arguments,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException(
                    $"Das Clientwerkzeug '{proposal.Name}' ist in Missum nicht verfügbar."),
            };
            return Result(proposal, "completed", Bounded(payload));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException exception) when (proposal.Name == ClientToolNames.CodingReadOutput)
        {
            return Result(proposal, "failed", new
            {
                evidenceId = proposal.Arguments.GetProperty("evidenceId").GetString(),
                available = false,
                retryable = false,
            }, "client.evidence_unavailable", exception.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Result(
                proposal,
                "failed",
                new { failed = true },
                "client.tool_failed",
                exception.Message);
        }
    }

    private static string ScientificFailureMessage(JsonElement result)
    {
        if (result.TryGetProperty("lean", out var lean) && lean.ValueKind == JsonValueKind.Object)
        {
            var details = lean.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String
                ? message.GetString() : null;
            if (string.IsNullOrWhiteSpace(details) && lean.TryGetProperty("diagnostics", out var diagnostics)
                && diagnostics.ValueKind == JsonValueKind.Array)
                details = string.Join("\n", diagnostics.EnumerateArray().Where(static line => line.ValueKind == JsonValueKind.String)
                    .Select(static line => line.GetString()).Where(static line => !string.IsNullOrWhiteSpace(line)).Take(6));
            if (!string.IsNullOrWhiteSpace(details)) return "Lean-Prüfung fehlgeschlagen: " + details[..Math.Min(details.Length, 1200)];
        }
        return "Das wissenschaftliche Werkzeug meldete eine fehlgeschlagene Ausführung oder Prüfung.";
    }

    private async Task<object> ExecuteIsolatedLeanToolAsync(ToolProposal proposal, ChatSession session, CancellationToken cancellationToken)
    {
        var sandbox = researchSandbox ?? throw new InvalidOperationException("Der isolierte Lean-Runner ist nicht verfügbar.");
        var projectId = "research-" + session.Id.ToString("N");
        ScientificResearchToolExecutor.ValidateArguments(proposal.Name, proposal.Arguments);
        var requestedProjectId = proposal.Arguments.GetProperty("projectId").GetString();
        if (!string.Equals(requestedProjectId, projectId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Das Forschungswerkzeug darf nur die Sandbox dieser Sitzung verwenden.");

        var runtime = await sandbox.PrepareRuntimeAsync(cancellationToken).ConfigureAwait(false);
        if (!runtime.IsReady) throw new InvalidOperationException(runtime.Detail ?? "Der Forschungsrunner ist derzeit nicht verfügbar.");
        var timeout = proposal.Arguments.TryGetProperty("timeoutSeconds", out var timeoutElement) && timeoutElement.TryGetInt32(out var timeoutValue)
            ? Math.Clamp(timeoutValue, 1, 7200) : 600;
        var stem = "generated/proof-" + Guid.NewGuid().ToString("N");
        var scriptPath = stem + ".py";
        var arguments = Array.Empty<string>();
        const string workingDirectory = ".";
        const int repetitions = 1;
        var leanArguments = proposal.Arguments;
        if (proposal.Arguments.TryGetProperty("path", out var requestedPath))
        {
            var workspaceRoot = await ResolveWorkspaceRootAsync(session.Id, session.CodingWorkspacePath, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Wähle einen vorhandenen Projektordner für Lean.");
            var sourcePath = WorkspaceFilePath.Resolve(workspaceRoot, requestedPath.GetString()!);
            // Historical receipts used work-relative paths; new requests use normal project-relative paths.
            if (!File.Exists(sourcePath)) sourcePath = WorkspaceFilePath.Resolve(Path.Combine(workspaceRoot, "work"), requestedPath.GetString()!);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Die Lean-Datei wurde nicht gefunden.", sourcePath);
            if (new FileInfo(sourcePath).Length > 262_144) throw new InvalidDataException("Die Lean-Datei ist zu groß.");
            var source = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
            LeanProofSourceContract.Validate(source);
            var copiedArguments = JsonNode.Parse(proposal.Arguments.GetRawText())!.AsObject();
            copiedArguments.Remove("path");
            copiedArguments["source"] = source;
            leanArguments = JsonSerializer.SerializeToElement(copiedArguments, JsonOptions);
        }
        var script = LeanProofHarness.CreateScript(leanArguments);
        await sandbox.WriteTextAsync(projectId, scriptPath, script, cancellationToken: cancellationToken).ConfigureAwait(false);
        var results = new List<Missum.Core.Research.ResearchSandboxRunResult>(repetitions);
        Missum.Core.Research.ResearchSandboxRunResult? leanPreparation = null;
        var preparationFailed = false;
        var executionBudget = System.Diagnostics.Stopwatch.StartNew();
        if (proposal.Name == ClientToolNames.MathFormalProof
            && (!proposal.Arguments.TryGetProperty("operation", out var leanOperation) || leanOperation.GetString() != "initialize"))
        {
            // Generated Lean sources must exist before the measured compiler run.
            // Keep the same provenance boundary used by Python experiments.
            leanPreparation = await sandbox.RunPythonAsync(projectId, scriptPath, ["--missum-prepare-only"], timeout,
                workingDirectory, cancellationToken).ConfigureAwait(false);
            preparationFailed = leanPreparation.ExitCode != 0 || leanPreparation.TimedOut;
            try
            {
                var prepared = JsonSerializer.Deserialize<JsonElement>(leanPreparation.StandardOutput);
                preparationFailed |= !prepared.TryGetProperty("success", out var preparedSuccess) || preparedSuccess.ValueKind != JsonValueKind.True
                    || !prepared.TryGetProperty("prepared", out var preparedFlag) || preparedFlag.ValueKind != JsonValueKind.True;
            }
            catch (JsonException) { preparationFailed = true; }
            if (preparationFailed) results.Add(leanPreparation);
        }
        for (var index = 0; index < repetitions && !preparationFailed; index++)
        {
            var remaining = Math.Max(1, timeout - (int)executionBudget.Elapsed.TotalSeconds);
            results.Add(await sandbox.RunPythonAsync(projectId, scriptPath, arguments, remaining, workingDirectory, cancellationToken).ConfigureAwait(false));
        }
        var succeeded = !preparationFailed && results.All(static run => run.ExitCode == 0 && !run.TimedOut);
        JsonElement? leanReceipt = null;
        var formalAccepted = false;
        if (proposal.Name == ClientToolNames.MathFormalProof)
        {
            try { leanReceipt = JsonSerializer.Deserialize<JsonElement>(results[0].StandardOutput); }
            catch (JsonException) { succeeded = false; }
            succeeded = succeeded && leanReceipt is { ValueKind: JsonValueKind.Object } lean
                && lean.TryGetProperty("success", out var leanSuccess) && leanSuccess.ValueKind == JsonValueKind.True;
            formalAccepted = succeeded && leanReceipt is { } checkedLean
                && checkedLean.TryGetProperty("formalVerification", out var verification) && verification.GetString() == "KernelAccepted"
                && checkedLean.TryGetProperty("axiomAudit", out var audit) && audit.GetString() == "passed"
                && checkedLean.TryGetProperty("proofs", out var proofs) && proofs.ValueKind == JsonValueKind.Array && proofs.GetArrayLength() > 0;
        }
        var experimentId = proposal.Arguments.TryGetProperty("experimentId", out var experiment) ? experiment.GetString()
            : "math-" + proposal.ProposalId;
        var attemptKey = experimentId + "\n" + proposal.ProposalId;
        var experimentRecordId = string.IsNullOrWhiteSpace(experimentId) ? null : "experiment-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\nexperiment\n" + attemptKey)))[..24];
        var verificationRecordId = "verification-" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(projectId + "\nverification\n" + proposal.ProposalId)))[..24];
        var receipt = new { success = succeeded, projectId, repetitions,
            experimentId, experimentRecordId, verificationRecordId,
            sourceFiles = results.Select(static run => run.ExecutedScriptPath).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(),
            inputHashes = results[0].InputHashes,
            processIsolation = "Docker cgroup and read-only container filesystem", networkIsolation = "Docker network none",
            resourceLimits = new { cpuCores = 16, memory = "48g", projectStorage = "100g", maximumStageSeconds = 7200 },
            verificationStatus = succeeded ? "ProcessSucceeded" : "ProcessFailed",
            formalVerification = proposal.Name == ClientToolNames.MathFormalProof ? (formalAccepted ? "KernelAccepted" : "NotEstablished") : null,
            lean = leanReceipt,
            preparation = leanPreparation is null ? null : new { leanPreparation.RunId, leanPreparation.ExitCode, leanPreparation.TimedOut,
                leanPreparation.StandardOutput, leanPreparation.StandardError, leanPreparation.SnapshotId,
                leanPreparation.InputHashes, leanPreparation.OutputHashes },
            runs = results.Select(static run => new { run.RunId, run.ExitCode, run.TimedOut, run.StandardOutput, run.StandardError,
                run.StartedAt, run.CompletedAt, run.Command, run.ExecutedScriptPath, run.ScriptSha256, run.SnapshotId, run.InputHashes, run.OutputHashes }) };
        if (JsonSerializer.SerializeToUtf8Bytes(receipt, JsonOptions).Length >= AssistantToolStep.MaximumStructuredJsonCharacters - 1024)
            throw new InvalidOperationException("Der vollständige Python-Ausführungsbeleg überschreitet das gespeicherte Toolergebnislimit; es wird kein gekürzter Beleg als Erfolg übernommen.");
        return receipt;
    }

    private static int CodingResultLimit(JsonElement arguments) => arguments.TryGetProperty("maximumResults", out var maximum) ? maximum.GetInt32() : 5;

    internal static void ValidateProposal(
        ToolProposal proposal,
        DateTimeOffset? currentTime = null,
        ExtensionToolDescriptor? extensionTool = null)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateIdentifier(proposal.ProposalId, "proposalId");
        ValidateIdentifier(proposal.RunId, "runId");
        if (string.IsNullOrWhiteSpace(proposal.Summary)
            || proposal.Summary.Length > 1_000
            || proposal.Summary.Any(character => char.IsControl(character)
                && character is not '\r' and not '\n' and not '\t'))
        {
            throw new InvalidDataException("Die Zusammenfassung des Client-Toolvorschlags ist ungültig.");
        }
        if (proposal.ExpiresAt <= (currentTime ?? DateTimeOffset.UtcNow))
        {
            throw new InvalidDataException("Der Client-Toolvorschlag ist abgelaufen.");
        }
        if (proposal.Arguments.ValueKind != JsonValueKind.Object
            || proposal.Arguments.GetRawText().Length > MaximumResultCharacters)
        {
            throw new InvalidDataException("Die Client-Toolargumente sind ungültig oder zu groß.");
        }

        var expectedRisk = proposal.Name switch
        {
            "coding.list" or "coding.search" or "coding.read" or "coding.gitDiff"
                or "coding.searchHistory" or "coding.searchKnowledge" or "coding.renderHtml"
                or "coding.readOutput" or "coding.searchRunEvidence" => ToolRiskClass.ReadOnly,
            "coding.write" or "coding.edit" or "coding.undo" => ToolRiskClass.LocalMutation,
            "coding.command" or WorkspaceTools.Open => ToolRiskClass.Process,
            ClientToolNames.MathSymbolic or ClientToolNames.MathNumeric or ClientToolNames.MathSmt
                or ClientToolNames.MathFormalProof => ToolRiskClass.Process,
            WorkspaceTools.ImageInput => ToolRiskClass.ReadOnly,
            ClientToolNames.DocumentRead or ClientToolNames.DocumentsList
                or ClientToolNames.DocumentsSearch or ClientToolNames.DocumentsReadPages => ToolRiskClass.ReadOnly,
            ClientToolNames.DocumentCreate => ToolRiskClass.LocalMutation,
            _ when extensionTool is not null => extensionTool.RiskClass switch
            {
                ExtensionToolRiskClass.ReadOnly => ToolRiskClass.ReadOnly,
                ExtensionToolRiskClass.LocalMutation => ToolRiskClass.LocalMutation,
                ExtensionToolRiskClass.Process => ToolRiskClass.Process,
                _ => throw new InvalidDataException("Die Risikoklasse des Erweiterungswerkzeugs ist ungültig."),
            },
            _ => throw new InvalidDataException($"Das Clientwerkzeug '{proposal.Name}' ist nicht freigegeben."),
        };
        if (proposal.RiskClass != expectedRisk)
        {
            throw new InvalidDataException(
                "Die Risikoklasse des Client-Toolvorschlags stimmt nicht mit dem lokalen Vertrag überein.");
        }

        var arguments = proposal.Arguments;
        if (extensionTool is not null)
        {
            ValidateExtensionArguments(extensionTool, arguments);
            return;
        }
        if (WorkspaceTools.IsLocal(proposal.Name)) { WorkspaceTools.Validate(proposal.Name, arguments); return; }
        if (proposal.Name.StartsWith("math.", StringComparison.Ordinal))
        {
            ScientificResearchToolExecutor.ValidateArguments(proposal.Name, arguments);
            return;
        }
        switch (proposal.Name)
        {
            case "coding.readOutput":
                ValidateProperties(arguments, ["evidenceId"], ["evidenceId", "stream", "offset", "maximumCharacters"]);
                var evidenceId = ValidateString(arguments, "evidenceId", 35, 35);
                if (!evidenceId.StartsWith("ev-", StringComparison.Ordinal) || evidenceId.AsSpan(3).ContainsAnyExcept(EvidenceIdCharacters))
                    throw new InvalidDataException("Die Belegkennung ist ungültig.");
                if (arguments.TryGetProperty("stream", out _) && ValidateString(arguments, "stream", 1, 6) is not ("stdout" or "stderr" or "input" or "result"))
                    throw new InvalidDataException("Der Belegkanal ist ungültig.");
                ValidateOptionalInteger(arguments, "offset", 0, int.MaxValue);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1, 32000);
                break;
            case "coding.searchRunEvidence":
                ValidateProperties(arguments, ["query"], ["query", "maximumResults"]);
                ValidateString(arguments, "query", 1, 512);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 20);
                break;
            case "coding.searchHistory":
            case "coding.searchKnowledge":
                ValidateProperties(arguments, ["query"], ["query", "maximumResults"]);
                ValidateString(arguments, "query", 1, 512);
                ValidateOptionalInteger(arguments, "maximumResults", 1, 8);
                break;
            case "coding.renderHtml":
                ValidateProperties(arguments, ["code"], ["code", "title"]);
                ValidateString(arguments, "code", 1, 16_000);
                ValidateOptionalString(arguments, "title", 1, 100);
                break;
            case ClientToolNames.DocumentRead:
                ValidateProperties(
                    arguments,
                    ["scope", "mode"],
                    ["scope", "mode", "reference", "query", "startUnit", "characterOffset", "maximumUnits", "maximumCharacters"]);
                if (ValidateString(arguments, "scope", 1, 16) != "session")
                {
                    throw new InvalidDataException("document.read ist nur für Sitzungsdokumente freigegeben.");
                }
                var readMode = ValidateString(arguments, "mode", 1, 16);
                if (readMode is not ("list" or "outline" or "read" or "search"))
                {
                    throw new InvalidDataException("Die document.read-Auswahl ist ungültig.");
                }
                ValidateOptionalString(arguments, "reference", 1, 1_024);
                ValidateOptionalString(arguments, "query", 1, 2_000);
                ValidateOptionalInteger(arguments, "startUnit", 1, 1_000_000);
                ValidateOptionalInteger(arguments, "characterOffset", 0, MaximumResultCharacters);
                ValidateOptionalInteger(arguments, "maximumUnits", 1, 30);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 40_000);
                if (readMode != "list" && !arguments.TryGetProperty("reference", out _))
                {
                    throw new InvalidDataException("document.read benötigt außerhalb des list-Modus eine reference.");
                }
                if (readMode == "search" && !arguments.TryGetProperty("query", out _))
                {
                    throw new InvalidDataException("document.read search benötigt query.");
                }
                break;
            case ClientToolNames.DocumentCreate:
                ValidateProperties(
                    arguments,
                    ["operation", "reference", "format", "sectionId", "content"],
                    ["operation", "reference", "format", "sectionId", "heading", "content"]);
                var operation = ValidateString(arguments, "operation", 1, 32);
                if (operation is not ("create" or "appendSection" or "replaceSection"))
                {
                    throw new InvalidDataException("Die document.create-Operation ist ungültig.");
                }
                ValidateString(arguments, "reference", 1, 1_024);
                var format = ValidateString(arguments, "format", 1, 16);
                if (format is not ("markdown" or "text" or "docx" or "pdf" or "xlsx" or "pptx"))
                {
                    throw new InvalidDataException("Das document.create-Format ist ungültig.");
                }
                ValidateString(arguments, "sectionId", 1, 128);
                ValidateOptionalString(arguments, "heading", 1, 500);
                ValidateString(arguments, "content", 0, 120_000);
                break;
            case ClientToolNames.DocumentsList:
                ValidateProperties(arguments, [], []);
                break;
            case ClientToolNames.DocumentsSearch:
                ValidateProperties(arguments, ["query"], ["query", "maximumCharacters"]);
                ValidateString(arguments, "query", 1, 20_000);
                ValidateOptionalInteger(arguments, "maximumCharacters", 1_000, 200_000);
                break;
            case ClientToolNames.DocumentsReadPages:
                ValidateProperties(
                    arguments,
                    ["documentId", "startPage", "endPage"],
                    ["documentId", "startPage", "endPage"]);
                _ = Guid.Parse(ValidateString(arguments, "documentId", 36, 36));
                ValidateInteger(arguments, "startPage", 1, 1_000_000);
                ValidateInteger(arguments, "endPage", 1, 1_000_000);
                break;
        }
    }

    private async Task<string?> ResolveWorkspaceRootAsync(
        Guid sessionId,
        string? suppliedWorkspace,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(suppliedWorkspace) && Directory.Exists(suppliedWorkspace))
            return Path.GetFullPath(suppliedWorkspace);
        var session = await chats.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Die Sitzung des Erweiterungswerkzeugs wurde nicht gefunden.");
        if (!string.IsNullOrWhiteSpace(session.CodingWorkspacePath) && Directory.Exists(session.CodingWorkspacePath))
            return Path.GetFullPath(session.CodingWorkspacePath);
        if (session.SessionGroupId is not { } groupId) return null;
        var group = (await chats.ListSessionGroupsAsync(session.ChatMode, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == groupId);
        return !string.IsNullOrWhiteSpace(group?.WorkspacePath) && Directory.Exists(group.WorkspacePath)
            ? Path.GetFullPath(group.WorkspacePath)
            : null;
    }

    private static void ValidateExtensionArguments(ExtensionToolDescriptor tool, JsonElement arguments)
    {
        var schema = tool.InputSchema;
        var allowed = schema.GetProperty("properties").EnumerateObject()
            .Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        var required = schema.GetProperty("required").EnumerateArray()
            .Select(static item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        ValidateProperties(arguments, required, allowed);
    }

    private async Task<object> ListDocumentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var items = await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return new
        {
            documents = items.Where(static item => item.PreparationStatus == DocumentPreparationStatus.Ready)
                .Select(static item => new { documentId = item.Id, item.FileName, item.PageCount, item.Sha256 }),
        };
    }

    private async Task<object> SearchDocumentsAsync(
        Guid sessionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var query = arguments.GetProperty("query").GetString()!;
        var maximum = arguments.TryGetProperty("maximumCharacters", out var value) ? value.GetInt32() : 120_000;
        IReadOnlyList<DocumentContextHit> hits;
        var searchMode = "fulltext";
        try
        {
            using var client = await connection.CreateClientAsync(cancellationToken).ConfigureAwait(false);
            var modelStatus = await client.GetModelStatusAsync(cancellationToken).ConfigureAwait(false);
            var embeddingModel = modelStatus.Models.FirstOrDefault(static item => item.Downloaded && item.Role == "embedding");
            var indexed = embeddingModel is null
                ? []
                : await documents.ListIndexChunksAsync(sessionId, embeddingModel.Id, cancellationToken).ConfigureAwait(false);
            if (embeddingModel is not null
                && indexed.Count > 0
                && indexed.All(static item => item.Embedding is not null))
            {
                var response = await client.CreateEmbeddingsAsync(
                    new EmbeddingBatchRequest([new EmbeddingInput("query", query)]),
                    cancellationToken).ConfigureAwait(false);
                var vector = response.Vectors.Single(static item => item.Id == "query").Values;
                hits = await documents.SearchHybridAsync(
                    sessionId,
                    query,
                    embeddingModel.Id,
                    vector,
                    maximum,
                    cancellationToken).ConfigureAwait(false);
                searchMode = "hybrid";
            }
            else
            {
                hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            hits = await documents.SearchAsync(sessionId, query, maximum, cancellationToken).ConfigureAwait(false);
            searchMode = $"fulltext (semantisch nicht verfügbar: {exception.GetType().Name})";
        }
        return new
        {
            searchMode,
            evidence = hits.Select(static hit => new
            {
                hit.DocumentId,
                hit.FileName,
                hit.PageNumber,
                hit.Score,
                hit.Text,
                citation = $"[{hit.FileName}, S. {hit.PageNumber}]",
            }),
        };
    }

    private async Task<object> ReadDocumentPagesAsync(
        Guid sessionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var documentId = Guid.Parse(arguments.GetProperty("documentId").GetString()!);
        var start = arguments.GetProperty("startPage").GetInt32();
        var end = arguments.GetProperty("endPage").GetInt32();
        if (end < start || end - start > 100)
        {
            throw new InvalidDataException("Der angeforderte Seitenbereich ist ungültig oder zu groß.");
        }
        var document = (await documents.ListAsync(sessionId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Id == documentId && item.PreparationStatus == DocumentPreparationStatus.Ready)
            ?? throw new FileNotFoundException("Das Dokument ist in dieser Sitzung nicht fertig aufbereitet.");
        var pages = (await documents.ReadPagesAsync(documentId, cancellationToken).ConfigureAwait(false))
            .Where(page => page.PageNumber >= start && page.PageNumber <= end)
            .Select(page => new
            {
                documentId,
                document.FileName,
                page.PageNumber,
                page.Text,
                citation = $"[{document.FileName}, S. {page.PageNumber}]",
            })
            .ToArray();
        return new { pages };
    }

    private static void ValidateIdentifier(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 256
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.' or ':')))
        {
            throw new InvalidDataException($"'{name}' ist ungültig.");
        }
    }

    private static void ValidateProperties(
        JsonElement arguments,
        IReadOnlyCollection<string> required,
        IReadOnlyCollection<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{property.Name}' ist nicht freigegeben.");
            }
        }
        foreach (var property in required)
        {
            if (!seen.Contains(property))
            {
                throw new InvalidDataException($"Das Werkzeugargument '{property}' fehlt.");
            }
        }
    }

    private static string ValidateString(
        JsonElement arguments,
        string name,
        int minimumLength,
        int maximumLength)
    {
        if (!arguments.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt oder ist kein Text.");
        }
        var value = NormalizeUnicodeScalarText(property.GetString() ?? string.Empty);
        if (value.Length < minimumLength || value.Length > maximumLength)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' hat eine ungültige Länge.");
        }
        return value;
    }

    private static void ValidateOptionalString(
        JsonElement arguments,
        string name,
        int minimumLength,
        int maximumLength)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            _ = ValidateString(arguments, name, minimumLength, maximumLength);
        }
    }

    private static int ValidateInteger(JsonElement arguments, string name, int minimum, int maximum)
    {
        if (!arguments.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < minimum
            || value > maximum)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist keine gültige Ganzzahl.");
        }
        return value;
    }

    private static void ValidateOptionalInteger(
        JsonElement arguments,
        string name,
        int minimum,
        int maximum)
    {
        if (arguments.TryGetProperty(name, out _))
        {
            _ = ValidateInteger(arguments, name, minimum, maximum);
        }
    }

    private static string RequiredString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' fehlt.");
        }
        var result = NormalizeUnicodeScalarText(property.GetString() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException($"Das Werkzeugargument '{name}' ist leer.");
        }
        return result;
    }

    internal static string NormalizeUnicodeScalarText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current)
                && index + 1 < value.Length
                && char.IsLowSurrogate(value[index + 1]))
            {
                if (builder is not null)
                {
                    builder.Append(current);
                    builder.Append(value[++index]);
                }
                else
                {
                    index++;
                }
                continue;
            }
            if (!char.IsSurrogate(current))
            {
                builder?.Append(current);
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
        }
        return builder?.ToString() ?? value;
    }

    private static object Bounded(object value)
    {
        if (JsonSerializer.Serialize(value, JsonOptions).Length > MaximumResultCharacters)
        {
            throw new InvalidOperationException("Das lokale Toolergebnis überschreitet das Größenlimit.");
        }
        return value;
    }

    private static ClientToolResult Result(
        ToolProposal proposal,
        string status,
        object value,
        string? errorCode = null,
        string? message = null) =>
        new(
            proposal.ProposalId,
            status,
            JsonSerializer.SerializeToElement(value, JsonOptions),
            errorCode,
            message);
}
