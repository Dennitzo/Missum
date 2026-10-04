using Missum.App.Controls;

namespace Missum.Tests;

public sealed class NativePlanetPaletteTests
{
    [Fact]
    public void InitialPaletteContainsDistinctAppearancesBeyondColorChanges()
    {
        var appearances = Enumerable.Range(0, NativePlanetPalette.PaletteCount).Select(NativePlanetPalette.Describe).ToArray();
        Assert.Equal(4096, appearances.Select(appearance => NativePlanetPalette.VisualSignatureFor(appearance.Index)).Distinct().Count());
        Assert.True(appearances.Select(appearance => (appearance.Surface, appearance.Orbit, appearance.Satellite)).Distinct().Count() >= 128);
        Assert.True(appearances.Select(appearance => appearance.Body).Distinct().Count() >= 16);
    }

    [Fact]
    public void LaterAgentsDoNotRepeatTheInitialPalette()
    {
        var signatures = Enumerable.Range(0, 20_000).Select(NativePlanetPalette.VisualSignatureFor).ToArray();
        Assert.Equal(signatures.Length, signatures.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DistantAndBoundaryIdentitiesKeepDistinctStableAppearances()
    {
        var indices = Enumerable.Range(0, 32).SelectMany(index => new[]
        {
            index, 4096 + index, 8_388_608 + index, int.MaxValue - index,
        }).ToArray();
        var signatures = indices.Select(NativePlanetPalette.VisualSignatureFor).ToArray();
        Assert.Equal(indices.Length, signatures.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(signatures, indices.Select(NativePlanetPalette.VisualSignatureFor));
        Assert.All(indices.Select(NativePlanetPalette.Describe), appearance => Assert.Equal(255, appearance.Body.A));
    }

    [Fact]
    public void DamagedNegativeIdentityCannotProduceAnAmbiguousPlanet()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NativePlanetPalette.Describe(-1));
    }
}
