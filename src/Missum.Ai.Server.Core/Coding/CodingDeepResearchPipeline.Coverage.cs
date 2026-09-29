using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Core.Coding;

internal static partial class CodingDeepResearchPipeline
{
    private static readonly string[] QuestionStatuses = ["answered", "partial", "unanswered"];
    private static readonly string[] IssueStatuses = ["materialOpen", "resolved", "nonMaterial"];
    private static readonly string[] RequiredQuestionAssessmentFields = ["questionId", "status", "findingIndexes", "remainingGaps", "reason"];
    private static readonly string[] RequiredIssueAssessmentFields = ["issueId", "status", "questionIds", "findingIndexes", "reason"];

    private static ResearchOpenIssue[] CreateOpenIssues(List<string> uncertainties, List<string> counterexamples) =>
        uncertainties.Select(static text => (Kind: "uncertainty", Text: text))
            .Concat(counterexamples.Select(static text => (Kind: "counterexample", Text: text)))
            .Distinct().Select(static issue => new ResearchOpenIssue(
                "issue-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issue.Kind + "\n" + issue.Text)))[..16],
                issue.Kind, issue.Text)).ToArray();

    private static string FindingStatus(int index, List<ResearchVerificationResult> verifications)
    {
        var matches = verifications.Where(item => item.ClaimIndex == index).ToArray();
        if (matches.Length == 0) return "supported";
        if (matches.Length != 1 || !VerificationStatuses.Contains(matches[0].Status, StringComparer.Ordinal)
            || !double.IsFinite(matches[0].Confidence) || matches[0].Confidence is < 0 or > 1) return "unresolved";
        return matches[0].Status;
    }

    private static ResearchCoverage CreateCoverage(List<ResearchQuestion> plan, int findingCount,
        List<ResearchVerificationResult> verifications, List<ResearchQuestionAssessment> questions,
        List<ResearchIssueAssessment> issueAssessments, ResearchOpenIssue[] issues)
    {
        var questionIds = plan.Select(static question => question.Id).ToHashSet(StringComparer.Ordinal);
        var issueCoverage = issues.Select(issue =>
        {
            var matches = issueAssessments.Where(item => item.IssueId == issue.Id).ToArray();
            var assessment = matches.Length == 1 ? matches[0] : null;
            var validTargets = assessment is not null
                && assessment.QuestionIds.Distinct(StringComparer.Ordinal).Count() == assessment.QuestionIds.Length
                && assessment.QuestionIds.All(questionIds.Contains);
            var valid = validTargets && IssueStatuses.Contains(assessment!.Status, StringComparer.Ordinal)
                && ValidFindingIndexes(assessment!.FindingIndexes, findingCount);
            var resolved = valid && assessment!.Status == "resolved" && assessment!.FindingIndexes.Length > 0
                && assessment!.FindingIndexes.All(index => FindingStatus(index, verifications) == "verified");
            var nonMaterial = valid && assessment!.Status == "nonMaterial";
            return new ResearchIssueCoverage(issue.Id, issue.Kind, issue.Text,
                resolved ? "resolved" : nonMaterial ? "nonMaterial" : "materialOpen",
                validTargets ? assessment!.QuestionIds : [], resolved || nonMaterial,
                valid ? assessment!.Reason : "Dieser Hinweis wurde nicht eindeutig eingeordnet und bleibt offen.");
        }).ToArray();
        var questionCoverage = plan.Select(question =>
        {
            var matches = questions.Where(item => item.QuestionId == question.Id).ToArray();
            var assessment = matches.Length == 1 ? matches[0] : null;
            var valid = assessment is not null && QuestionStatuses.Contains(assessment.Status, StringComparer.Ordinal)
                && ValidFindingIndexes(assessment.FindingIndexes, findingCount);
            var indexes = valid ? assessment!.FindingIndexes : [];
            var relatedIssues = issueCoverage.Where(issue => issue.IsMaterialOpen
                && (issue.QuestionIds.Length == 0 || issue.QuestionIds.Contains(question.Id, StringComparer.Ordinal))).ToArray();
            var gaps = (valid ? assessment!.RemainingGaps : []).Concat(relatedIssues.Select(static issue => issue.Text))
                .Distinct(StringComparer.Ordinal).ToArray();
            var complete = valid && assessment!.Status == "answered" && indexes.Length > 0 && gaps.Length == 0
                && indexes.All(index => FindingStatus(index, verifications) == "verified");
            var status = indexes.Length == 0 || assessment!.Status == "unanswered" ? "unresolved"
                : indexes.Any(index => FindingStatus(index, verifications) == "refuted") ? "refuted"
                : indexes.Any(index => FindingStatus(index, verifications) == "conflictingEvidence") ? "conflictingEvidence"
                : complete ? "verified"
                : "provisionallySupported";
            var reason = valid ? assessment!.Reason : "Die Abdeckung dieser Teilfrage ist nicht eindeutig nachgewiesen.";
            return new ResearchQuestionCoverage(question.Id, status, indexes, gaps, reason, complete);
        }).ToArray();
        return new(questionCoverage.Length > 0 && questionCoverage.All(static question => question.IsComplete)
            && issueCoverage.All(static issue => !issue.IsMaterialOpen), questionCoverage, issueCoverage);
    }

    private static bool ValidFindingIndexes(int[] indexes, int findingCount) =>
        indexes.Distinct().Count() == indexes.Length && indexes.All(index => index >= 0 && index < findingCount);

