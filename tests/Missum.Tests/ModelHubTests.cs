using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Missum.App.Services.ModelHub;

namespace Missum.Tests;

public sealed class ModelHubTests
{
    private const long Gb = 1_000_000_000;

    [Fact]
    public void GroupsRootFilesFoldersAndSplitPartsIntoQuantizations()
    {
        var files = new[]
        {
            new HubFile("README.md", 10, null),
            new HubFile("gemma-3-27b-it-Q4_K_M.gguf", 16 * Gb, "a"),
            new HubFile("gemma-3-27b-it-UD-IQ1_M.gguf", 8 * Gb, "b"),
            new HubFile("mmproj-F16.gguf", 800_000_000, "c"),
            new HubFile("mmproj-BF16.gguf", 800_000_000, "d"),
            new HubFile("BF16/gemma-3-27b-it-BF16-00002-of-00002.gguf", 20 * Gb, "e"),
            new HubFile("BF16/gemma-3-27b-it-BF16-00001-of-00002.gguf", 30 * Gb, "f"),
            new HubFile("UD-Q2_K_XL/gemma-3-27b-it-UD-Q2_K_XL-00001-of-00003.gguf", 9 * Gb, "g"),
            new HubFile("UD-Q2_K_XL/gemma-3-27b-it-UD-Q2_K_XL-00002-of-00003.gguf", 9 * Gb, "h"),
            new HubFile("imatrix_unsloth.gguf", 100, "i"),
            new HubFile("mtp-gemma-Q8_0.gguf", 2 * Gb, "j"),
            new HubFile("Mainline_Support/x.gguf", 1, "k"),
            new HubFile("dspark-gemma-Q8_0.gguf", 500_000_000, "l"),
            new HubFile("dspark/dspark-gemma-BF16.gguf", 650_000_000, "m"),
        };
        var groups = ModelHubQuantizations.Group(files);
        // The incomplete UD-Q2_K_XL (2 of 3 parts) is not offered; mmproj, mtp, imatrix are no models.
        Assert.Equal(["UD-IQ1_M", "Q4_K_M", "BF16"], groups.Select(g => g.Label));
        var bf16 = groups.Single(g => g.Label == "BF16");
        Assert.Equal("BF16/gemma-3-27b-it-BF16-00001-of-00002.gguf", bf16.Primary.Path);
        Assert.Equal(2, bf16.Parts.Count);
        // The vision projector comes along (the 16-bit one), found walking up from the folder.
        Assert.Equal(["mmproj-F16.gguf"], bf16.Companions.Select(c => c.Path));
        Assert.Equal(50 * Gb + 800_000_000, bf16.TotalBytes);
    }

    [Fact]
    public void CatalogIdMatchesTheNativeRuntime()
    {
        // Known id of the model already on this PC: coding/Qwen3.8-Flash-Next-UD-Q2_K_XL~8838fac19bdc.
        var id = ModelHubCatalogId.For("unsloth/Qwen3.8-Flash-Next-GGUF", "f5d175de2cf7f720d45d6064019a81fcf056e986",
            "UD-Q2_K_XL/Qwen3.8-Flash-Next-UD-Q2_K_XL-00001-of-00004.gguf");
        Assert.Equal("coding/Qwen3.8-Flash-Next-UD-Q2_K_XL~8838fac19bdc", id);
        Assert.Equal(@"E:\x\hub\models--unsloth--A-GGUF\snapshots\abc\Q8_0\a.gguf",
            ModelHubCatalogId.FilePath(@"E:\x", "unsloth/A-GGUF", "abc", "Q8_0/a.gguf"));
        Assert.Equal(@"E:\x\hub", ModelHubCatalogId.HubRoot(@"E:\x\hub"));
        Assert.True(ModelHubCatalogId.GatewaySees(@"E:\x\hub", @"E:\x"));
        Assert.False(ModelHubCatalogId.GatewaySees(@"D:\Models", @"E:\x"));
    }

