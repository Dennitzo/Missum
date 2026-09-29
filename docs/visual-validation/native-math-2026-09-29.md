# Native Mathematik im Chat – 29.09.2026

Missum rendert die gemeinsame LaTeX-/KaTeX-Grundnotation jetzt in nativen WinUI-Controls. Die Umsetzung verwendet CSharpMath/SkiaSharp, keine WebView und keine JavaScript-KaTeX-Engine. Spezielle KaTeX-Erweiterungen sind daher nicht vollständig abgedeckt; nicht unterstützte Eingaben bleiben als kopierbarer LaTeX-Quelltext sichtbar.

## Verhalten

- Inline: `$...$`, `\(...\)`; Block: `$$...$$`, `\[...\]`.
- Brüche, Wurzeln, Summen, Integrale, griechische Zeichen, Gleichungssysteme und Matrizen.
- Gemeinsamer Renderer für Benutzer-/AI-Nachrichten, Denkprozesse, Markdown-Werkzeugdetails und Forschungsberichte.
- Codebeispiele und unvollständige Streaming-Formeln bleiben literal; unveränderte Formeln behalten ihr natives Element. Der Akzentcursor bleibt erhalten.
- Fettschrift und Links bleiben um Inline-Formeln erhalten. WinUI erlaubt kein InlineUIContainer innerhalb Hyperlink; die native Formel-Schaltfläche übernimmt deshalb den Link selbst.
- Formeln lassen sich als ursprüngliches LaTeX kopieren (bei verlinkten Formeln über das Kontextmenü).
- Modellanweisungen für General, Coding und wissenschaftliche Recherche sind vereinheitlicht. Fortgesetzte Coding-Sitzungen erhalten sie einmalig; Hörbuch und interne Kontextkomprimierung bleiben ausgenommen.

## Validierung

| Prüfung | Ergebnis |
|---|---|
| NativeMath-Parser-/Bitmaptests | 57 bestanden |
| Mathematikrichtlinien, Coding-Narration, Checkpoint-/Protokolltests | 89 bestanden |
| Nativer WinUI-Smoke | bestanden: Streaming, Cursor, Elementwiederverwendung, Codegrenzen, Bold, Links, ungültige Formeln, Inline und Block |
| Echter DeepSeek-General-Lauf | abgeschlossen, Reasoning `none`, keine Werkzeuge, 25,933 Sekunden, 2.787 Eingabe-/123 Ausgabetokens |
| Darstellung der unveränderten Modellantwort im Portable-Smoke | alle drei Formeln erfolgreich gesetzt |
| `windows/build.ps1 -Configuration Release -SkipTests` | erfolgreich; zielgerichtete Tests zuvor separat ausgeführt |
| SingleFile, Manifest, isolierter Profilstart und native Bibliotheken | bestanden |
| Gateway | neues Image aktiv, healthy/ready; vorhandene wartende Runs erhalten |

Der Smoke wurde um native Formelprüfungen und RenderTargetBitmap-Vorschauen ergänzt. Diese Bilder zeigen tatsächlich gerenderte WinUI-Controls mit transparentem Hintergrund, keine Mockups und keine Aufnahme des gesamten Fensters:

- [Native Grundfälle](../../artifacts/portable/win-x64.math-preview.png)
- [Echte DeepSeek-Antwort](../../artifacts/portable/win-x64.math-live-preview.png)
- [Live-Lauf und Metriken](../../artifacts/native-math-live.report.json)
- [Portable-Buildprotokoll](../../artifacts/native-math-portable.log)

Portable: `artifacts/portable/win-x64/Missum.exe` mit dem zugehörigen `ExtensionHost`-Ordner.

SHA-256: `a989560ce9612db0feefb6cab6a160a5ea0e9eabe4dbbae50f10c7eecae04a9a`.

Die neue Grafikabhängigkeit liefert native Debugsymbole mit. Der Portable-Publish entfernt PDB-Einträge vor dem Bundling; die strenge Prüfung gegen zusätzliche Dateien bleibt erhalten. Bibliotheks- und eingebettete Font-Lizenzen werden unter `Assets/Notices` mitgeliefert.
