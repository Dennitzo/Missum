using System.Text.RegularExpressions;

namespace Missum.Core.Coding;

/// <summary>
/// Shared source boundary for a kernel-checked, declarative Lean input. This is
/// deliberately narrower than arbitrary Lean programs: admissions, diagnostic
/// overrides, unchecked imports, compiler directives and metaprograms cannot
/// establish the application's KernelAccepted receipt.
/// </summary>
public static class LeanProofSourceContract
{
    public const string Version = "declarative-lean-audited-v2";
    private static readonly Regex ImportLine = new(@"(?m)^[ \t]*import[ \t]+([^\r\n]+)[ \t]*\r?$", RegexOptions.CultureInvariant);

    public static void Validate(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        // Inspect comments and strings too. A partial lexer must not pretend to
        // understand arbitrary Lean syntax or allow escaped compiler commands.
        if (source.Contains('#', StringComparison.Ordinal) || source.Contains('«') || source.Contains('»')) throw new InvalidDataException(
            "Die formale Prüfung akzeptiert deklarative Lean-Quellen ohne #-Direktiven.");
        // Only installed, audited standard-library modules may be imported. Project-local
        // imports could hide admissions or axioms behind a successful compiler exit code.
        source = ImportLine.Replace(source, match =>
        {
            var modules = match.Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (modules.Length == 0 || modules.Any(module => !Regex.IsMatch(module,
                @"^(?:Mathlib|Std|Init)(?:\.[A-Za-z0-9_]+)*$", RegexOptions.CultureInvariant)))
                throw new InvalidDataException("Die formale Prüfung akzeptiert deklarative Lean-Quellen nur mit Imports aus Mathlib, Std und Init; lokale und programmatische Imports sind ausgeschlossen.");
            return "";
        });
        var start = -1;
        for (var index = 0; index <= source.Length; index++)
        {
            var identifier = index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] == '_');
            if (identifier && start < 0) start = index;
            if (identifier || start < 0) continue;
            var token = source[start..index];
            start = -1;
            if (token is "sorry" or "sorryAx" or "admit" or "axiom" or "constant" or "set_option" or "import" or "prelude"
                or "run_cmd" or "run_tac" or "run_elab" or "elab" or "elab_rules" or "macro" or "macro_rules"
                or "syntax" or "initialize" or "builtin_initialize" or "attribute" or "IO" or "System" or "Lean"
                or "unsafe" or "extern" or "implemented_by" or "register_option" or "simproc" or "builtin_simproc"
                or "native_decide" or "ofReduceBool" or "ofReduceNat" or "export" or "notation" or "infix" or "infixl" or "infixr" or "prefix" or "postfix")
                throw new InvalidDataException($"Die formale Prüfung akzeptiert deklarative Lean-Quellen ohne '{token}', einschließlich Kommentaren und Zeichenketten. Beweislücken, Option-Overrides, ungeprüfte Imports und Metaprogrammierung sind ausgeschlossen.");
        }
    }
}