    [Fact]
    public void FitLevelsDistinguishOneGpuSeveralGpusOffloadAndTooBig()
    {
        static HubQuantization Quant(long bytes) => new("Q", [new HubFile("a.gguf", bytes, null)], []);
        var gpu = 48L << 30;
        var two = new SystemResourceSnapshot([new GpuInfo(0, "RTX A", gpu, 0), new GpuInfo(1, "RTX A", gpu, 0)], 224L << 30, 200L << 30);
        Assert.Equal(ModelFitLevel.SingleGpu, ModelFitEstimator.Estimate(Quant(30 * Gb), 30_000_000_000, two).Level);
        Assert.Equal(ModelFitLevel.MultiGpu, ModelFitEstimator.Estimate(Quant(70 * Gb), 70_000_000_000, two).Level);
        var offload = ModelFitEstimator.Estimate(Quant(150 * Gb), 300_000_000_000, two);
        Assert.Equal(ModelFitLevel.Offload, offload.Level);
        Assert.True(offload.RamPartBytes > 0);
        Assert.Equal(ModelFitLevel.Insufficient, ModelFitEstimator.Estimate(Quant(400 * Gb), 800_000_000_000, two).Level);
        var noGpu = new SystemResourceSnapshot([], 64L << 30, 40L << 30);
        Assert.Equal(ModelFitLevel.Offload, ModelFitEstimator.Estimate(Quant(10 * Gb), 10_000_000_000, noGpu).Level);
        // A single GPU never reports "several GPUs".
        var one = new SystemResourceSnapshot([new GpuInfo(0, "RTX", 24L << 30, 0)], 64L << 30, 40L << 30);
        Assert.Equal(ModelFitLevel.Offload, ModelFitEstimator.Estimate(Quant(30 * Gb), 30_000_000_000, one).Level);
    }

    [Fact]
    public void ParsesNvidiaSmiAndStripsFrontMatter()
    {
        var gpus = SystemResources.ParseGpus("1, NVIDIA RTX 6000, 49140, 1200\n0, NVIDIA RTX 6000, 49140, 300\n");
        Assert.Equal([0, 1], gpus.Select(g => g.Index));
        Assert.Equal(49140L * 1024 * 1024, gpus[0].TotalBytes);
        Assert.Equal("# Titel\nText", HuggingFaceHubClient.StripFrontMatter("---\nlicense: mit\n---\n\n# Titel\nText"));
        Assert.Equal("# Ohne", HuggingFaceHubClient.StripFrontMatter("# Ohne"));
    }

