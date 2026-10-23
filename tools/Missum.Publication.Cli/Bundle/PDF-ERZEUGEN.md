# Publikation als PDF erzeugen

Die bearbeitbare Quelle ist **`Publikation.md` im gleichen Ordner wie das Skript**.
Die PDF wird mit Missums originalem wissenschaftlichen Layout, Markdown-Renderer,
KaTeX 0.16.10 und Schriftdateien erzeugt: eine Spalte, Inhaltsverzeichnis,
Kapitelnummerierung, PDF-Lesezeichen und lokale Abbildungen wie in Missum.

## Ausführen

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publikation-erzeugen.ps1
```

Nur Formeln, Überschriften und Bilder prüfen, ohne die PDF zu ersetzen:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Publikation-erzeugen.ps1 -CheckOnly
```

Das Skript verwendet seinen eigenen Ordner; es kann auch aus einem anderen
Arbeitsverzeichnis über seinen vollständigen Pfad aufgerufen werden. Optional
ist ein anderer Projektordner über `-ProjectPath "C:\Pfad\Projekt"` möglich.

## Rückmeldungen und Reparaturen durch die lokale AI

- Fortschritt und Renderer-Ausgaben erscheinen direkt im Werkzeugergebnis
  und in `publication-build.log`.
- `publication-build.json` enthält das abschließende Ergebnis, Status, Exit-Code,
  Prüfzahlen und Fehler. Die Fehlerfelder enthalten, soweit zuordenbar, die
  Originalformel beziehungsweise Überschrift, den Grund, Abschnitt und Fundstellen
  mit Zeilennummern in `Publikation.md`.
- Bei einem Fehler diese Fundstellen in **`Publikation.md`** bearbeiten und den
  gleichen Befehl erneut ausführen. Keine Formeln still auslassen oder durch
  Platzhalter ersetzen. Dateifehler durch Korrektur des Bildpfads beziehungsweise
  der referenzierten Datei beheben.
- Lokale PNG-/JPEG-Abbildungen werden relativ zur Publikation aufgelöst und
  eingebettet. Wie in Missum gelten bis zu 8 MiB pro Bild, 64 verschiedene Bilder
  und 128 MiB insgesamt. Fehlende Bilder erzeugen einen Fehler.
- Der Renderer bearbeitet das Manuskript nicht. Bei einem Fehler bleibt die
  bisherige PDF erhalten. Ändern sich Quellen während der Erzeugung, wird keine
  veraltete PDF übernommen.
- Ein Lauf benötigt kein AI-Modell und hat kein künstliches Zeitlimit.
  Unveränderte, bereits erfolgreich erzeugte Eingaben werden wiederverwendet.

Exit-Codes: **0** erfolgreich, **2** Manuskript-/Satzfehler, **3** fehlende
Abhängigkeit oder technischer Fehler, **4** Quellen während des Laufs geändert.

## In einem anderen Projekt verwenden

`Publikation-erzeugen.ps1` und den gesamten Ordner **`.publication-renderer`**
zusammen kopieren; dort die eigene `Publikation.md` ablegen. Die Namen des
Projekts und dessen übergeordneter Ordner spielen keine Rolle.

Das mitgelieferte Programm ist eigenständig für Windows x64. Benötigt werden
Windows PowerShell 5.1 und Microsoft Edge. Missum muss nicht gestartet sein.
Bei einer abweichenden Edge-Installation kann `MISSUM_EDGE_PATH` auf die
vorhandene `msedge.exe` gesetzt werden. Kein Download während der Erzeugung.
Externe Links bleiben Literaturverweise; online verfügbare Bilder sollten für
eine reproduzierbare Publikation als lokale PNG-/JPEG-Dateien vorliegen.

Die Satzregeln stammen unverändert aus Missum. Browser-PDFs enthalten bei einem
neuen Satz technische Zeitmetadaten; bei unveränderten Eingaben wird die vorhandene,
geprüfte PDF bytegleich beibehalten. Mitgelieferte Renderer-Dateien zusammen
aktualisieren, wenn später eine andere Missum-Formatversion verwendet werden soll.
