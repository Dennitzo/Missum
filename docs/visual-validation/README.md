# Visueller Abgleich – Missum / ChatGPT

[Aktuelle portable Missum-Oberfläche](missum-final.jpg)

Die Aufnahmen der laufenden Windows-Anwendungen sind unverändert. Die Referenz
stammt sowohl aus den bereitgestellten Bildern als auch aus direkten Aufnahmen der
ChatGPT-Anwendung. Inhalte und Sitzungen unterscheiden sich; ein globaler Pixel-Diff
wäre deshalb keine sinnvolle Messung der Oberflächenübereinstimmung.

## Etappen

| Etappe | Aufnahme | Befund / Korrektur |
| --- | --- | --- |
| Referenz | [Nutzervorlage](reference.png), [laufende ChatGPT-App](reference-live.jpg) | Dunkle native Fensterfläche, schmale Hauptnavigation, Chat-Seitenleiste, zentrale 736-Pixel-Spalte, Ausgaben rechts. |
| 1 | [Erster nativer Aufbau](missum-stage-1.jpg), [echte Chat-Antwort](missum-stage-1-chat.jpg) | WinUI-Renderer und echter Modellaufruf. Zentrierung und horizontales Clipping anschließend korrigiert. |
| 2 | [Zentrierte Oberfläche](missum-stage-2.jpg), [Werkzeuglauf](missum-stage-2-tools.jpg) | Echter Lese-/Schreibauftrag. Werkzeugnamen und große Standard-Expander wichen noch ab. |
| 3 | [Reasoning](missum-stage-3-reasoning.jpg), [Werkzeugliste](missum-stage-3-tools.jpg) | Funktionierende Auswahl; schmales Standardmenü und dünner Standard-Slider entsprachen noch nicht den Detailvorlagen. |
| 4 | [Neue ChatGPT-Aufnahme](reference-stage-4.jpg), [Modusmenü](missum-stage-4-mode.jpg), [Reasoning-Regler](missum-stage-4-reasoning.jpg), [Plus-Menü](missum-stage-4-tools.jpg) | Gleiche Grundgeometrie, blauer Reasoning-Regler und detaillierte Menüzeilen. Sichtbare Standardrahmen und WinUI-Breitenbegrenzung wurden im nächsten Schritt entfernt. |
| Streaming | [Modell laden](missum-stage-4-stream-a.jpg), [Token-Fortschritt](missum-stage-4-stream-b.jpg), [Antwort](missum-stage-4-stream-c.jpg) | Gleiche Position der Statuszeile und des Composers. Der noch sichtbare Spinner nach „Fertig“ führte zur Korrektur der verspäteten Status-Snapshots. |
| 5 | [Neue ChatGPT-Aufnahme](reference-stage-5.jpg), [breites Plus-Menü](missum-stage-5-tools.jpg), [Skizze](missum-stage-5-sketch.jpg), [Git-Diff](missum-stage-5-diff.jpg), [Antwortabschluss](missum-stage-5-completed.jpg) | Plus-Menü in voller Composer-Breite, native PNG-Skizze exportiert und angehängt, echte Dateiänderung farbig. Neuer echter Antwortlauf endet ohne weiterlaufenden Spinner. |
| 6 | [Aktuelle ChatGPT-App](reference-stage-6.jpg), [Kontext-Tooltip](missum-stage-6-tooltip.jpg), [äußerer Scrollbalken](missum-stage-6-scrollbar.jpg), [Änderungsanzeige](missum-stage-6-changes.jpg), [Tabs](missum-stage-6-tabs.jpg), [Modus](missum-stage-6-mode.jpg), [Reasoning](missum-stage-6-reasoning.jpg) | Umlaufender Rahmen, offene Ausgaben, Tooltip, Mikrofonabstand, transparente Eingabe, Projektchats und echte Tabs. Live-Schreibauftrag lieferte „1 Datei geändert +1 −1“. Tabwechsel stellte Diff und Zusammenfassung wieder her; Tab-Schließen ließ den Chat in der Seitenleiste erhalten. |

## Aktuelle Umsetzung (nach Etappe 6 weiterentwickelt)

- Native WinUI-3-Steuerelemente; die aktive Chat-Seite erstellt keine WebView.
- Hauptnavigation 52 px, Chat-Seitenleiste 288 px, Titelbereich 44 px.
- Chat in voller verfügbarer Breite; Eingabe zentriert und auf 900 px begrenzt.
  Eingabefeld Radius 22 px, Ausgaben als darüberliegendes Popup mit Radius 24 px.
- Header und linke Seitenleiste verwenden dieselbe dunkle Fläche. Eine vertikale
  1-Pixel-Linie trennt die Seitenleiste vom Assistenten.
- Der Chat-Scrollbalken liegt außen rechts. Mausrad,
  Ziehen und Tastatur greifen auf denselben Chat-Scrollzustand zu.
- Der Assistent hat einen umlaufenden, abgerundeten Rahmen. Ausgaben sind beim
  Start geöffnet; Modellwahl und Mikrofon haben einen zusätzlichen Abstand.
- Sitzungstabs ersetzen den einzelnen Titel. Plus erstellt einen Chat; ein Tab
  lässt sich wechseln oder schließen, ohne den gespeicherten Chat zu löschen.
- Projektchats stehen eingerückt unter ihrem Ordner, mit aktiver Markierung und
  Fortschrittskreis. Leere Projekte zeigen „Keine Chats“.
- Der Kontextkreis zeigt verwendete/maximale Tokens im Tooltip und kennzeichnet
  Schätzungen. Das Textfeld bleibt bei Hover und Fokus transparent.