    [Fact]
    public async Task DownloadsResumesAfterPauseVerifiesAndPlacesFilesLikeTheHubCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-hub-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        var target = Path.Combine(root, "models");
        try
        {
            var first = Bytes(3_000_000, 1);
            var second = Bytes(2_000_000, 2);
            var server = new FakeHub(new()
            {
                ["Q8_0/m-Q8_0-00001-of-00002.gguf"] = first,
                ["Q8_0/m-Q8_0-00002-of-00002.gguf"] = second,
            });
            var details = new HubModelDetails("unsloth/M-GGUF", "c0ffee", DateTimeOffset.UtcNow, null, null, null, null, []);
            var quant = new HubQuantization("Q8_0",
                [new HubFile("Q8_0/m-Q8_0-00001-of-00002.gguf", first.Length, Sha(first)), new HubFile("Q8_0/m-Q8_0-00002-of-00002.gguf", second.Length, Sha(second))], []);
            await using (var service = new ModelDownloadService(NullLogger<ModelDownloadService>.Instance, data, handler: server) { ResolveUri = (_, _, path) => new Uri("https://hub.test/" + path) })
            {
                server.Throttle = true;
                var job = service.Enqueue(details, quant, target);
                await Until(() => job.DoneBytes > 600_000);
                service.Pause(job.Id);
                await Until(() => job.State == ModelDownloadState.Paused);
                var paused = job.DoneBytes;
                Assert.True(paused < first.Length + second.Length);
                // Pausing keeps the partial file; nothing that looks like a model is visible yet.
                Assert.Empty(Directory.GetFiles(target, "*.gguf", SearchOption.AllDirectories));
                server.Throttle = false;
                service.Resume(job.Id);
                await Until(() => job.State == ModelDownloadState.Completed, 20);
                // The resume continued with a Range request at the byte where it stopped.
                Assert.Contains(server.Ranges, from => from > 0);
                Assert.Equal(first, await File.ReadAllBytesAsync(ModelHubCatalogId.FilePath(target, details.Id, details.Commit, quant.Parts[0].Path)));
                Assert.Equal(second, await File.ReadAllBytesAsync(ModelHubCatalogId.FilePath(target, details.Id, details.Commit, quant.Parts[1].Path)));
                // The second part is placed before the first; refs/main comes last.
                Assert.Equal(["Q8_0/m-Q8_0-00002-of-00002.gguf", "Q8_0/m-Q8_0-00001-of-00002.gguf"], job.Files.Select(f => f.Path));
                Assert.Equal("c0ffee", await File.ReadAllTextAsync(Path.Combine(ModelHubCatalogId.RepositoryFolder(target, details.Id), "refs", "main")));
                Assert.False(Directory.Exists(Path.Combine(ModelHubCatalogId.SnapshotFolder(target, details.Id, details.Commit), ".download")));
                Assert.True(ModelDownloadService.IsInstalled(target, details.Id, details.Commit, quant));
            }
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task AnUpdateReusesUnchangedFilesWithoutDownloadingThem()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-hub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var changed = Bytes(300_000, 7);
            var unchanged = Bytes(200_000, 8);
            var server = new FakeHub(new() { ["m-Q4-00001-of-00002.gguf"] = changed, ["m-Q4-00002-of-00002.gguf"] = unchanged });
            var models = Path.Combine(root, "models");
            var old = ModelHubCatalogId.FilePath(models, "unsloth/M-GGUF", "old", "m-Q4-00002-of-00002.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            await File.WriteAllBytesAsync(old, unchanged);
            var details = new HubModelDetails("unsloth/M-GGUF", "new", DateTimeOffset.UtcNow, null, null, null, null, []);
            var quant = new HubQuantization("Q4", [new HubFile("m-Q4-00001-of-00002.gguf", changed.Length, Sha(changed)), new HubFile("m-Q4-00002-of-00002.gguf", unchanged.Length, Sha(unchanged))], []);
            await using var service = new ModelDownloadService(NullLogger<ModelDownloadService>.Instance, Path.Combine(root, "data"), handler: server)
                { ResolveUri = (_, _, path) => new Uri("https://hub.test/" + path) };
            var job = service.Enqueue(details, quant, models, new Dictionary<string, string> { ["m-Q4-00002-of-00002.gguf"] = old });
            await Until(() => job.State == ModelDownloadState.Completed, 20);
            // Only the changed part came from the server; the other one is the old file.
            Assert.Equal([0L], server.Ranges);
            Assert.Equal(unchanged, await File.ReadAllBytesAsync(ModelHubCatalogId.FilePath(models, details.Id, "new", "m-Q4-00002-of-00002.gguf")));
            Assert.True(ModelDownloadService.IsInstalled(models, details.Id, "new", quant));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task CancelDeletesPartialFilesAndABadChecksumFails()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-hub-" + Guid.NewGuid().ToString("N"));
        try
        {
            var content = Bytes(2_000_000, 3);
            var server = new FakeHub(new() { ["a-Q4_K_M.gguf"] = content }) { Throttle = true };
            var details = new HubModelDetails("unsloth/A-GGUF", "beef", DateTimeOffset.UtcNow, null, null, null, null, []);
            await using var service = new ModelDownloadService(NullLogger<ModelDownloadService>.Instance, Path.Combine(root, "data"), handler: server)
                { ResolveUri = (_, _, path) => new Uri("https://hub.test/" + path) };
            var quant = new HubQuantization("Q4_K_M", [new HubFile("a-Q4_K_M.gguf", content.Length, Sha(content))], []);
            var job = service.Enqueue(details, quant, Path.Combine(root, "models"));
            await Until(() => job.DoneBytes > 200_000);
            service.Cancel(job.Id);
            await Until(() => job.State == ModelDownloadState.Canceled && !File.Exists(ModelDownloadService.PartPath(job, quant.Parts[0])));

            server.Throttle = false;
            var wrong = new HubQuantization("Q4_K_M", [new HubFile("a-Q4_K_M.gguf", content.Length, new string('0', 64))], []);
            var bad = service.Enqueue(details with { Commit = "beef2" }, wrong, Path.Combine(root, "models"));
            await Until(() => bad.State == ModelDownloadState.Failed, 20);
            Assert.Contains("Prüfsumme", bad.Error, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task Until(Func<bool> condition, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Bedingung nicht erreicht");
            await Task.Delay(20);
        }
    }

    /// <summary>Serves files with Range support; throttled, it trickles bytes so a pause lands mid-file.</summary>
    private sealed class FakeHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public volatile bool Throttle;
        public List<long> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath.TrimStart('/'));
            if (!files.TryGetValue(path, out var content)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var from = request.Headers.Range?.Ranges.First().From ?? 0;
            lock (Ranges) Ranges.Add(from);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(new SlowStream(content, (int)from, this)),
            };
            if (from > 0) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class SlowStream(byte[] data, int offset, FakeHub hub) : Stream
    {
        private int _position = offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (hub.Throttle) await Task.Delay(15, cancellationToken);
            var count = Math.Min(Math.Min(buffer.Length, hub.Throttle ? 64 * 1024 : 1 << 20), data.Length - _position);
            if (count <= 0) return 0;
            data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class ModelHubModeTests
{
    [Fact]
    public async Task ModelHubIsAModeWithItsOwnPlaceholderSessionAndMigration()
    {
        await using var environment = await TestEnvironment.CreateAsync();
        var chats = environment.Get<Missum.Core.Contracts.IChatRepository>();
        var hub = await chats.CreateSessionAsync("Model Hub", Missum.Core.Models.ChatMode.ModelHub);
        Assert.Equal([hub.Id], (await chats.ListSessionsAsync(Missum.Core.Models.ChatMode.ModelHub)).Select(item => item.Id));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(environment.Directory, "DatabaseBackups"), "*.pre-v61-*.bak"));
        Assert.True(await environment.Get<Missum.Core.Contracts.IMissumDatabase>().CheckIntegrityAsync());
    }

    [Theory]
    [InlineData("modelhub")]
    [InlineData("Model-Hub")]
    [InlineData("models")]
    public void WireNamesParseToModelHub(string value)
    {
        Assert.Equal(Missum.Core.Models.ChatMode.ModelHub, Missum.App.Services.AssistantCoordinator.ParseChatMode(value));
        // No chat: the hub is not a coding agent session.
        Assert.False(Missum.App.Services.AssistantCoordinator.IsCodingAgentSession(Missum.Core.Models.ChatMode.ModelHub));
    }
}

public sealed class LocalHubModelsTests
{
    [Fact]
    public void FindsModelsAlreadyInTheFolderAcrossSnapshots()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-local-hub-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Write(string path, int size)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[size]);
            }
            var hub = Path.Combine(root, "hub");
            Write(Path.Combine(hub, "models--unsloth--Old-GGUF", "snapshots", "aaa", "Q4", "m-Q4-00001-of-00002.gguf"), 10);
            Write(Path.Combine(hub, "models--unsloth--Old-GGUF", "snapshots", "aaa", "Q4", "m-Q4-00002-of-00002.gguf"), 20);
            Write(Path.Combine(hub, "models--unsloth--Old-GGUF", "snapshots", "aaa", ".download", "x.gguf"), 5);
            Write(Path.Combine(hub, "models--someone--Other-GGUF", "snapshots", "bbb", "o.gguf"), 5);
            Write(Path.Combine(hub, "models--unsloth--Empty-GGUF", "snapshots", "ccc", "README.md"), 5);

