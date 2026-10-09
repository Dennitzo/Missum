using Missum.Core.Coding;

namespace Missum.Tests;

public sealed class DiffPagingTests
{
    [Fact]
    public void EightyTwoThousandLinesRemainAccessibleWithBoundedPagesAndCorrectNumbers()
    {
        var source = "diff --git a/plot.py b/plot.py\n--- /dev/null\n+++ b/plot.py\n@@ -0,0 +1,82000 @@\n"
            + string.Join("\n", Enumerable.Range(1, 82_000).Select(index => "+value_" + index));
        var document = CodingDiffDocument.Parse(source);
        Assert.Equal(82_004, document.LineCount); Assert.Equal(274, document.PageCount);
        var all = Enumerable.Range(0, document.PageCount).SelectMany(document.Page).ToArray();
        Assert.Equal(document.LineCount, all.Length);
        Assert.All(Enumerable.Range(0, document.PageCount), page => Assert.InRange(document.Page(page).Count, 1, CodingDiffDocument.PageSize));
        var last = document.Page(document.PageCount - 1)[^1];
        Assert.Equal(CodingDiffLineKind.Added, last.Kind); Assert.Equal("value_82000", last.Text);
        Assert.Equal(82_000, last.NewLine); Assert.Null(last.OldLine); Assert.Equal("b/plot.py", last.Path);
    }

    [Fact]
    public void PagesKeepHunkStateAndDoNotMistakeAddedHeaderLikeSourceForMetadata()
    {
        var diff = "--- a/test.cs\n+++ b/test.cs\n@@ -299,3 +401,3 @@\n-old\n+++new\n context\n\\ No newline at end of file\n-last\n+final\n";
        var document = CodingDiffDocument.Parse(diff);
        var rows = document.Page(0).Where(line => line.Kind != CodingDiffLineKind.Metadata).ToArray();
        Assert.Equal(299, rows[0].OldLine); Assert.Equal("++new", rows[1].Text); Assert.Equal(401, rows[1].NewLine);
        Assert.Equal(300, rows[2].OldLine); Assert.Equal(402, rows[2].NewLine);
        Assert.Equal(301, rows[3].OldLine); Assert.Equal(403, rows[4].NewLine);
        Assert.Equal(9, document.LineCount);
    }

    [Fact]
    public void CancelledLargeParsingDoesNotContinueCreatingDisplayRows()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => CodingDiffDocument.Parse("+value\n", cancellation.Token));
    }
}
