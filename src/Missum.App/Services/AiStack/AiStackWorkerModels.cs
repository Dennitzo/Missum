using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Missum.App.Services.AiStack;

/// <summary>Optional parts of the local AI stack; the base services (proxy, gateway, search, media) are always installed.</summary>
[Flags]
public enum AiStackComponents
{
    None = 0,
    /// <summary>Transcription, speaker recognition and speech output (the "speech" worker).</summary>
    Speech = 1,
    /// <summary>Image generation (the "image" worker, compiles stable-diffusion.cpp with CUDA).</summary>
    Image = 2,
}

/// <summary>One pinned file of an offline worker model: source revision, target below the model root, length and SHA-256.</summary>
public sealed record AiStackWorkerModelFile(
    AiStackComponents Component, string Repository, string Revision, string SourcePath, string Destination, long Length, string Sha256);

/// <summary>
/// The offline worker models of the speech and image services, pinned exactly like
/// <c>windows/download-ai-models.ps1</c> (repository, commit, length, SHA-256). Every file is listed,
/// so the app downloads them itself, without a system Python or the Hugging Face CLI.
/// </summary>
public static class AiStackWorkerModels
{
    private const string Whisper = "Systran/faster-whisper-large-v3";
    private const string WhisperRevision = "edaa852ec7e145841d8ffdb056a99866b5f0a478";
    private const string Speaker = "speechbrain/spkrec-ecapa-voxceleb";
    private const string SpeakerRevision = "0f99f2d0ebe89ac095bcc5903c4dd8f72b367286";
    private const string Supertonic = "Supertone/supertonic-3";
    private const string SupertonicRevision = "3cadd1ee6394adea1bd021217a0e650ede09a323";

    public static IReadOnlyList<AiStackWorkerModelFile> Files { get; } =
    [
        WhisperFile("config.json", 2394, "a9306624f5ec14270a014b647e5c316b6e03a662c369758d1b90697a7b0655b9"),
        WhisperFile("preprocessor_config.json", 340, "7ccc62c6f2765af1f3b46c00c9b5894426835a05021c8b9c01eecb6dfb542711"),
        WhisperFile("tokenizer.json", 2480617, "6d8cbd7cd0d8d5815e478dac67b85a26bbe77c1f5e0c6d76d1ce2abc0e5f21ca"),
        WhisperFile("vocabulary.json", 1068114, "c69260f2ab26d659b7c398f9a2b2b48ed0df16c3b47d7326782fd9cba71690c1"),
        WhisperFile("model.bin", 3087284237, "69f74147e3334731bc3a76048724833325d2ec74642fb52620eda87352e3d4f1"),
        SpeakerFile("classifier.ckpt", 5534328, "fd9e3634fe68bd0a427c95e354c0c677374f62b3f434e45b78599950d860d535"),
        SpeakerFile("config.json", 51, "15ff7afad09adec0936c31493a7397c13f855914f4579a8393fe82649778a664"),
        SpeakerFile("embedding_model.ckpt", 83316686, "0575cb64845e6b9a10db9bcb74d5ac32b326b8dc90352671d345e2ee3d0126a2"),
        SpeakerFile("hyperparams.yaml", 1919, "6f78854fa04ba59e761437b76a2575d3aba5e5016de3e9b69f0c9a5077fb1a41"),
        SpeakerFile("label_encoder.txt", 128619, "e13c3a167bb4112685670ee896d20e2b565af16b3a4ceeaa8689fa4d22adb8b9"),
        SpeakerFile("mean_var_norm_emb.ckpt", 1921, "cd70225b05b37be64fc5a95e24395d804231d43f74b2e1e5a513db7b69b34c33"),
        SupertonicFile("onnx/duration_predictor.onnx", 3700147, "c3eb91414d5ff8a7a239b7fe9e34e7e2bf8a8140d8375ffb14718b1c639325db"),
        SupertonicFile("onnx/text_encoder.onnx", 36416150, "c7befd5ea8c3119769e8a6c1486c4edc6a3bc8365c67621c881bbb774b9902ff"),
        SupertonicFile("onnx/vector_estimator.onnx", 256534781, "883ac868ea0275ef0e991524dc64f16b3c0376efd7c320af6b53f5b780d7c61c"),
        SupertonicFile("onnx/vocoder.onnx", 101424195, "085de76dd8e8d5836d6ca66826601f615939218f90e519f70ee8a36ed2a4c4ba"),
        SupertonicFile("onnx/tts.json", 8253, "42078d3aef1cd43ab43021f3c54f47d2d75ceb4e75f627f118890128b06a0d09"),
        SupertonicFile("onnx/unicode_indexer.json", 277676, "9bf7346e43883a81f8645c81224f786d43c5b57f3641f6e7671a7d6c493cb24f"),
        SupertonicFile("voice_styles/F5.json", 291479, "45966e73316415626cf41a7d1c6f3b4c70dbc1ba2bee5c1978ef0ce33244fc8d"),
        SupertonicFile("LICENSE", 15007, "0d944a9110fed9a9602d60e0423a272903e7bd21ab060490774efc77c2275e9f"),
        new(AiStackComponents.Image, "leejet/Z-Image-Turbo-GGUF", "c61c0e422dc8b541b7548cf33a4ef8302b0f8085",
            "z_image_turbo-Q4_K.gguf", "image/z-image/z_image_turbo-Q4_K.gguf",
            3864250304, "14b375ab4f226bc5378f68f37e899ef3c2242b8541e61e2bc1aff40976086fbd"),
        new(AiStackComponents.Image, "Comfy-Org/z_image_turbo", "d24c4cf2a0cd98a42f23467e27e3d76ee9438b8e",
            "split_files/vae/ae.safetensors", "image/z-image/ae.safetensors",
            335304388, "afc8e28272cd15db3919bacdb6918ce9c1ed22e96cb12c4d5ed0fba823529e38"),
        new(AiStackComponents.Image, "unsloth/Qwen3-4B-Instruct-2507-GGUF", "a06e946bb6b655725eafa393f4a9745d460374c9",
            "Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "image/z-image/Qwen3-4B-Instruct-2507-Q4_K_M.gguf",
            2497281120, "3605803b982cb64aead44f6c1b2ae36e3acdb41d8e46c8a94c6533bc4c67e597"),
    ];

