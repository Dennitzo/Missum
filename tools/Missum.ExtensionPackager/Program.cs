using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Missum.Core.Extensions;
using Missum.Infrastructure.Extensions;

namespace Missum.ExtensionPackager;

/// <summary>
/// Builds and checks <c>.aext</c> extension packages with the very rules the app applies later:
/// the manifest is validated by <see cref="ExtensionManifestValidator"/>, the signed content hash is
/// taken from <see cref="AextPackageInspector"/> instead of being reimplemented here, and
/// <c>verify</c> runs the same signature verifier the desktop app uses. That keeps a package built on
/// a development machine identical in judgement to a package the app will accept or reject.
///
/// <code>
/// Missum.ExtensionPackager key    --keys &lt;directory&gt; --key-id &lt;id&gt;
/// Missum.ExtensionPackager pack   --manifest &lt;extension.json&gt; --content &lt;directory&gt;
///                                 --out &lt;package.aext&gt; --private-key &lt;pem&gt; --key-id &lt;id&gt;
/// Missum.ExtensionPackager verify --package &lt;package.aext&gt; --trusted-keys &lt;directory&gt;
/// </code>
/// </summary>
internal static class Program
{
    private const string Algorithm = "ecdsa-p256-sha256";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Verwendung: key | pack | verify  (siehe --hilfe)");
            return 2;
        }

        if (args.Any(static argument => argument is "--hilfe" or "--help"))
        {
            Console.WriteLine(Usage());
            return 0;
        }

        var options = ParseOptions(args[1..]);
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "key" => CreateKey(options),
                "pack" => await PackAsync(options).ConfigureAwait(false),
                "verify" => await VerifyAsync(options).ConfigureAwait(false),
                _ => Fail($"Unbekannter Befehl '{args[0]}'. Erlaubt sind key, pack und verify."),
            };
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Fehler: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Creates an ECDSA P-256 signing key pair. The private part stays on the build machine; the
    /// public part is written as a trusted key file that the app reads from
    /// <c>&lt;data directory&gt;\Extensions\trusted-keys</c>.
    /// </summary>
    private static int CreateKey(Dictionary<string, string> options)
    {
        var directory = Path.GetFullPath(Required(options, "keys"));
        var keyId = Required(options, "key-id");
        Directory.CreateDirectory(directory);
        var privatePath = Path.Combine(directory, keyId + ".private.pem");
        var trustedPath = Path.Combine(directory, keyId + ".json");
        if (File.Exists(privatePath) && !options.ContainsKey("force"))
        {
            Console.WriteLine($"schlüssel vorhanden={privatePath}");
            return 0;
        }

        // NamedCurves.nistP256 is the portable spelling; "prime256v1" is rejected by Windows CNG here.
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(privatePath, ecdsa.ExportPkcs8PrivateKeyPem());
        var trusted = new TrustedAextSigningKey(keyId, Algorithm, ecdsa.ExportSubjectPublicKeyInfoPem());
        File.WriteAllText(trustedPath, JsonSerializer.Serialize(trusted, Json));
        Console.WriteLine($"signierschlüssel={privatePath}");
        Console.WriteLine($"vertrauensschlüssel={trustedPath}");
        return 0;
    }

    /// <summary>
    /// Packs the manifest plus a content directory into a signed package. The content hash that gets
    /// signed is the one the store will recompute when it extracts and later re-verifies the content.
    /// </summary>
    private static async Task<int> PackAsync(Dictionary<string, string> options)
    {
        var manifestPath = Path.GetFullPath(Required(options, "manifest"));
        var contentDirectory = Path.GetFullPath(Required(options, "content"));
        var packagePath = Path.GetFullPath(Required(options, "out"));
        var privateKeyPath = Path.GetFullPath(Required(options, "private-key"));
        var keyId = Required(options, "key-id");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException("Die Manifestdatei wurde nicht gefunden.", manifestPath);
        if (!Directory.Exists(contentDirectory))
            throw new DirectoryNotFoundException($"Der Inhaltsordner fehlt: {contentDirectory}");
        if (!File.Exists(privateKeyPath))
            throw new FileNotFoundException("Der Signierschlüssel wurde nicht gefunden.", privateKeyPath);

        Directory.CreateDirectory(Path.GetDirectoryName(packagePath)
            ?? throw new InvalidOperationException("Der Zielpfad des Pakets besitzt kein Verzeichnis."));
        if (File.Exists(packagePath)) File.Delete(packagePath);

        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(manifestPath, ExtensionPackageFormat.ManifestEntryName, CompressionLevel.Optimal);
            foreach (var file in Directory.EnumerateFiles(contentDirectory, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(contentDirectory, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                // The manifest is taken from --manifest, and a signature is never part of its own hash.
                if (string.Equals(relative, ExtensionPackageFormat.ManifestEntryName, StringComparison.Ordinal)
                    || string.Equals(relative, ExtensionPackageFormat.SignatureEntryName, StringComparison.Ordinal))
                    continue;
                archive.CreateEntryFromFile(file, relative, CompressionLevel.Optimal);
            }
        }

        var unsigned = await new AextPackageInspector()
            .InspectAsync(packagePath, CancellationToken.None)
            .ConfigureAwait(false);
        var signature = Sign(Convert.FromHexString(unsigned.ContentSha256), privateKeyPath);
        using (var archive = ZipFile.Open(packagePath, ZipArchiveMode.Update))
        {
            var envelope = new AextSignatureEnvelope(Algorithm, keyId, unsigned.ContentSha256, signature);
            await using var stream = archive
                .CreateEntry(ExtensionPackageFormat.SignatureEntryName, CompressionLevel.Optimal)
                .Open();
            await JsonSerializer.SerializeAsync(stream, envelope, Json, CancellationToken.None).ConfigureAwait(false);
        }

        var packed = await new AextPackageInspector()
            .InspectAsync(packagePath, CancellationToken.None)
            .ConfigureAwait(false);
        Describe(packed);

        // The product inspector refuses a package it cannot verify, so the finished package is checked
        // here against the public key that belongs to the signing key. A package the app would reject
        // later is never reported as ready.
        var trustedKeys = options.TryGetValue("trusted-keys", out var keyDirectory) && !string.IsNullOrWhiteSpace(keyDirectory)
            ? Path.GetFullPath(keyDirectory)
            : Path.GetDirectoryName(privateKeyPath)!;
        if (!Directory.EnumerateFiles(trustedKeys, "*.json").Any())
        {
            Console.WriteLine($"signatur=Ungeprueft (kein Vertrauenschluessel in {trustedKeys})");
            return 0;
        }

        var withTrustedKey = await new AextPackageInspector(new DirectoryAextSignatureVerifier(trustedKeys))
            .InspectAsync(packagePath, CancellationToken.None)
            .ConfigureAwait(false);
        Console.WriteLine("signatur=" + withTrustedKey.SignatureVerification.Status);
        if (withTrustedKey.SignatureVerification.Reason is { Length: > 0 } declineReason)
            Console.WriteLine($"grund={declineReason}");
        return withTrustedKey.SignatureVerification.Status == AextSignatureStatus.Verified ? 0 : 1;
    }

    /// <summary>
    /// Runs the app's own inspection over a built package, including signature verification against a
    /// directory of trusted keys. Exit code 0 means the app would accept exactly this package.
    /// </summary>
    private static async Task<int> VerifyAsync(Dictionary<string, string> options)
    {
        var packagePath = Path.GetFullPath(Required(options, "package"));
        var trustedKeys = Path.GetFullPath(Required(options, "trusted-keys"));
        var inspector = new AextPackageInspector(new DirectoryAextSignatureVerifier(trustedKeys));
        var inspection = await inspector.InspectAsync(packagePath, CancellationToken.None).ConfigureAwait(false);
        Describe(inspection);
        Console.WriteLine("signatur=" + inspection.SignatureVerification.Status
            + (inspection.SignatureVerification.Reason is { Length: > 0 } reason ? $" grund={reason}" : string.Empty));
        return inspection.SignatureVerification.Status == AextSignatureStatus.Verified ? 0 : 1;
    }

    private static void Describe(AextPackageInspection inspection)
    {
        Console.WriteLine($"paket={inspection.PackagePath}");
        Console.WriteLine($"extension={inspection.Manifest.Id} version={inspection.Manifest.Version}");
        Console.WriteLine($"aktionen={Number(inspection.Manifest.Actions.Length)} werkzeuge={Number(inspection.Manifest.Tools.Length)}");
        foreach (var group in inspection.Manifest.Groups ?? [])
            Console.WriteLine($"gruppe={group.GroupId} label={group.Label} order={Number(group.GroupOrder)}");
        foreach (var action in inspection.Manifest.Actions)
        {
            Console.WriteLine($"aktion={action.ActionId} gruppe={action.GroupId} modi={string.Join('+', action.SupportedChatModes)}");
            Console.WriteLine($"  werkzeuge={string.Join(", ", inspection.Manifest.Tools
                .Where(tool => string.Equals(tool.ActionId, action.ActionId, StringComparison.Ordinal))
                .Select(tool => tool.ModelToolName))}");
        }

        foreach (var entrypoint in inspection.Manifest.Entrypoints)
            Console.WriteLine($"einstieg={entrypoint.Kind} pfad={entrypoint.Path}");
        Console.WriteLine($"berechtigungen={string.Join('+', inspection.Manifest.Permissions)}");
        Console.WriteLine($"eintraege={Number(inspection.Entries.Count)} byte={Number(inspection.PackageLength)}");
        Console.WriteLine($"inhalt-hash={inspection.ContentSha256}");
        Console.WriteLine($"paket-hash={inspection.PackageSha256}");
    }

    private static string Sign(byte[] digest, string privateKeyPath)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        if (ecdsa.KeySize != 256)
            throw new InvalidOperationException("Der Signierschlüssel muss ein ECDSA-Schlüssel mit P-256 sein.");
        // Same shape the verifier expects: the signature covers the content hash itself, not a
        // second hash of it, and uses the concatenated IEEE format rather than DER.
        return Convert.ToBase64String(ecdsa.SignHash(digest));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) continue;
            var name = args[index][2..];
            if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                values[name] = args[index + 1];
                index++;
            }
            else values[name] = "true";
        }

        return values;
    }

    private static string Required(Dictionary<string, string> options, string name) =>
        options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Die Option --{name} fehlt.");

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static string Usage() => string.Join(Environment.NewLine,
        "key    --keys <ordner> --key-id <id> [--force]",
        "pack   --manifest <extension.json> --content <ordner> --out <paket.aext> --private-key <pem> --key-id <id> [--trusted-keys <ordner>]",
        "verify --package <paket.aext> --trusted-keys <ordner>");
}
