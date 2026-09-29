namespace Missum.App.Services;

internal static class MicrophoneSpectrum
{
    internal static double[] Analyze(ReadOnlySpan<byte> pcm)
    {
        var bands = new double[5];
        var count = pcm.Length / 2;
        for (var band = 0; band < bands.Length; band++)
        {
            var coefficient = 2 * Math.Cos(2 * Math.PI * (180 * Math.Pow(2, band)) / 16000);
            double q1 = 0, q2 = 0;
            for (var i = 0; i < count; i++)
            {
                var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(i * 2, 2)) / 32768d;
                var q = sample + coefficient * q1 - q2; q2 = q1; q1 = q;
            }
            var magnitude = Math.Sqrt(Math.Max(0, q1 * q1 + q2 * q2 - coefficient * q1 * q2)) / Math.Max(1, count);
            bands[band] = Math.Clamp(magnitude * 35, 0, 1);
        }
        return bands;
    }
}