    public static IEnumerable<AiStackWorkerModelFile> For(AiStackComponents components) =>
        Files.Where(file => components.HasFlag(file.Component));

    public static long TotalBytes(AiStackComponents components) => For(components).Sum(static file => file.Length);

    /// <summary>The download URL of one pinned file (Hugging Face redirects it to its CDN).</summary>
    public static Uri ResolveUri(AiStackWorkerModelFile file) =>
        new($"https://huggingface.co/{file.Repository}/resolve/{file.Revision}/{string.Join('/', file.SourcePath.Split('/').Select(Uri.EscapeDataString))}");

    /// <summary>Absolute target path of a file; rejects anything that would leave the model root.</summary>
    public static string TargetPath(string modelRoot, AiStackWorkerModelFile file)
    {
        var root = Path.GetFullPath(modelRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(root, file.Destination.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Ein Worker-Modellpfad liegt außerhalb des Modellordners: " + file.Destination);
        return target;
    }

    private static AiStackWorkerModelFile WhisperFile(string name, long length, string sha256) =>
        new(AiStackComponents.Speech, Whisper, WhisperRevision, name, "speech/faster-whisper-large-v3/" + name, length, sha256);

    private static AiStackWorkerModelFile SpeakerFile(string name, long length, string sha256) =>
        new(AiStackComponents.Speech, Speaker, SpeakerRevision, name, "speech/spkrec-ecapa-voxceleb/" + name, length, sha256);

    private static AiStackWorkerModelFile SupertonicFile(string name, long length, string sha256) =>
        new(AiStackComponents.Speech, Supertonic, SupertonicRevision, name, "speech/supertonic-3/" + name, length, sha256);
}

/// <summary>
/// Downloads pinned worker model files: partial data lives in a staging folder named by the SHA-256,
/// is resumed with HTTP range requests, checked against length and hash and only then moved into place.
/// </summary>
public sealed class AiStackWorkerModelDownloader : IDisposable
{
    private const int Attempts = 8;
    private readonly HttpClient _http;

    public AiStackWorkerModelDownloader(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Missum",
            typeof(AiStackWorkerModelDownloader).Assembly.GetName().Version?.ToString(3) ?? "2.0"));
    }

    /// <summary>Ensures every file of the components exists with the pinned length and hash.</summary>
    public async Task EnsureAsync(string modelRoot, string stagingRoot, AiStackComponents components,
        IProgress<AiStackSetupProgress>? progress, CancellationToken cancellationToken)
    {
        var files = AiStackWorkerModels.For(components).ToArray();
        var total = files.Sum(static file => file.Length);
        long completed = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = AiStackWorkerModels.TargetPath(modelRoot, file);
            var done = completed;
            void Report(long bytes) => progress?.Report(new(AiStackSetupStage.Models,
                $"Lade {file.Destination} …", total == 0 ? null : (done + bytes) / (double)total));
            if (File.Exists(target) && new FileInfo(target).Length == file.Length
                && await HashMatchesAsync(target, file.Sha256, cancellationToken).ConfigureAwait(false))
            {
                completed += file.Length;
                Report(0);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(stagingRoot);
            var part = Path.Combine(stagingRoot, file.Sha256.ToLowerInvariant() + ".part");
            await DownloadAsync(file, part, Report, cancellationToken).ConfigureAwait(false);
            if (!await HashMatchesAsync(part, file.Sha256, cancellationToken).ConfigureAwait(false))
            {
                File.Delete(part);
                throw new InvalidDataException($"Die Prüfsumme von {file.Destination} stimmt nicht; die Datei wird beim nächsten Versuch neu geladen.");
            }
            File.Move(part, target, overwrite: true);
            completed += file.Length;
            Report(0);
        }
    }

    private async Task DownloadAsync(AiStackWorkerModelFile file, string part, Action<long> report, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (existing > file.Length) { File.Delete(part); existing = 0; }
            if (existing == file.Length) return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AiStackWorkerModels.ResolveUri(file));
                if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (existing > 0 && response.StatusCode != HttpStatusCode.PartialContent) existing = 0;
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = new FileStream(part, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                var buffer = new byte[1 << 20];
                var written = existing;
                var lastReport = Environment.TickCount64;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > file.Length) throw new InvalidDataException($"{file.Destination} ist größer als erwartet.");
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (Environment.TickCount64 - lastReport > 500) { report(written); lastReport = Environment.TickCount64; }
                }
                if (written == file.Length) return;
                throw new IOException($"Der Download von {file.Destination} brach nach {written} von {file.Length} Bytes ab.");
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < Attempts
                && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, attempt * 5)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<bool> HashMatchesAsync(string path, string sha256, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        return string.Equals(hash, sha256, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}
