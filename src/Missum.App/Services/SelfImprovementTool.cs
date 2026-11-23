using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Missum.Ai.Contracts;
using Missum.Core.Memory;

namespace Missum.App.Services;

internal static class SelfImprovementTool
{
    private static readonly SemaphoreSlim Submission = new(1, 1);
    private static readonly JsonSerializerOptions Json = MissumAiProtocol.CreateJsonOptions();

    internal static string Repository => Environment.GetEnvironmentVariable("ASSISTANT_SELF_IMPROVEMENT_REPOSITORY")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "GitHub", "Missum");

    // Tests substitute Docker image inspection with recorded digests.
    internal static Func<string, CancellationToken, Task<string?>>? ImageDigestOverride { get; set; }

    internal static bool Allows(string? workspace)
    {
        var repository = Repository;
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(workspace) || !Path.IsPathFullyQualified(repository)
            || !File.Exists(Path.Combine(repository, "windows", "build.ps1"))
            || !File.Exists(Path.Combine(workspace, "windows", "build-ai-stack.ps1"))) return false;

        var configured = ProjectMemoryScope.FromWorkspace(repository).ProjectKey;
        return configured.StartsWith("workspace:git:", StringComparison.OrdinalIgnoreCase)
            && configured == ProjectMemoryScope.FromWorkspace(workspace).ProjectKey;
    }

    internal static async Task<ClientToolResult> ExecuteAsync(
        ToolProposal proposal, string workspace, Guid sessionId, CancellationToken cancellationToken,
        SelfImprovementApply.ApplyContext? applyContext = null)
    {
        var state = SelfImprovementApply.StateRoot;
        var candidateId = proposal.Name == "selfimprovement.build"
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString("D") + ":" + proposal.ProposalId)))[..32]
            : proposal.Arguments.TryGetProperty("candidateId", out var candidate) && candidate.ValueKind == JsonValueKind.String
                ? candidate.GetString() : null;
        if (candidateId is not { Length: 32 } || !candidateId.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Gib eine gültige Kandidatenkennung an.");
        candidateId = candidateId.ToUpperInvariant();
        var directory = Path.Combine(state, candidateId);
        var requestPath = Path.Combine(directory, "request.json");
        var reportPath = Path.Combine(directory, "report.json");

        if (proposal.Name == "selfimprovement.build")
        {
            await Submission.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(requestPath))
                {
                    var request = JsonSerializer.Serialize(new { candidateId, workspace, repository = Repository, sessionId }, Json);
                    var pending = requestPath + "." + Guid.NewGuid().ToString("N") + ".pending";
                    await File.WriteAllTextAsync(pending, request, cancellationToken).ConfigureAwait(false);
                    var submitted = false;
                    try
                    {
                        File.Move(pending, requestPath);
                        submitted = true;
                    }
                    catch (IOException) when (File.Exists(requestPath)) { File.Delete(pending); }
                    if (submitted)
                    {
                        try { using var worker = StartWorker(requestPath); }
                        catch
                        {
                            File.Delete(requestPath);
                            throw;
                        }
                    }
                }
            }
            finally { Submission.Release(); }
        }

        if (!File.Exists(requestPath))
            throw new InvalidOperationException("Der Build-Kandidat wurde nicht gefunden.");
        using var binding = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath, cancellationToken).ConfigureAwait(false));
        if (binding.RootElement.GetProperty("sessionId").GetGuid() != sessionId
            || !string.Equals(binding.RootElement.GetProperty("workspace").GetString(), workspace, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Der Build-Kandidat gehört zu einer anderen Sitzung oder einem anderen Workspace.");

        if (proposal.Name == "selfimprovement.status" && proposal.Arguments.TryGetProperty("logStep", out var step))
        {
            var name = step.ValueKind == JsonValueKind.String ? step.GetString() : null;
            if (name is not ("portable" or "app-tests" or "ai-stack" or "portable-smoke" or "gateway-smoke"))
                throw new InvalidOperationException("Der Prüfschritt ist ungültig.");
            var offset = proposal.Arguments.TryGetProperty("lineOffset", out var lineOffset)
                && lineOffset.ValueKind == JsonValueKind.Number && lineOffset.TryGetInt32(out var line)
                ? Math.Clamp(line, 0, 100_000) : 0;
            return await ReadLogAsync(proposal.ProposalId, candidateId, directory, name, offset, cancellationToken).ConfigureAwait(false);
        }

        var wait = proposal.Name == "selfimprovement.status" && proposal.Arguments.TryGetProperty("waitSeconds", out var waitValue)
            && waitValue.ValueKind == JsonValueKind.Number && waitValue.TryGetInt32(out var seconds)
            ? Math.Clamp(seconds, 0, 20) : 0;
        var lastUpdate = File.GetLastWriteTimeUtc(reportPath);
        for (var elapsed = 0; elapsed < wait && lastUpdate == File.GetLastWriteTimeUtc(reportPath); elapsed++)
        {
            if (File.Exists(reportPath))
            {
                using var current = await ReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
                if (current.RootElement.GetProperty("status").GetString() is "verified" or "failed") break;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        if (proposal.Name == "selfimprovement.apply")
        {
            if (applyContext is null)
                throw new InvalidOperationException("Die Übernahme braucht den zugehörigen Gateway-Lauf.");
            // A started operation is durable: a duplicate or recovered proposal joins it without revalidating.
            if (File.Exists(Path.Combine(SelfImprovementApply.OperationDirectory(SelfImprovementApply.OperationId(sessionId, proposal.ProposalId)), "operation.json")))
            {
                using var recorded = await ReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
                return await SelfImprovementApply.ExecuteAsync(proposal, sessionId, candidateId, recorded.RootElement, applyContext, cancellationToken)
                    .ConfigureAwait(false);
            }
            using var report = File.Exists(reportPath) ? await ReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false) : null;
            IReadOnlyList<SelfImprovementCandidateValidator.Problem> problems = report is null
                ? [new("selfImprovement.candidate_not_verified", "Für diesen Kandidaten liegt noch kein Build-Bericht vor.")]
                : await SelfImprovementCandidateValidator.ValidateAsync(candidateId, directory, workspace,
                    report.RootElement, ImageDigestOverride, cancellationToken).ConfigureAwait(false);
            if (problems.Count > 0)
                return new(proposal.ProposalId, "rejected", JsonSerializer.SerializeToElement(new
                {
                    candidateId, reportPath, problems = problems.Select(p => new { code = p.Code, detail = p.Detail, paths = p.Paths }),
                }, Json), problems[0].Code, string.Join(" ", problems.Select(p => p.Detail)));
            return await SelfImprovementApply.ExecuteAsync(proposal, sessionId, candidateId, report!.RootElement, applyContext, cancellationToken)
                .ConfigureAwait(false);
        }
        if (File.Exists(reportPath))
        {
            using var report = await ReadReportAsync(reportPath, cancellationToken).ConfigureAwait(false);
            if (report.RootElement.GetProperty("status").GetString() == "running" && !WorkerIsRunning(report.RootElement))
                return new(proposal.ProposalId, "completed", JsonSerializer.SerializeToElement(new
                {
                    candidateId, status = "failed", detail = "Build-Broker wurde unterbrochen.",
                    reportPath, lastReport = report.RootElement,
                }, Json));
            return new(proposal.ProposalId, "completed", report.RootElement.Clone());
        }
        return new(proposal.ProposalId, "completed",
            JsonSerializer.SerializeToElement(new { candidateId, status = "queued", reportPath }, Json));
    }

    private static bool WorkerIsRunning(JsonElement report)
    {
        if (!report.TryGetProperty("workerPid", out var pid) || !pid.TryGetInt32(out var id)
            || !report.TryGetProperty("workerStartedAt", out var started) || !started.TryGetDateTimeOffset(out var startTime))
            return false;
        try
        {
            using var worker = Process.GetProcessById(id);
            return !worker.HasExited && worker.StartTime.ToUniversalTime() == startTime.UtcDateTime;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<JsonDocument> ReadReportAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonDocument.ParseAsync(file, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ClientToolResult> ReadLogAsync(string proposalId, string candidateId, string directory,
        string step, int offset, CancellationToken cancellationToken)
    {
        var log = Path.Combine(directory, step + ".log");
        if (!File.Exists(log)) throw new InvalidOperationException("Für diesen Prüfschritt liegt noch kein Protokoll vor.");
        await using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);
        var current = 0;
        var lines = new List<string>();
        while (lines.Count < 20 && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } text)
        {
            if (current++ >= offset) lines.Add(text.Length > 600 ? text[..600] : text);
        }
        return new(proposalId, "completed", JsonSerializer.SerializeToElement(new
        {
            candidateId, step, lines, nextOffset = current, hasMore = lines.Count == 20,
        }, Json));
    }

    private static Process StartWorker(string request)
    {
        var script = ApplicationAssets.ResolvePath("Assets", "SelfImprovement", "self-improvement-broker.ps1");
        var shell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(request)! };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-File", script, "-RequestPath", request })
            start.ArgumentList.Add(arg);
        return Process.Start(start) ?? throw new InvalidOperationException("Der Build-Broker konnte nicht gestartet werden.");
    }
}