- Über dem Eingabefeld stehen Werkzeug-, Ziel- und Änderungs-Chips nebeneinander.
  Der Änderungen-Chip zeigt Text und Zahlen ohne Symbol; Klick öffnet einen eigenen
  Tab mit sämtlichen gespeicherten Git-Diffs dieses Laufs.
- Streaming wird in 80-ms-Schritten zusammengefasst. Nachrichten, Textblöcke,
  Inline-Knoten und Werkzeugzeilen bleiben erhalten; nur veränderte Inhalte werden
  aktualisiert. Statushöhe und Kontextkreis bleiben konstant.
- Git-Diffs haben alte/neue Zeilennummern, grüne Ergänzungen und rote Entfernungen,
  auswählbaren Text und horizontales Scrollen. Das gilt auch für Diff-Codeblöcke.

## Funktionsgrenzen der lokalen Implementierung

Die Oberfläche nutzt die lokalen Missum-Dienste und Modelle. Ein ChatGPT-Konto
oder dort installierte Plugins werden dadurch nicht übernommen. Die vier Einträge
Documents, PDF, Spreadsheets und Presentations steuern die lokalen Dokumentwerkzeuge.
DOCX, XLSX und PPTX werden als native Office-Pakete erstellt, gelesen und versioniert.
Excel-Formeln werden beim Öffnen neu berechnet; ihre Ergebnisse werden hier nicht
als bereits berechnet dargestellt. Verfügbare externe Missum-Erweiterungen erscheinen
zusätzlich aus dem echten Aktionskatalog. Die Qualität erzeugter Office-Dateien
hängt vom gewählten Modell und den verfügbaren lokalen Werkzeugen ab.

„Ziel“ speichert einen Auftrag pro Chat und führt ihn in Folgeprompts mit; es startet
keinen zeitgesteuerten Hintergrundlauf. „Zeichnen“ öffnet eine native Zeichenfläche
und hängt die bestätigte PNG-Skizze an. „Projekt anhängen“ fügt Pfad und die erste
Verzeichnisebene als Projektkontext hinzu, nicht heimlich alle Dateiinhalte.

## Bereits geprüfte Laufzeitpfade

- Echter General-Chat mit sichtbarer Antwort und Wiederherstellung nach Neustart.
- Echter Codex-Auftrag: `sample.txt` gelesen, ausschließlich `result.txt` angelegt,
  anschließend den gespeicherten Inhalt erneut gelesen und bestätigt.
- Reasoning über den Slider verändert und nach Schließen wieder angezeigt.
- Neuer Antwortlauf mit laufender Kontext-/Tokenanzeige und vollständiger Antwort.
- Portabler Start in einem separaten Datenordner samt Hashprüfung, nativer Sitzung,
  SQLite und gebündelter Modellunterstützung bestanden. Der erste Prüflauf nutzte
  fälschlich einen nicht unterstützten Profilnamen; das Skript verwendet nun das
  unterstützte Stable-Profil mit weiterhin getrennten Testdaten.
- Separate Missum-Daten unter `%LOCALAPPDATA%\Missum`; das ursprüngliche Vorlagen-Repository blieb unverändert.

## Historischer Prüfstand bis Etappe 6 (28. September 2026)

- Client-Suite: **767 bestanden**, keine Fehler oder übersprungenen Tests.
- Server-Suite: **719 bestanden**, keine Fehler oder übersprungenen Tests.
- Echter aktueller Codex-Auftrag änderte ausschließlich `result.txt` im isolierten
  Testprojekt von „Native WinUI 3 Werkzeugtest erfolgreich“ zu „Native WinUI 3
  Diff-Prüfung erfolgreich“. Persistierte Git-Quittung: eine Ergänzung, eine Entfernung.
- Tooltip erschien sichtbar mit verwendetem/maximalem Kontext und Schätz-Hinweis.
- Ziehen des äußeren Scrollbalkens änderte den Chat-Offset von 1180 auf rund 485,
  während Ausgaben und Eingabe an ihrer Position blieben.
- Neuer Tab, Rückwechsel und Tab-Schließen wurden über die native UI ausgeführt.
- Die vorübergehend reduzierte Reasoning-Stufe wurde auf „Sehr hoch“ zurückgesetzt.
- Protokolle: `artifacts-client-final.log`, `artifacts-server-tests.log`,
  `artifacts-publish-final.log`; portable Datei unter `artifacts/portable/win-x64/`.
- Letzte portable Veröffentlichung samt erneuter Start- und Hashprüfung bestanden;
  die Anwendung wurde anschließend mit dem normalen Missum-Profil geöffnet.

Die Screenshots belegen Layoutzustände. Eine garantierte Bildrate oder vollständige
Funktionsgleichheit mit sämtlichen Cloud-Funktionen von ChatGPT wird daraus nicht
abgeleitet.

## Etappe 7 und aktueller Stand

Die [erneut aufgenommene Referenz](reference-stage-7.jpg) sowie die vom Nutzer
gelieferten Detailbilder wurden für die weiteren Korrekturen herangezogen. Der
Dateiänderungs-Tab wurde noch vor der letzten Layoutkorrektur in der laufenden
Missum-Oberfläche geöffnet. Ein abschließender Screenshot dieser Version fehlt:
Die Desktop-Steuerung meldet auch nach erneuter Freigabe weiterhin die zuvor
ausgelöste Escape-Sperre. Maximierung, Hover, das Ausgaben-Popup und die neue
Forschungsansicht gelten deshalb noch nicht als visuell abgenommen.

Der aktuelle Funktions- und Migrationsstand wird gesondert in
[MISSUM-VALIDATION-2026-09-28.md](../MISSUM-VALIDATION-2026-09-28.md) dokumentiert.
