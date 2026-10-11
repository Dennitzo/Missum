using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Missum.Core.Research;

/// <summary>Checks an explicit numerical acceptance contract against an already authenticated execution artifact.</summary>
public static class ScientificQuantitativeValidation
{
    public const string ArtifactPathPattern = @"^(?:work|artifacts)/(?:[A-Za-z0-9_-][A-Za-z0-9_.-]*/)*[A-Za-z0-9_-][A-Za-z0-9_.-]*\.json$";
    public const string MetricPattern = @"^[A-Za-z][A-Za-z0-9_.-]{0,127}$";
    private static readonly string[] DefinitionFields = ["observable", "reference", "artifactPath", "sampling", "checks"];
    private static readonly string[] CheckFields = ["metric", "kind", "maximum"];
    private static readonly string[] DescriptionFields = ["observable", "reference"];

    public static string? ValidateDefinition(JsonElement definition)
    {
        if (!HasExactly(definition, DefinitionFields))
            return "quantitativeValidation benötigt genau observable, reference, artifactPath, sampling und checks; unbekannte oder doppelte Felder entfernen.";
        foreach (var name in DescriptionFields)
            if (Text(definition, name) is not { Length: > 0 and <= 2000 } text || string.IsNullOrWhiteSpace(text))
                return $"quantitativeValidation.{name} muss die tatsächlich verglichene Messgröße bzw. Referenz als Text mit 1–2000 Zeichen beschreiben.";
        var path = Text(definition, "artifactPath");
        if (path.Length > 1024 || path.Any(char.IsControl) || !Regex.IsMatch(path, ArtifactPathPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            return "quantitativeValidation.artifactPath muss eine relative JSON-Ergebnisdatei unter work/ oder artifacts/ ohne Pfadwechsel bezeichnen, etwa work/validation.json.";
        if (definition.GetProperty("sampling").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return "quantitativeValidation.sampling muss true bei stichprobenabhängigen Messungen, sonst false sein.";
        var checks = definition.GetProperty("checks");
        if (checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() is < 2 or > 32)
            return "quantitativeValidation.checks benötigt 2–32 Kriterien, mindestens accuracy und convergence; bei sampling=true zusätzlich uncertainty.";
        var metrics = new HashSet<string>(StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in checks.EnumerateArray())
        {
            if (!HasExactly(check, CheckFields))
                return "Jedes quantitativeValidation.checks-Kriterium benötigt genau metric, kind und maximum.";
            var metric = Text(check, "metric");
            if (metric.Any(char.IsControl) || !Regex.IsMatch(metric, MetricPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) || !metrics.Add(metric))
                return "quantitativeValidation.checks[].metric benötigt eine eindeutige ASCII-Kennung (Buchstabe zuerst, danach Buchstaben, Ziffern, _, . oder -, höchstens 128 Zeichen). Für unterschiedliche Prüfarten getrennte Messwerte verwenden.";
            var kind = Text(check, "kind");
            if (kind is not ("accuracy" or "convergence" or "uncertainty"))
                return $"Prüfkriterium {metric}: kind muss accuracy, convergence oder uncertainty sein.";
            if (kind == "accuracy" && IsCorrelation(metric))
                return $"Prüfkriterium {metric}: Pearson/Korrelation belegt keine quantitative Genauigkeit. Unter accuracy einen tatsächlichen Fehler gegenüber der angegebenen Referenz messen, beispielsweise die Abweichung derselben normierten Verteilung.";
            if (!NonnegativeFinite(check.GetProperty("maximum"), out _))
                return $"Prüfkriterium {metric}: maximum muss eine endliche nichtnegative Fehlerschranke sein; keine Erfolgsmarkierung oder Zeichenfolge.";
            kinds.Add(kind);
        }
        if (!kinds.Contains("accuracy") || !kinds.Contains("convergence")
            || definition.GetProperty("sampling").GetBoolean() && !kinds.Contains("uncertainty"))
            return "quantitativeValidation.checks muss accuracy und convergence sowie bei sampling=true uncertainty abdecken. Genauigkeitsfehler, Verfeinerungsfehler und gegebenenfalls Stichprobenunsicherheit getrennt messen.";
        return null;
    }

    /// <summary>The caller must first verify the artifact's project, execution identity and output hash.</summary>
    public static IReadOnlyList<string> Assess(JsonElement definition, JsonElement measuredArtifact)
    {
        if (ValidateDefinition(definition) is { } invalid) return [invalid];
        foreach (var field in DescriptionFields)
            if (measuredArtifact.ValueKind != JsonValueKind.Object
                || measuredArtifact.EnumerateObject().Count(property => property.Name == field) != 1
                || !measuredArtifact.TryGetProperty(field, out var identity) || identity.ValueKind != JsonValueKind.String
                || !string.Equals(identity.GetString(), definition.GetProperty(field).GetString(), StringComparison.Ordinal))
                return [$"Die Messdatei benötigt {field} als eindeutigen String, der exakt quantitativeValidation.{field} entspricht. Messwerte einer anderen Messgröße oder Referenz dürfen nicht wiederverwendet werden; den richtigen Vergleich ausführen und das zugehörige Ergebnis speichern."];
        if (measuredArtifact.ValueKind != JsonValueKind.Object
            || measuredArtifact.EnumerateObject().Count(item => item.Name == "metrics") != 1
            || !measuredArtifact.TryGetProperty("metrics", out var values) || values.ValueKind != JsonValueKind.Object)
            return ["Die ausgeführte JSON-Ergebnisdatei benötigt genau ein flaches metrics-Objekt mit endlichen nichtnegativen Messwerten; den Prüfcode korrigieren und erneut ausführen."];
        var errors = new List<string>();
        var measurements = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var value in values.EnumerateObject())
        {
            if (!NonnegativeFinite(value.Value, out var number))
                errors.Add($"Messwert metrics.{value.Name} ist keine endliche nichtnegative Zahl. Fehlgeschlagene oder undefinierte Auswertungen dürfen nicht als bestanden gelten.");
            else if (!measurements.TryAdd(value.Name, number))
                errors.Add($"Messwert metrics.{value.Name} ist doppelt angegeben. Einen eindeutigen gemessenen Wert speichern und die Prüfung erneut ausführen.");
        }
        foreach (var check in definition.GetProperty("checks").EnumerateArray())
        {
            var metric = Text(check, "metric");
            if (!measurements.TryGetValue(metric, out var value))
            {
                errors.Add($"Messwert metrics.{metric} fehlt oder ist ungültig. Das bestehende {Text(check, "kind")}-Kriterium im Prüfcode tatsächlich auswerten; die Grenzwerte nicht abschwächen.");
                continue;
            }
            var maximum = check.GetProperty("maximum").GetDouble();
            if (value > maximum)
                errors.Add($"{Text(check, "kind")}-Kriterium {metric} nicht erreicht: gemessen {value.ToString("G", CultureInfo.InvariantCulture)}, zulässig höchstens {maximum.ToString("G", CultureInfo.InvariantCulture)}. Ursache und nächste Verfeinerung untersuchen; das Ziel bleibt offen.");
        }
        return errors;
    }

    private static bool HasExactly(JsonElement value, string[] fields) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Count() == fields.Length
        && fields.All(field => value.EnumerateObject().Count(property => property.Name == field) == 1);
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static bool NonnegativeFinite(JsonElement value, out double number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number) && double.IsFinite(number) && number >= 0;
    }
    private static bool IsCorrelation(string metric) => metric.Contains("pearson", StringComparison.OrdinalIgnoreCase)
        || metric.Contains("correlation", StringComparison.OrdinalIgnoreCase) || metric.Contains("korrelation", StringComparison.OrdinalIgnoreCase);
}
