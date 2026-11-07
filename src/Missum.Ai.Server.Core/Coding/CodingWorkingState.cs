using Missum.Ai.Server.Core.Models;
using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Missum.Ai.Server.Core.Coding;

public sealed record CodingPlanItem(string Id, string Title, string Status);
public sealed record CodingStateFact(string Text);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
public sealed record CodingToolEvidence(string Id, string Tool, bool Success, string? Path,
    int? ExitCode, string Summary, long Sequence, string? OutputEvidenceId = null,
    string? SourceRunId = null, bool Historical = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool FormalVerified = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? VerificationPath = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CommandTarget = null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip)]
public sealed record CodingActiveFile(string Path, int StartLine, string Snippet,
    bool Truncated, bool NeedsRead, string EvidenceId, long Sequence, string? OutputEvidenceId = null);
public sealed record CodingFailureSignature(string Signature, string Tool, string ArgumentHash, string Error,
    int Count, long EnvironmentRevision, string EvidenceId);

/// <summary>Bounded, checkpoint-serializable working data; it does not replace the original run journal.</summary>
public sealed record CodingWorkingState(
    string OriginalTask,
    IReadOnlyList<CodingPlanItem> Plan,
    IReadOnlyList<CodingPlanItem> AcceptanceCriteria,
    IReadOnlyList<CodingStateFact> Facts,
    IReadOnlyList<CodingStateFact> RejectedHypotheses,
    IReadOnlyList<CodingToolEvidence> Evidence,
    IReadOnlyList<CodingToolEvidence> Tests,
    IReadOnlyList<CodingActiveFile> ActiveFiles,
    IReadOnlyList<CodingFailureSignature> Failures,
    string? NextStep,
    string Phase,
    long EnvironmentRevision,
    long Sequence,
    string? EnvironmentFingerprint = null,
    int ConsecutivePlanNoOps = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ScienceState? Science = null)
{
    public static CodingWorkingState Create(string originalTask)
    {
        ArgumentNullException.ThrowIfNull(originalTask);
        return new(originalTask, [], [], [], [], [], [], [], [], null, "planning", 0, 0);
    }
}

public static class CodingWorkingStateReducer
{
    public const int MaximumEvidence = 224;
    public const int MaximumActiveFiles = 12;
    public const int MaximumFailures = 64;
    private static readonly SearchValues<char> EvidenceIdCharacters = SearchValues.Create("0123456789abcdef");

