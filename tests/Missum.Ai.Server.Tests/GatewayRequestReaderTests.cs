using Missum.Ai.Contracts;
using Missum.Ai.Server.Core.Gateway;
using Microsoft.AspNetCore.Http;

namespace Missum.Ai.Server.Tests;

public sealed class GatewayRequestReaderTests
{
    [Fact]
    public async Task RejectsOversizedChunkedJsonWithoutContentLength()
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = null;
        context.Request.Body = new MemoryStream(new byte[MissumAiProtocol.MaximumJsonBytes + 1]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            GatewayRequestReader.ReadJsonAsync<RunRequest>(context, MissumAiProtocol.CreateJsonOptions()));

        Assert.Contains("2 MiB", exception.Message, StringComparison.Ordinal);
    }
}
