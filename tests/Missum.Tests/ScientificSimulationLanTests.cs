using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services;

namespace Missum.Tests;

[Collection("Assistant LAN host")]
public sealed class ScientificSimulationLanTests
{
    [Fact]
    public async Task RegisteredScienceHtmlIsInlineScriptEnabledAndIsolatedWhileOtherHtmlDownloads()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-simulation-lan-" + Guid.NewGuid().ToString("N"));
        var web = Path.Combine(root, "web");
        Directory.CreateDirectory(web);
        await File.WriteAllTextAsync(Path.Combine(web, "index.html"), "<!doctype html><title>Simulation test</title>");
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var previousPort = Environment.GetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT");
        Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await using var host = new AssistantLanWebHost(root, NullLogger.Instance);
            const string html = "<!doctype html><canvas id=\"canvas\"></canvas><script>window.simulationTick=1;</script>";
            host.ResourceResolver = (_, _, _) => Task.FromResult<LanArtifactResource?>(new(
                new MemoryStream(Encoding.UTF8.GetBytes(html)), "text/html", "animation.html"));
            await host.StartAsync(web);
            using var http = new HttpClient();
            using var simulation = await http.GetAsync($"http://127.0.0.1:{port}/science-resources/simulation");

            Assert.Equal("text/html", simulation.Content.Headers.ContentType!.MediaType);
            Assert.Null(simulation.Content.Headers.ContentDisposition);
            Assert.Equal(html, await simulation.Content.ReadAsStringAsync());
            var policy = Assert.Single(simulation.Headers.GetValues("Content-Security-Policy"));
            Assert.Contains("sandbox allow-scripts", policy, StringComparison.Ordinal);
            Assert.Contains("connect-src 'none'", policy, StringComparison.Ordinal);
            Assert.Contains("frame-ancestors 'self'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("allow-same-origin", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("allow-popups", policy, StringComparison.Ordinal);

            using var other = await http.GetAsync($"http://127.0.0.1:{port}/settings-resources/other");
            Assert.Equal("application/octet-stream", other.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", other.Content.Headers.ContentDisposition!.DispositionType);
            Assert.DoesNotContain("allow-scripts", Assert.Single(other.Headers.GetValues("Content-Security-Policy")), StringComparison.Ordinal);
            using var download = await http.GetAsync($"http://127.0.0.1:{port}/science-resources/simulation?download=1");
            Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASSISTANT_LAN_WEB_PORT", previousPort);
            Directory.Delete(root, recursive: true);
        }
    }
}