    public static CodingWorkingState ObserveToolResult(CodingWorkingState state, LmToolCall call, string resultJson)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(resultJson);
        // Replayed receipts must not increment failure counters or invalidate newer file evidence.
        if (state.Evidence.Any(item => item.Id == call.Id)) return state;
        using var document = Parse(resultJson);
        var outer = document?.RootElement ?? default;
        var result = Unwrap(outer);
        var success = IsSuccessfulResult(outer);
        var failed = IsFailedResult(outer);
        var path = Text(result, "path") ?? Text(call.Arguments, "path");
        if (path is not null) path = Bound(path.Replace('\\', '/'), 1024);
        var exit = Integer(result, "exitCode");
        var outputEvidenceId = Text(result, "evidenceId") ?? Text(outer, "evidenceId");
        if (outputEvidenceId is null && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("evidence", out var outputReference))
            outputEvidenceId = Text(outputReference, "evidenceId");
        if (outputEvidenceId is not null && (outputEvidenceId.Length != 35 || !outputEvidenceId.StartsWith("ev-", StringComparison.Ordinal)
            || outputEvidenceId.AsSpan(3).ContainsAnyExcept(EvidenceIdCharacters))) outputEvidenceId = null;
        var sequence = state.Sequence + 1;
        var summary = Bound(Text(result, "stderr") ?? Text(result, "content") ?? Text(result, "stdout")
            ?? Text(outer, "message") ?? Text(result, "message") ?? resultJson, 1600);
        // Prefer actual combined process output; an empty stderr is not a useful success receipt.
        if (exit is not null) summary = Bound("exitCode=" + exit + "\nstdout:\n" + Text(result, "stdout") + "\nstderr:\n" + Text(result, "stderr"), 1600);
        // Output provenance must survive bounded summaries and checkpoints: the text
        // may be much longer than the working-state excerpt and is still historical.
        var sourceRunId = call.Name == "coding.readOutput" ? Text(result, "sourceRunId") : null;
        var historical = call.Name == "coding.readOutput" && Boolean(result, "historical");
        var evidence = new CodingToolEvidence(call.Id, call.Name, success, path, exit, summary, sequence, outputEvidenceId,
            sourceRunId, historical, CommandTarget: call.Name == "coding.command" ? ReadCommandTarget(call.Arguments) : null);
        if (state.Science is not null)
        {
            evidence = ScienceStagePolicy.BindReceipt(state, evidence, outer);
            if (success && !evidence.Success) { success = false; failed = true; }
        }
        var files = state.ActiveFiles.ToList();
        var environment = state.EnvironmentFingerprint;
        var environmentChanged = false;
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("environment", out var observedEnvironment))
        {
            var fingerprint = Hash(observedEnvironment.GetRawText());
            environmentChanged = environment is not null && environment != fingerprint;
            environment = fingerprint;
        }
        var previousFile = path is null ? null : files.FirstOrDefault(item => SamePath(item.Path, path));
        var observedContent = Text(result, "content");
        var contentChanged = call.Name == "coding.read" && observedContent is not null && previousFile is { NeedsRead: false }
            && previousFile.StartLine == (Integer(result, "startLine") ?? Integer(call.Arguments, "startLine") ?? 1)
            && previousFile.Snippet != observedContent[..Math.Min(observedContent.Length, 2400)];
        var mutation = success && call.Name is "coding.write" or "coding.edit";
        var processRan = call.Name == "coding.command" && exit is not null;
        // Failed/aborted processes can already have changed the project even
        // when no exit code survived. Only explicit non-execution receipts are safe.
        var commandNotExecuted = Text(outer, "status") is "rejected" or "not_executed"
            || (Text(outer, "errorCode") ?? Text(result, "errorCode")) is
                "agent.invalid_tool_call" or "agent.tool_budget";
        var processMayHaveRun = call.Name == "coding.command" && (processRan
            || Boolean(result, "outcomeUnknown") || Boolean(outer, "outcomeUnknown") || failed && !commandNotExecuted);
        // A successful probe/test is not proof that unrelated failure conditions
        // changed. Its matching signature is removed below; only observed project
        // or environment changes invalidate the broader failure epoch.
        var invalidateFailures = environmentChanged || contentChanged || mutation;
        var epoch = state.EnvironmentRevision + (invalidateFailures ? 1 : 0);
        var failures = invalidateFailures ? new List<CodingFailureSignature>() : state.Failures.ToList();
        if (success)
        {
            var argumentHash = ArgumentHash(call.Arguments);
            failures.RemoveAll(item => item.Tool == call.Name && item.ArgumentHash == argumentHash);
        }
        if (processMayHaveRun)
            files = files.Select(static file => file with { Snippet = string.Empty, NeedsRead = true }).ToList();
        if (path is not null && (success && call.Name == "coding.read" || mutation))
        {
            files.RemoveAll(item => SamePath(item.Path, path));
            var content = mutation ? string.Empty : Text(result, "content") ?? string.Empty;
            files.Add(new(path, Integer(result, "startLine") ?? Integer(call.Arguments, "startLine") ?? 1,
                content[..Math.Min(content.Length, 2400)], content.Length > 2400 || Boolean(result, "truncated"), mutation, call.Id, sequence, outputEvidenceId));
        }
        if (failed)
        {
            var argumentHash = ArgumentHash(call.Arguments);
            var error = Bound(Text(outer, "errorCode") ?? Text(result, "errorCode") ?? Text(result, "stderr")
                ?? Text(outer, "message") ?? Text(result, "message") ?? "tool.failed", 500);
            var signature = Hash(call.Name + "\n" + argumentHash + "\n" + error);
            var previous = failures.FirstOrDefault(item => item.Signature == signature && item.EnvironmentRevision == epoch);
            failures.RemoveAll(item => item.Signature == signature);
            failures.Add(new(signature, call.Name, argumentHash, error, Math.Min(int.MaxValue - 1, previous?.Count ?? 0) + 1, epoch, call.Id));
        }
        var observed = state with
        {
            Evidence = KeepEvidence(state.Evidence.Append(evidence)),
            Tests = processRan ? state.Tests.Append(evidence).TakeLast(16).ToArray() : state.Tests,
            ActiveFiles = files.TakeLast(MaximumActiveFiles).ToArray(),
            Failures = failures.TakeLast(MaximumFailures).ToArray(),
            EnvironmentRevision = epoch, EnvironmentFingerprint = environment, Sequence = sequence,
            ConsecutivePlanNoOps = call.Name == CodingWorkingStateTools.PlanTool ? state.ConsecutivePlanNoOps : 0,
            Phase = failed ? "error_recovery" : success ? mutation ? "editing" : state.Phase == "error_recovery" ? "exploration" : state.Phase : state.Phase,
        };
        return observed;
    }

    public static CodingWorkingState ApplyPlanUpdate(CodingWorkingState state, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("updatePlan benötigt ein Argumentobjekt.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "steps", "acceptanceCriteria", "facts", "rejectedHypotheses", "nextStep", "phase", "explanation", "scienceStages" };
        if (arguments.EnumerateObject().Any(property => !allowed.Contains(property.Name))) throw new ArgumentException("Unbekanntes updatePlan-Feld.");
        CodingWorkingStateTools.Validate(arguments);
        ValidateNewPlanItems(state, arguments);
        var plan = arguments.TryGetProperty("steps", out var steps) ? ReadPlan(steps, state.Plan, "steps") : state.Plan;
        var criteria = arguments.TryGetProperty("acceptanceCriteria", out var acceptance) ? ReadPlan(acceptance, state.AcceptanceCriteria, "acceptanceCriteria") : state.AcceptanceCriteria;
        if (state.AcceptanceCriteria.Any(previous => !criteria.Any(item => item.Id == previous.Id && item.Title == previous.Title)))
            throw new ArgumentException("Vorhandene Akzeptanzkriterien dürfen nicht entfernt oder umgedeutet werden; nur der Status darf sich ändern.");
        var facts = arguments.TryGetProperty("facts", out var suppliedFacts) ? ReadFacts(suppliedFacts, state.Facts) : state.Facts;
        var rejected = arguments.TryGetProperty("rejectedHypotheses", out var suppliedRejected) ? ReadFacts(suppliedRejected, state.RejectedHypotheses) : state.RejectedHypotheses;
        var phase = Text(arguments, "phase") ?? state.Phase;
        if (phase is not ("planning" or "exploration" or "editing" or "review" or "final" or "error_recovery" or "unknown"))
            throw new ArgumentException("Unbekannte Arbeitsphase.");
        var next = arguments.TryGetProperty("nextStep", out _) ? RequireText(arguments, "nextStep", 1000) : state.NextStep;
        if (arguments.TryGetProperty("explanation", out _)) _ = RequireText(arguments, "explanation", 2000);
        var updated = state with { Plan = plan, AcceptanceCriteria = criteria, Facts = facts, RejectedHypotheses = rejected,
            NextStep = next, Phase = phase, Sequence = state.Sequence + 1, ConsecutivePlanNoOps = 0 };
        updated = ScienceStagePolicy.Apply(state, updated, arguments);
        return CodingPlanProgressGuard.HasChanged(state, updated) ? updated
            : state with { ConsecutivePlanNoOps = Math.Clamp(state.ConsecutivePlanNoOps, 0, CodingPlanProgressGuard.MaximumNoOps - 1) + 1 };
    }

    private static void ValidateNewPlanItems(CodingWorkingState state, JsonElement arguments)
    {
        var missing = new List<string>();
        Check("steps", state.Plan);
        Check("acceptanceCriteria", state.AcceptanceCriteria);
        if (missing.Count == 0) return;
        throw new ArgumentException("Fehlende Pflichtfelder für neue Planeinträge: " + string.Join(", ", missing) + ". "
            + "Diese IDs sind in der jeweiligen Liste dieses Laufs noch nicht gespeichert. "
            + "title benötigt 1–400 Zeichen. Ein ausgelassener status beginnt bei neuen Einträgen sicher mit pending; "
            + "explizit erlaubt sind pending, in_progress oder completed. "
            + "steps und acceptanceCriteria können gemeinsam in einem Aufruf angelegt oder geändert werden. "
            + "Ergänze die genannten Felder und sende den korrigierten gemeinsamen Aufruf erneut; Aufteilen ist nicht erforderlich. "
            + $"Gespeicherte IDs in steps: {JsonSerializer.Serialize(state.Plan.Select(static entry => entry.Id))}; "
            + $"acceptanceCriteria: {JsonSerializer.Serialize(state.AcceptanceCriteria.Select(static entry => entry.Id))}. "
            + "IDs aus früheren Läufen oder der anderen Liste gelten hier nicht als vorhanden. "
            + "Das gesamte Update wurde abgelehnt; kein Feld wurde geändert.");

        void Check(string section, IReadOnlyList<CodingPlanItem> existing)
        {
            if (!arguments.TryGetProperty(section, out var items)) return;
            var index = 0;
            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                if (!existing.Any(previous => previous.Id == id))
                {
                    if (!item.TryGetProperty("title", out _)) missing.Add($"{section}[{index}].title (ID {JsonSerializer.Serialize(id)})");
                }
                index++;
            }
        }
    }

    private static List<CodingPlanItem> ReadPlan(JsonElement value, IReadOnlyList<CodingPlanItem> existing, string section)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 16) throw new ArgumentException("Ein Plan benötigt 1 bis 16 Schritte.");
        var items = existing.ToList();
        var updatedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            var id = RequireText(item, "id", 80);
            var previousIndex = items.FindIndex(previous => previous.Id == id);
            var previous = previousIndex >= 0 ? items[previousIndex] : null;
            var title = item.TryGetProperty("title", out _) || previous is null ? RequireText(item, "title", 400) : previous.Title;
            var status = item.TryGetProperty("status", out _) ? RequireText(item, "status", 20) : previous?.Status ?? "pending";
            if (status is not ("pending" or "in_progress" or "completed") || !updatedIds.Add(id))
                throw new ArgumentException("Planschritte benötigen eindeutige IDs und einen gültigen Status.");
            var updated = new CodingPlanItem(id, title, status);
            if (previousIndex >= 0) items[previousIndex] = updated;
            else items.Add(updated);
        }
        if (items.Count > 16) throw new ArgumentException("Ein gespeicherter Plan darf insgesamt höchstens 16 Schritte enthalten.");
        if (items.Count(static item => item.Status == "in_progress") > 1) throw new ArgumentException($"{section}: Im zusammengeführten Arbeitsstand darf höchstens ein Eintrag in_progress sein. Schließe den bisherigen Schritt im selben Update ab oder setze ihn auf pending.");
        return items;
    }

    private static CodingStateFact[] ReadFacts(JsonElement values, IReadOnlyList<CodingStateFact> existing)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 12) throw new ArgumentException("Höchstens 12 Erkenntnisse sind erlaubt.");
        var facts = existing.ToList();
        foreach (var item in values.EnumerateArray())
        {
            var updated = new CodingStateFact(RequireText(item, "text", 600));
            facts.RemoveAll(previous => previous.Text == updated.Text);
            facts.Add(updated);
        }
        // Only the compact working window rolls; all submitted facts remain in the run journal.
        return facts.TakeLast(12).ToArray();
    }

    internal static CodingToolEvidence[] KeepEvidence(IEnumerable<CodingToolEvidence> evidence) =>
        evidence.TakeLast(MaximumEvidence).OrderBy(static item => item.Sequence).ToArray();

    public static bool IsSuccessfulResult(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || IsFailedResult(result)) return false;
        var status = Text(result, "status");
        if (status is "completed" or "success") return true;
        if (result.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object && IsSuccessfulResult(nested)) return true;
        return status is "completed" or "success" || Boolean(result, "success") || Integer(result, "exitCode") == 0
            || result.TryGetProperty("content", out _) || result.TryGetProperty("entries", out _) || result.TryGetProperty("matches", out _)
            || result.TryGetProperty("results", out _);
    }

    internal static bool IsFailedResult(JsonElement result) => result.ValueKind == JsonValueKind.Object
        && (Text(result, "status") is "failed" or "rejected" or "cancelled"
            || result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False
            || Integer(result, "exitCode") is { } exit && exit != 0
            || result.TryGetProperty("result", out var nested) && IsFailedResult(nested));

    private static string? ReadCommandTarget(JsonElement arguments)
    {
        if (Text(arguments, "executable") is not { } executable) return null;
        if (arguments.TryGetProperty("arguments", out var argv) && argv.ValueKind == JsonValueKind.Array)
        {
            foreach (var argument in argv.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String || argument.GetString() is not { Length: <= 240 } value
                    || value.StartsWith('-')) continue;
                var extension = Path.GetExtension(value);
                if (extension.Equals(".py", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                    return Bound(executable + " " + value, 240);
            }
        }
        return Bound(executable, 240);
    }

    internal static JsonElement Unwrap(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("result", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : value;
    internal static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string RequireText(JsonElement value, string name, int maximum)
    {
        var text = Text(value, name);
        return text is not null && !string.IsNullOrWhiteSpace(text) && text.Length <= maximum ? text
            : throw new ArgumentException($"{name} muss ein nichtleerer Text mit höchstens {maximum} Zeichen sein.");
    }
    internal static int? Integer(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var result) ? result : null;
    private static bool Boolean(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var boolean) && boolean.ValueKind == JsonValueKind.True;
    internal static string Bound(string value, int maximum) => value.Length <= maximum ? value : value[..(maximum - 36)] + "\n[gekürzt; Beleg im Laufjournal]";
    internal static bool SamePath(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static JsonDocument? Parse(string value) { try { return JsonDocument.Parse(value); } catch (JsonException) { return null; } }
    internal static string ArgumentHash(JsonElement args)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, args);
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject().OrderBy(static item => item.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value); }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.Undefined) writer.WriteNullValue();
        else value.WriteTo(writer);
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
