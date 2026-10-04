using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Configuration;
using Missum.Ai.Server.Core.Research;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Missum.Ai.Server.Tests;

public sealed class WebSearchRankingTests
{
    [Fact]
    public void RecordedPlanckAreaQueryPrefersConcreteCoverageAndKeepsWeakResultsAvailable()
    {
        // Bounded title/snippet excerpts from run-25c1..., search event 1893076.
        const string query = "holographic principle entropy per Planck area cell k_B ln 2 't Hooft Susskind Bekenstein bound";
        WebSearchResult[] results =
        [
            new("AN ATTEMPT AT A NATURAL SCIENCE EXPLANATION OF THE UFO/UAP PHENOMENON IN THE CONTEXT OF THE ACTA UNIVERSI 2025 HYPOTHESIS",
                "https://doi.org/10.24108/preprints-3113919", "The paper presents a study of the UFO/UAP phenomenon in the context of the new Acta Universi hypothesis about the nature of dark energy.", "openalex"),
            new("Growing Amount of Information inside a Space-Region may cause to expand the Region and to change its Surface-Curvature-(1s-e-2022)",
                "https://vixra.org/pdf/2209.0135v1.pdf", "According to G. t, HOOFT,s holographic principle the cornbination of quantum mechanics and gravity requires that a 3-dimensional space-region to be projected on the 2-dimensional bounding surface of the region.", "semantic scholar"),
            new("Jacob Bekenstein (Black Hole Entropy and the Bekenstein Bound)",
                "https://www.worldscientific.com/doi/abs/10.1142/9789811203961_0012", "", "crossref"),
            new("The Scientific Programme of Planck", "http://arxiv.org/abs/astro-ph/0604069v1",
                "Planck, the third space CMB mission after COBE and WMAP, is designed to extract essentially all of the information in the CMB temperature anisotropies.", "arxiv"),
            new("M-Theory and Quantum Geometry (The Holographic Principle)", "http://link.springer.com/10.1007/978-94-011-4303-5_4", "", "crossref"),
            new("Bekenstein, I, and the quantum of black-hole surface area", "http://arxiv.org/abs/1805.03660v1",
                "My personal contribution to the ongoing attempts to understand the evenly spaced (discrete) area spectrum of quantized black holes, as originally suggested by Bekenstein in the early days of his scientific career, is described.", "arxiv"),
            new("HOLOGRAPHIC BOUND FROM SECOND LAW", "https://www.worldscientific.com/doi/10.1142/9789812777386_0038", "", "crossref"),
            new("Fischler-Susskind holographic cosmology revisited", "http://arxiv.org/abs/0704.1637v2",
                "The Fischler-Susskind prescription is used to obtain the maximum number of degrees of freedom per Planck volume at the Planck era compatible with the Holographic Principle.", "arxiv"),
            new("arXiv:hep-th/0203101v2 29 Jun 2002", "https://arxiv.org/pdf/hep-th/0203101",
                "entropy bound, 't Hooft (1993) and Susskind (1995b). The entropy bound of one bit per Planck area, however, is not explicit in", "google cse"),
        ];
        var selected = WebResearchService.RankSearchResults(query, results, 3);
        Assert.Contains(results[2], selected);
        Assert.Contains(results[^1], selected);
        Assert.DoesNotContain(results[0], selected);
        Assert.DoesNotContain(results[3], selected);
        var all = WebResearchService.RankSearchResults(query, results, 20);
        Assert.Equal(results.Length, all.Count);
        Assert.Contains(results[0], all);
        Assert.All(all, result => Assert.Contains(result, results));
    }

