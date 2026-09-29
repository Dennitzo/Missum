namespace Missum.Ai.Server.Core.Policies;

/// <summary>Shared mathematical notation for native chat answers and research output.</summary>
public static class MathFormattingPolicy
{
    public const string Instructions = """
        Mathematische Darstellung im Chat:
        - Nutze für Formeln den gemeinsamen LaTeX-/KaTeX-Kern: inline $...$ oder \(...\), abgesetzt
          $$...$$ oder \[...\]. Setze längere Herleitungen in eigene Formelblöcke mit Leerzeilen davor und danach.
        - Setze zu rendernde Formeln nicht in Backticks oder Codeblöcke. Nur ausdrücklich angeforderter
          LaTeX-Quelltext gehört in einen Codeblock. Nutze Formeln auch in Tabellenzellen mit Inline-Trennzeichen.
        - Verwende Standardbefehle wie \frac{a}{b}, \sqrt{x}, x^{2}, x_{i}, \sum, \int, \cdot und
          \text{Text}. Nutze aligned für mehrzeilige Gleichungen und matrix, pmatrix oder bmatrix für Matrizen;
          trenne Spalten mit & und Zeilen mit \\. Halte Klammern und Anfangs-/Endbefehle vollständig und passend.
        - Erfinde keine LaTeX-Befehle. Nutze keine Dokumentpräambeln, Pakete, eigenen Makros, HTML-Befehle oder
          externen Ressourcen für Chat-Formeln. Vereinfache ungewöhnliche Notation zu Standardbefehlen.
        - Schreibe LaTeX-Befehle im sichtbaren Markdown mit genau einem Backslash. Verdopple sie dort nicht wie
          in einem JSON-String; zwei Backslashes bleiben ausschließlich echte Zeilentrenner einer Formel.
        - Nutze Dollarzeichen nicht als Formeltrenner für Geldbeträge: schreibe dafür die Währung aus oder USD.
          Erkläre Symbole und Einheiten soweit nötig. Mathematische Notation ergänzt eine verständliche Erklärung.
        """;
}
