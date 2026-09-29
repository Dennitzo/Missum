using Missum.App.Services;
using System.Buffers.Binary;

namespace Missum.Tests;

public sealed class MicrophoneSpectrumTests
{
    [Fact]
    public void SilenceHasNoAnimatedEnergy() => Assert.All(MicrophoneSpectrum.Analyze(new byte[3200]), value => Assert.Equal(0d, value));

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void ActualPcmFrequencySelectsTheMatchingBand(int band)
    {
        var pcm = new byte[3200];
        var hz = 180 * Math.Pow(2, band);
        for (var i = 0; i < 1600; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)(1000 * Math.Sin(2 * Math.PI * hz * i / 16000)));
        var spectrum = MicrophoneSpectrum.Analyze(pcm);
        Assert.True(spectrum[band] > .4);
        Assert.Equal(band, Array.IndexOf(spectrum, spectrum.Max()));
        Assert.All(spectrum, value => Assert.InRange(value, 0, 1));
    }
}
