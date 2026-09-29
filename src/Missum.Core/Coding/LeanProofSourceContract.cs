namespace Missum.Core.Coding;

/// <summary>
/// Shared source boundary for a kernel-checked, declarative Lean input. This is
/// deliberately narrower than arbitrary Lean programs: admissions, diagnostic
/// overrides, imported assumptions, compiler directives and metaprograms cannot
/// establish the application's KernelAccepted receipt.
/// </summary>
public static class LeanProofSourceContract
{
    public static void Validate(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        // Inspect comments and strings too. A partial lexer must not pretend to
        // understand arbitrary Lean syntax or allow escaped compiler commands.
        if (source.Contains('#', StringComparison.Ordinal)) throw new InvalidDataException(
            "Die formale Prüfung akzeptiert deklarative Lean-Quellen ohne #-Direktiven.");
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
                or "unsafe" or "extern" or "implemented_by" or "register_option" or "simproc" or "builtin_simproc")
                throw new InvalidDataException($"Die formale Prüfung akzeptiert deklarative Lean-Quellen ohne '{token}', einschließlich Kommentaren und Zeichenketten. Beweislücken, Option-Overrides, Imports und Metaprogrammierung sind ausgeschlossen.");
        }
    }
}
