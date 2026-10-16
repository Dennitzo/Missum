using System.Text.Json;

namespace Missum.Core.Research;

/// <summary>Reads the host-owned, source-bound Lean receipt. A status or prose claim alone is never a proof.</summary>
public static class ScientificLeanEvidence
{
    public static bool TryRead(ResearchVerification check, string projectId, out ScientificLeanProofEvidence? evidence)
    {
        evidence = null;
        if (check.ProjectId != projectId || check.Method != "math.formalProof" || check.Status != "passed"
            || check.Dimension != "formal" || check.TargetType != "experiment") return false;
        try
        {
            using var document = JsonDocument.Parse(check.EvidenceJson);
            var root = document.RootElement;
            if (Text(root, "projectId") != projectId || !True(root, "success")
                || Text(root, "formalVerification") != "KernelAccepted"
                || !root.TryGetProperty("lean", out var lean) || Text(lean, "operation") != "check"
                || Text(lean, "formalVerification") != "KernelAccepted" || Text(lean, "axiomAudit") != "passed"
                || !True(lean, "success") || !Zero(lean, "exitCode")
                || !lean.TryGetProperty("unexpectedAxioms", out var unexpected) || unexpected.ValueKind != JsonValueKind.Array
                || unexpected.GetArrayLength() != 0) return false;
            var source = Text(lean, "source");
            var sourceHash = Text(lean, "sourceFileSha256");
            var checkedSource = Text(lean, "checkedSource");
            var checkedHash = Text(lean, "checkedSourceSha256");
            if (!RelativeLeanPath(source) || !RelativeLeanPath(checkedSource) || !Hash(sourceHash) || !Hash(checkedHash)
                || !root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array
                || !runs.EnumerateArray().Any(run => Zero(run, "exitCode") && !True(run, "timedOut")
                    && Text(run, "runId").Length > 0 && Text(run, "snapshotId").Length > 0
                    && Bound(run, source, sourceHash) && Bound(run, checkedSource, checkedHash))) return false;
            if (!lean.TryGetProperty("proofs", out var proofs) || proofs.ValueKind != JsonValueKind.Array || proofs.GetArrayLength() == 0)
                return false;
            var declarations = new List<ScientificLeanDeclaration>();
            var complete = !True(lean, "proofsTruncated") && (!lean.TryGetProperty("proofCount", out var count)
                || count.ValueKind == JsonValueKind.Number && count.TryGetInt32(out var total) && total == proofs.GetArrayLength());
            foreach (var proof in proofs.EnumerateArray())
            {
                var statement = Text(proof, "statement");
                var declaration = Text(proof, "declaration");
                if (statement.Length == 0 || declaration.Length == 0 || !proof.TryGetProperty("axioms", out var axioms)
                    || axioms.ValueKind != JsonValueKind.Array || axioms.EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String
                        || value.GetString() is not ("propext" or "Classical.choice" or "Quot.sound"))) return false;
                declarations.Add(new(declaration, statement));
                complete &= !True(proof, "statementTruncated") && !True(proof, "declarationTruncated")
                    && (True(lean, "completeReceiptLoaded") || statement.Length < 600 && declaration.Length < 256
                        || proof.TryGetProperty("statementTruncated", out _) && proof.TryGetProperty("declarationTruncated", out _));
            }
            evidence = new(check.Id, check.TargetId, source, sourceHash, Text(lean, "leanVersion"), declarations, complete);
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool Bound(JsonElement run, string path, string hash) => run.TryGetProperty("inputHashes", out var hashes)
        && hashes.ValueKind == JsonValueKind.Object && (Text(hashes, "work/" + path) is { Length: > 0 } value
            ? value.Equals(hash, StringComparison.OrdinalIgnoreCase) : Text(hashes, path).Equals(hash, StringComparison.OrdinalIgnoreCase));
    private static bool RelativeLeanPath(string path) => path.EndsWith(".lean", StringComparison.OrdinalIgnoreCase)
        && !path.Contains(':') && !path.Contains('\\') && !path.Any(char.IsControl)
        && path.Split('/').All(part => part is not ("" or "." or ".."));
    private static bool Hash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static bool True(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool Zero(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number == 0;
    private static string Text(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

public sealed record ScientificLeanDeclaration(string Name, string Statement);
public sealed record ScientificLeanProofEvidence(string CheckId, string ExperimentId, string SourcePath,
    string SourceSha256, string LeanVersion, IReadOnlyList<ScientificLeanDeclaration> Declarations, bool Complete = true);