            // Picking the parent or the hub folder itself finds the same models.
            foreach (var folder in new[] { root, hub })
            {
                var local = Assert.Single(LocalHubModels.Scan(folder));
                Assert.Equal("unsloth/Old-GGUF", local.Id);
                Assert.Equal(2, local.Files.Count);
                var q4 = new HubQuantization("Q4", [new HubFile("Q4/m-Q4-00001-of-00002.gguf", 10, null), new HubFile("Q4/m-Q4-00002-of-00002.gguf", 20, null)], []);
                Assert.True(local.Has(q4));
                Assert.EndsWith("aaa", local.FolderOf(q4));
                // A newer commit with a different size of the same file is not the installed one.
                Assert.False(local.Has(new HubQuantization("Q4", [new HubFile("Q4/m-Q4-00001-of-00002.gguf", 11, null)], [])));
            }
            Assert.Empty(LocalHubModels.Scan(Path.Combine(root, "missing")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}

public sealed class ModelHubVersionTests
{
    private static readonly HubQuantization Q4 = new("Q4_K_M",
        [new HubFile("Q4/m-00001-of-00002.gguf", 100, "new1"), new HubFile("Q4/m-00002-of-00002.gguf", 50, "same2")],
        [new HubFile("mmproj-F16.gguf", 10, "proj")]);

    private static LocalHubRepo Local(string commit, params (string Path, long Size)[] files) =>
        new("unsloth/M-GGUF", DateTimeOffset.UtcNow, files.Select(f => new LocalHubFile(f.Path, f.Size, Path.Combine(@"C:\hub\models--unsloth--M-GGUF\snapshots", commit))).ToList());

    private static readonly (string, long)[] AllFiles = [("Q4/m-00001-of-00002.gguf", 100), ("Q4/m-00002-of-00002.gguf", 50), ("mmproj-F16.gguf", 10)];

    [Fact]
    public void NothingLocalMeansDownload() =>
        Assert.Equal(LocalQuantizationState.Missing, ModelHubVersions.Compare(Q4, "c2", null, new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>()).State);

    [Fact]
    public void TheCurrentSnapshotIsPresent() =>
        Assert.Equal(LocalQuantizationState.Current, ModelHubVersions.Compare(Q4, "c2", Local("c2", AllFiles), new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>()).State);

    [Fact]
    public void ASharedProjectorAloneIsNoCopy() =>
        Assert.Equal(LocalQuantizationState.Missing, ModelHubVersions.Compare(Q4, "c2", Local("c2", ("mmproj-BF16.gguf", 907), ("Q8/m-Q8.gguf", 300)),
            new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>()).State);

    [Fact]
    public void AnotherProjectorVariantCounts() =>
        // The list prefers mmproj-F16; a folder with mmproj-BF16 next to the model is just as complete.
        Assert.Equal(LocalQuantizationState.Current, ModelHubVersions.Compare(Q4, "c2",
            Local("c2", ("Q4/m-00001-of-00002.gguf", 100), ("Q4/m-00002-of-00002.gguf", 50), ("mmproj-BF16.gguf", 907)),
            new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>()).State);

    [Fact]
    public void AnOlderSnapshotWithTheSameChecksumsIsPresent()
    {
        var old = new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>
        {
            ["c1"] = new[] { new HubFile("Q4/m-00001-of-00002.gguf", 100, "new1"), new HubFile("Q4/m-00002-of-00002.gguf", 50, "same2"), new HubFile("mmproj-F16.gguf", 10, "proj") }
                .ToDictionary(f => f.Path),
        };
        Assert.Equal(LocalQuantizationState.Current, ModelHubVersions.Compare(Q4, "c2", Local("c1", AllFiles), old).State);
    }

    [Fact]
    public void AChangedFileOnHuggingFaceOffersAnUpdateThatReusesTheRest()
    {
        // The first part was re-quantized (same size, new checksum); the second part and the projector did not change.
        var old = new Dictionary<string, IReadOnlyDictionary<string, HubFile>?>
        {
            ["c1"] = new[] { new HubFile("Q4/m-00001-of-00002.gguf", 100, "old1"), new HubFile("Q4/m-00002-of-00002.gguf", 50, "same2"), new HubFile("mmproj-F16.gguf", 10, "proj") }
                .ToDictionary(f => f.Path),
        };
        var status = ModelHubVersions.Compare(Q4, "c2", Local("c1", AllFiles), old);
        Assert.Equal(LocalQuantizationState.Outdated, status.State);
        Assert.Equal(["Q4/m-00002-of-00002.gguf", "mmproj-F16.gguf"], status.Reusable.Keys.Order(StringComparer.Ordinal));
        Assert.EndsWith(@"c1\Q4\m-00002-of-00002.gguf", status.Reusable["Q4/m-00002-of-00002.gguf"]);
    }

    [Fact]
    public void AnIncompleteOrResizedCopyIsAnUpdate()
    {
        var unknown = new Dictionary<string, IReadOnlyDictionary<string, HubFile>?> { ["c1"] = null };
        // Unknown old commit (copied by hand, offline): equal sizes count as unchanged …
        Assert.Equal(LocalQuantizationState.Current, ModelHubVersions.Compare(Q4, "c2", Local("c1", AllFiles), unknown).State);
        // … but a missing part or another size is an update.
        Assert.Equal(LocalQuantizationState.Outdated, ModelHubVersions.Compare(Q4, "c2", Local("c1", ("Q4/m-00001-of-00002.gguf", 100)), unknown).State);
        Assert.Equal(LocalQuantizationState.Outdated, ModelHubVersions.Compare(Q4, "c2", Local("c1", ("Q4/m-00001-of-00002.gguf", 90), ("Q4/m-00002-of-00002.gguf", 50), ("mmproj-F16.gguf", 10)), unknown).State);
    }
}

public sealed class ModelHubSpaceTests
{
    [Fact]
    public void OnlyMissingBytesCountTowardsTheSpaceCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "missum-space-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string repo = "unsloth/S-GGUF";
            var quant = new HubQuantization("Q4", [new HubFile("a-00001-of-00003.gguf", 1000, null), new HubFile("a-00002-of-00003.gguf", 2000, null), new HubFile("a-00003-of-00003.gguf", 3000, null)],
                [new HubFile("mmproj-F16.gguf", 400, null)]);
            Assert.Equal(6400, ModelDownloadService.BytesToDownload(root, repo, "c", quant));
            // Part 1 is in place, part 2 is half downloaded, the projector is reused from an older snapshot.
            var done = ModelHubCatalogId.FilePath(root, repo, "c", "a-00001-of-00003.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(done)!);
            File.WriteAllBytes(done, new byte[1000]);
            var part = Path.Combine(ModelHubCatalogId.SnapshotFolder(root, repo, "c"), ".download", "a-00002-of-00003.gguf.part");
            Directory.CreateDirectory(Path.GetDirectoryName(part)!);
            File.WriteAllBytes(part, new byte[500]);
            var old = Path.Combine(root, "old-mmproj.gguf");
            File.WriteAllBytes(old, new byte[400]);
            Assert.Equal(1500 + 3000, ModelDownloadService.BytesToDownload(root, repo, "c", quant, new Dictionary<string, string> { ["mmproj-F16.gguf"] = old }));
            Assert.NotNull(Missum.App.Pages.NativeAssistantPage.HubFreeSpace(root));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