    [Fact]
    public async Task SearchAppliesCoverageAndDeduplicationBeforeTheRequestedLimit()
    {
        using var handler = new SearchHandler("""
            {"results":[
              {"title":"Planck satellite","url":"https://example.org/cmb","engine":"google cse"},
              {"title":"Bekenstein entropy bound","url":"https://doi.org/10.1142/9789811203961_0012","engine":"crossref"},
              {"title":"Bekenstein entropy bound","url":"https://www.worldscientific.com/doi/abs/10.1142/9789811203961_0012","engine":"openalex"},
              {"title":"Planck area entropy bound","url":"https://arxiv.org/pdf/hep-th/0203101","engine":"bing"}],
             "unresponsive_engines":[]}
            """);
        using var service = new WebResearchService(new TestClientFactory(handler), Options.Create(new MissumAiServerOptions()));
        var response = await service.SearchAsync(new("Bekenstein entropy bound Planck area", MaximumResults: 2, Profile: "science"));
        Assert.Equal(2, response.Results.Count);
        Assert.Contains(response.Results, static result => result.Url == "https://arxiv.org/pdf/hep-th/0203101");
        Assert.Contains(response.Results, static result => result.Url == "https://doi.org/10.1142/9789811203961_0012");
        Assert.Equal("searxng", response.Provider);
        Assert.False(response.IsFallback);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://dx.doi.org/10.1142/EXAMPLE_0012")]
    [InlineData("https://publisher.example/doi/pdf/10.1142/example_0012")]
    [InlineData("https://publisher.example/article/10.1142%2Fexample_0012")]
    public void DoiResolverAndPublisherUrlsDeduplicateWithoutChangingTheChosenProvenance(string alternative)
    {
        var original = new WebSearchResult("Bekenstein entropy bound", "https://doi.org/10.1142/example_0012", "entropy bound", "crossref");
        var duplicate = new WebSearchResult(original.Title, alternative, original.Snippet, "openalex");
        var result = Assert.Single(WebResearchService.RankSearchResults("Bekenstein entropy bound", [original, duplicate], 10));
        Assert.Same(original, result);
        Assert.Equal("crossref", result.Source);
        Assert.Equal(original.Title, result.Title);
    }

    [Fact]
    public void MentioningADoiInASnippetDoesNotDeduplicateDifferentWorks()
    {
        WebSearchResult[] results =
        [
            new("Review", "https://example.org/review", "Cites DOI 10.1142/example_0012"),
            new("Original work", "https://doi.org/10.1142/example_0012", null),
            new("Other work", "https://doi.org/10.1142/example_0013", null),
        ];
        Assert.Equal(3, WebResearchService.RankSearchResults("scientific review", results, 10).Count);
    }

    [Fact]
    public void ConcreteBingHitOutranksUnrelatedSpecialistAndTiesKeepExistingStablePreference()
    {
        var precise = new WebSearchResult("TaskGroup cancellation", "https://docs.python.org/3/", null, "bing");
        var unrelated = new WebSearchResult("Other API", "https://example.org/unrelated", null, "google cse");
        Assert.Same(precise, Assert.Single(WebResearchService.RankSearchResults("TaskGroup cancellation", [unrelated, precise], 1)));
        var cse = precise with { Url = "https://example.org/cse", Source = "google cse" };
        var arxiv = precise with { Url = "https://example.org/arxiv", Source = "arxiv" };
        Assert.Equal(new[] { cse, arxiv, precise }, WebResearchService.RankSearchResults("TaskGroup cancellation", [precise, cse, arxiv], 10));
    }

    [Fact]
    public void QueryOperatorsDoNotBecomeRelevanceTermsAndRepeatedTermsDoNotInflateCoverage()
    {
        var exact = new WebSearchResult("TaskGroup cancellation", "https://example.org/exact", null);
        var noisy = new WebSearchResult("docs python org pdf cancellation cancellation satellite", "https://example.org/noisy", null);
        var selected = WebResearchService.RankSearchResults("site:docs.python.org filetype:pdf TaskGroup cancellation -satellite", [noisy, exact], 1);
        Assert.Same(exact, Assert.Single(selected));
    }

    [Fact]
    public void CanonicalUrlDeduplicationPreservesQueryDataAndDistinctDoiSuffixes()
    {
        WebSearchResult[] results =
        [
            new("One", "https://EXAMPLE.org/a#first", null),
            new("One", "https://example.org/a#second", null),
            new("Two", "https://example.org/a?id=1", null),
            new("Three", "https://example.org/a?id=2", null),
            new("Four", "https://example.org/a?id=2/", null),
        ];
        Assert.Equal(4, WebResearchService.RankSearchResults("reference", results, 10).Count);
    }

    private sealed class TestClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class SearchHandler(string response) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
