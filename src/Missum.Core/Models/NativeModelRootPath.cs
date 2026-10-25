namespace Missum.Core.Models;

/// <summary>Validates a native model folder without probing local disks or network shares.</summary>
public static class NativeModelRootPath
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.Length > 32760 || path.Any(character => char.IsControl(character) || character is '"' or '<' or '>' or '|' or '*' or '?')
            || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Bitte einen vollständigen lokalen Ordnerpfad oder UNC-Pfad wie \\\\Server\\Freigabe\\Modelle eingeben.", nameof(value));
        path = Path.GetFullPath(path);
        var colon = path.IndexOf(':');
        if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(path[0]) || path.IndexOf(':', colon + 1) >= 0))
            throw new ArgumentException("Der AI-Modellordner enthält einen ungültigen Pfad.", nameof(value));
        if (path.StartsWith("\\\\", StringComparison.Ordinal) && path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2)
            throw new ArgumentException("Ein UNC-Pfad benötigt Server und Freigabe.", nameof(value));
        return Path.TrimEndingDirectorySeparator(path);
    }
}