    private static void ReadCoverageAssessments(JsonElement value, List<ResearchQuestion> plan, ResearchOpenIssue[] expectedIssues,
        List<ResearchQuestionAssessment> questions, List<ResearchIssueAssessment> issues)
    {
        // Missing coverage in older checkpoints/fixtures is allowed, but never means
        // that every question was answered. Malformed or duplicate entries stay open.
        if (value.TryGetProperty("questionAssessments", out var questionValues) && questionValues.ValueKind == JsonValueKind.Array)
        {
            if (questionValues.GetArrayLength() > plan.Count)
            {
                questions.AddRange(plan.Select(static question => new ResearchQuestionAssessment(question.Id, "invalid", [], [],
                    "Die Antwort enthält zusätzliche oder doppelte Fragezuordnungen.")));
            }
            else foreach (var item in questionValues.EnumerateArray())
            {
                if (!TryText(item, "questionId", out var id)) continue;
                if (!TryText(item, "status", out var status)
                    || !TryText(item, "reason", out var reason) || !TryIndexes(item, out var indexes)
                    || !TryStrings(item, "remainingGaps", out var gaps))
                {
                    questions.Add(new(id, "invalid", [], [], "Die Fragezuordnung ist ungültig."));
                    continue;
                }
                questions.Add(new(id, status, indexes, gaps, reason));
            }
        }
        if (value.TryGetProperty("issueAssessments", out var issueValues) && issueValues.ValueKind == JsonValueKind.Array)
        {
            if (issueValues.GetArrayLength() > expectedIssues.Length)
            {
                issues.AddRange(expectedIssues.Select(static issue => new ResearchIssueAssessment(issue.Id, "invalid", [], [],
                    "Die Antwort enthält zusätzliche oder doppelte Hinweiszuordnungen.")));
            }
            else foreach (var item in issueValues.EnumerateArray())
            {
                if (!TryText(item, "issueId", out var id)) continue;
                if (!TryText(item, "status", out var status)
                    || !TryText(item, "reason", out var reason) || !TryIndexes(item, out var indexes)
                    || !TryStrings(item, "questionIds", out var questionIds))
                {
                    issues.Add(new(id, "invalid", [], [], "Die Hinweiseinstufung ist ungültig."));
                    continue;
                }
                issues.Add(new(id, status, questionIds, indexes, reason));
            }
        }
    }

    private static bool TryText(JsonElement item, string name, out string text)
    {
        text = string.Empty;
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String || value.GetString() is not { } result
            || string.IsNullOrWhiteSpace(result) || result.Length > 500) return false;
        text = result;
        return true;
    }

    private static bool TryIndexes(JsonElement item, out int[] indexes)
    {
        indexes = [];
        if (!item.TryGetProperty("findingIndexes", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > 8) return false;
        var parsed = new List<int>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var index)) return false;
            parsed.Add(index);
        }
        indexes = parsed.ToArray();
        return true;
    }

    private static bool TryStrings(JsonElement item, string name, out string[] strings)
    {
        strings = [];
        if (!item.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() > 12) return false;
        var parsed = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
                || string.IsNullOrWhiteSpace(text) || text.Length > 500) return false;
            parsed.Add(text);
        }
        strings = parsed.ToArray();
        return true;
    }

    private static object FindingIndexesSchema(int count) => new
    {
        type = "array", maxItems = count, uniqueItems = true,
        items = new { type = "integer", minimum = 0, maximum = Math.Max(0, count - 1) },
    };

    private static object QuestionAssessmentSchema(List<ResearchQuestion> questions, int findingCount) => new
    {
        type = "array", minItems = questions.Count, maxItems = questions.Count,
        items = new
        {
            type = "object", properties = new
            {
                questionId = new { type = "string", @enum = questions.Select(static question => question.Id).ToArray() },
                status = new { type = "string", @enum = QuestionStatuses },
                findingIndexes = FindingIndexesSchema(findingCount),
                remainingGaps = new { type = "array", maxItems = 3, items = new { type = "string", maxLength = 200 } },
                reason = new { type = "string", maxLength = 240 },
            },
            required = RequiredQuestionAssessmentFields, additionalProperties = false,
        },
    };

    private static object IssueAssessmentSchema(List<ResearchQuestion> questions, ResearchOpenIssue[] issues, int findingCount) => new
    {
        type = "array", minItems = issues.Length, maxItems = issues.Length,
        items = new
        {
            type = "object", properties = new
            {
                // A nonempty enum keeps native JSON-schema grammars valid even when
                // the surrounding array has maxItems=0 because no issues exist.
                issueId = new { type = "string", @enum = issues.Length > 0 ? issues.Select(static issue => issue.Id).ToArray() : ["no-issues"] },
                status = new { type = "string", @enum = IssueStatuses },
                questionIds = new { type = "array", maxItems = questions.Count, uniqueItems = true,
                    items = new { type = "string", @enum = questions.Select(static question => question.Id).ToArray() } },
                findingIndexes = FindingIndexesSchema(findingCount),
                reason = new { type = "string", maxLength = 240 },
            },
            required = RequiredIssueAssessmentFields, additionalProperties = false,
        },
    };

    private sealed record ResearchOpenIssue(string Id, string Kind, string Text);
    private sealed record ResearchQuestionAssessment(string QuestionId, string Status, int[] FindingIndexes, string[] RemainingGaps, string Reason);
    private sealed record ResearchIssueAssessment(string IssueId, string Status, string[] QuestionIds, int[] FindingIndexes, string Reason);
    private sealed record ResearchQuestionCoverage(string QuestionId, string Status, int[] FindingIndexes, string[] RemainingGaps, string Reason, bool IsComplete);
    private sealed record ResearchIssueCoverage(string IssueId, string Kind, string Text, string Status, string[] QuestionIds, bool IsClosed, string Reason)
    {
        public bool IsMaterialOpen => !IsClosed;
    }
    private sealed record ResearchCoverage(bool IsComplete, ResearchQuestionCoverage[] Questions, ResearchIssueCoverage[] Issues);
}
