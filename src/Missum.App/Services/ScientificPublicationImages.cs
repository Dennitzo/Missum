using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Missum.Core.Research;

namespace Missum.App.Services;

/// <summary>Captures only explicitly referenced, current-run images from the owning research sandbox.</summary>
public static partial class ScientificPublicationImages
{
    private const int MaximumImageBytes = 8 * 1024 * 1024;
    internal const int MaximumTotalBytes = 128 * 1024 * 1024;
    internal const int MaximumImages = 64;

    public sealed record ImageArtifact(string RelativePath, string Sha256, ImmutableArray<byte> Bytes);

    public sealed record PreparedImages(string Markdown, ImmutableArray<ImageArtifact> Images)
    {
        public async Task WriteImagesAsync(string stagingDirectory, CancellationToken cancellationToken = default)
        {
            if (Images.IsDefaultOrEmpty) return;
            if (Images.Length > MaximumImages || Images.Sum(image => (long)image.Bytes.Length) > MaximumTotalBytes)
                throw new InvalidDataException("Die Publikationsabbildungen überschreiten die Grenze von 64 Bildern bzw. 128 MiB.");
            var root = Path.GetFullPath(stagingDirectory);
            Directory.CreateDirectory(root);
            var directory = ScientificSimulationService.SafePath(root, "figures");
            Directory.CreateDirectory(directory);
            foreach (var image in Images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Prepared bytes are immutable and copied from the source before fingerprinting;
                // later sandbox edits cannot silently change the PDF under that fingerprint.
                if (!OutputPath().IsMatch(image.RelativePath)) throw new InvalidDataException("Ungültiger Publikationsbildpfad.");
                var destination = ScientificSimulationService.SafePath(root, image.RelativePath);
                var bytes = image.Bytes.ToArray();
                if (bytes.Length > MaximumImageBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != image.Sha256)
                    throw new InvalidDataException("Die vorbereitete Publikationsabbildung ist ungültig.");
                await File.WriteAllBytesAsync(destination, bytes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public static Task<PreparedImages> PrepareAsync(string markdown, string projectId,
        IResearchSandboxService sandbox, DateTimeOffset? runStartedAt, CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(markdown, projectId, sandbox, runStartedAt, null, cancellationToken);

    internal static Task<PreparedImages> PrepareCanonicalAsync(string markdown, string projectId,
        IResearchSandboxService sandbox, IReadOnlyDictionary<string, string> measuredHashes,
        CancellationToken cancellationToken = default) =>
        PrepareCoreAsync(markdown, projectId, sandbox, DateTimeOffset.MinValue, measuredHashes, cancellationToken);

    private static async Task<PreparedImages> PrepareCoreAsync(string markdown, string projectId,
        IResearchSandboxService sandbox, DateTimeOffset? runStartedAt, IReadOnlyDictionary<string, string>? measuredHashes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(sandbox);
        var matches = ImageReference().Matches(markdown);
        if (matches.Count == 0) return new(markdown, []);
        var code = CodeRanges(markdown);
        var images = ImmutableArray.CreateBuilder<ImageArtifact>();
        var byPath = new Dictionary<string, ImageArtifact?>(StringComparer.OrdinalIgnoreCase);
        var byHash = new Dictionary<string, ImageArtifact>(StringComparer.Ordinal);
        var output = new StringBuilder(markdown.Length);
        ResearchSandboxLayout? layout = null;
        var layoutAttempted = false;
        var previous = 0;
        var totalBytes = 0;
        foreach (Match match in matches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Append(markdown, previous, match.Index - previous);
            previous = match.Index + match.Length;
            if (code.Any(range => match.Index >= range.Start && match.Index < range.End))
            {
                output.Append(match.Value); continue;
            }
            var reference = DecodeTarget(match.Groups["target"].Value);
            if (reference is not null && Uri.TryCreate(reference, UriKind.Absolute, out var remote)
                && remote.Scheme is "http" or "https")
            {
                output.Append(match.Value); continue;
            }
            ImageArtifact? image = null;
            string? capacityFailure = null;
            try
            {
                if (runStartedAt is not null && reference is not null && SafeIdentifier().IsMatch(projectId))
                {
                    if (!layoutAttempted)
                    {
                        layoutAttempted = true;
                        layout = await sandbox.EnsureProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
                        if (layout.ProjectId != projectId) layout = null;
                    }
                    if (layout is not null && ResolvePublicationImage(layout, reference, measuredHashes) is { } resolved)
                    {
                        var path = resolved.Path;
                        if (!byPath.TryGetValue(path, out image))
                        {
                            if (File.Exists(path) && new FileInfo(path).Length > MaximumImageBytes)
                                capacityFailure = "Eine Publikationsabbildung ist größer als 8 MiB. Komprimiere den Plot; keine erforderliche Abbildung wurde still ausgelassen.";
                            var loaded = await ReadImageAsync(path, runStartedAt.Value,
                                MaximumImageBytes, cancellationToken).ConfigureAwait(false);
                            if (loaded is not null && resolved.ExpectedHash is not null
                                && !loaded.Sha256.Equals(resolved.ExpectedHash, StringComparison.OrdinalIgnoreCase)) loaded = null;
                            if (loaded is not null)
                            {
                                if (!byHash.TryGetValue(loaded.Sha256, out image))
                                {
                                    if (images.Count >= MaximumImages || (long)totalBytes + loaded.Bytes.Length > MaximumTotalBytes)
                                        capacityFailure = "Die Publikationsabbildungen überschreiten die Grenze von 64 verschiedenen Bildern bzw. 128 MiB. Teile den Bildumfang fachlich auf; keine erforderliche Abbildung wurde still ausgelassen.";
                                    else
                                    {
                                        image = loaded; images.Add(image); byHash.Add(image.Sha256, image);
                                        totalBytes += image.Bytes.Length;
                                    }
                                }
                            }
                            byPath[path] = image;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                             or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // An unavailable or unsafe picture must never suppress the mandatory publication.
            }
            if (capacityFailure is not null) throw new ScientificPublicationContentException(capacityFailure);
            if (image is null) output.Append(UnavailableCaption(match.Groups["alt"].Value));
            else output.Append("![").Append(EscapeCaption(match.Groups["alt"].Value)).Append("](").Append(image.RelativePath).Append(')');
        }
        output.Append(markdown, previous, markdown.Length - previous);
        return new(output.ToString(), images.ToImmutable());
    }

    private static async Task<ImageArtifact?> ReadImageAsync(string path, DateTimeOffset runStartedAt,
        int remainingBytes, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.LastWriteTimeUtc < runStartedAt.UtcDateTime || info.Length is < 24 or > MaximumImageBytes
            || info.Length > remainingBytes) return null;
        var before = info.LastWriteTimeUtc;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
        var bytes = new byte[(int)info.Length];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        if (stream.ReadByte() != -1) return null;
        info.Refresh();
        if (info.LastWriteTimeUtc != before || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
        string extension;
        if (bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            if (!bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) || bytes.Length < 45
                || !bytes.AsSpan(bytes.Length - 12, 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 })) return null;
            var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
            var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            if (width == 0 || height == 0 || width > 16000 || height > 16000 || (ulong)width * height > 64_000_000) return null;
            extension = ".png";
        }
        else if (bytes[0] == 255 && bytes[1] == 216 && bytes[2] == 255 && bytes[^2] == 255 && bytes[^1] == 217)
            extension = ".jpg";
        else return null;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return new("figures/" + hash + extension, hash, ImmutableArray.CreateRange(bytes));
    }

    private static string? DecodeTarget(string value)
    {
        try
        {
            value = value.Trim();
            if (value.StartsWith('<') && value.EndsWith('>')) value = value[1..^1];
            value = MarkdownEscape().Replace(value, "$1");
            value = Uri.UnescapeDataString(value).Replace('\\', '/');
            return value.Length is > 0 and <= 4096 && !value.Any(char.IsControl) ? value : null;
        }
        catch (UriFormatException) { return null; }
    }

    private static string? RelativeArtifactPath(string reference)
    {
        if (reference.StartsWith("/sandbox/artifacts/", StringComparison.Ordinal)) reference = reference[19..];
        else if (reference.StartsWith("artifacts/", StringComparison.Ordinal)) reference = reference[10..];
        if (reference.StartsWith('/') || reference.Contains(':') || reference.Contains('?') || reference.Contains('#')) return null;
        var parts = reference.Split('/');
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.'))) return null;
        return Path.GetExtension(reference).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" ? reference : null;
    }

    private static (string Path, string? ExpectedHash)? ResolvePublicationImage(ResearchSandboxLayout layout,
        string reference, IReadOnlyDictionary<string, string>? measuredHashes)
    {
        if (reference.StartsWith("/sandbox/work/", StringComparison.Ordinal)) reference = reference[9..];
        if (reference.StartsWith("work/", StringComparison.Ordinal))
        {
            if (measuredHashes is null || !measuredHashes.TryGetValue(reference, out var expected)
                || expected.Length != 64 || !expected.All(char.IsAsciiHexDigit)) return null;
            var relative = reference[5..];
            if (RelativeArtifactPath(relative) != relative) return null;
            _ = ScientificSimulationService.SafePath(layout.RootPath, Path.GetRelativePath(layout.RootPath, layout.WorkPath));
            return (ScientificSimulationService.SafePath(layout.WorkPath, relative), expected);
        }
        if (RelativeArtifactPath(reference) is not { } artifact) return null;
        _ = ScientificSimulationService.SafePath(layout.RootPath, Path.GetRelativePath(layout.RootPath, layout.ArtifactsPath));
        var artifactKey = "artifacts/" + artifact;
        var expectedHash = measuredHashes is not null && measuredHashes.TryGetValue(artifactKey, out var hash) ? hash : null;
        return (ScientificSimulationService.SafePath(layout.ArtifactsPath, artifact), expectedHash);
    }

    private static string UnavailableCaption(string caption)
    {
        var label = caption.Trim();
        if (label.Length > 240) label = label[..240] + "…";
        return "*Abbildung" + (label.Length == 0 ? "" : ": " + EscapeCaption(label)) + " · lokale Bilddatei nicht verfügbar.*";
    }

    private static string EscapeCaption(string caption)
    {
        var label = MarkdownEscape().Replace(caption, "$1");
        return label.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private static List<(int Start, int End)> CodeRanges(string markdown)
    {
        var result = new List<(int, int)>();
        var fenceStart = -1;
        char fence = '\0'; var fenceLength = 0;
        var offset = 0;
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.TrimStart(' ');
            var indent = line.Length - text.Length;
            var markerLength = text.Length > 0 && text[0] is '`' or '~' ? text.TakeWhile(ch => ch == text[0]).Count() : 0;
            if (fenceStart >= 0)
            {
                if (indent <= 3 && markerLength >= fenceLength && text[0] == fence && string.IsNullOrWhiteSpace(text[markerLength..]))
                {
                    result.Add((fenceStart, offset + line.Length + 1)); fenceStart = -1;
                }
            }
            else if (indent <= 3 && markerLength >= 3)
            {
                fenceStart = offset; fence = text[0]; fenceLength = markerLength;
            }
            else
            {
                foreach (Match code in InlineCode().Matches(line)) result.Add((offset + code.Index, offset + code.Index + code.Length));
            }
            offset += line.Length + 1;
        }
        if (fenceStart >= 0) result.Add((fenceStart, markdown.Length));
        return result;
    }

    [GeneratedRegex(@"!\[(?<alt>(?:\\.|[^\]\\\r\n])*)\]\(\s*(?<target><[^>\r\n]+>|(?:\\.|[^\s()\\]|\([^()\r\n]*\))+)(?:\s+(?:""[^""\r\n]*""|'[^'\r\n]*'))?\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex ImageReference();
    [GeneratedRegex(@"\\([\\`*_{}\[\]()#+.!<> -])", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownEscape();
    [GeneratedRegex(@"(?<ticks>`+)(?<content>[^`\r\n]+)\k<ticks>", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCode();
    [GeneratedRegex(@"^figures/[a-f0-9]{64}\.(png|jpg)$", RegexOptions.CultureInvariant)]
    private static partial Regex OutputPath();
    [GeneratedRegex("^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeIdentifier();
}
