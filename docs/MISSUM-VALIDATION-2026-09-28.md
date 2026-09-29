# Missum 2.0 – Prüfprotokoll vom 28. September 2026

Dieses Protokoll betrifft den aktuellen Missum-Quellstand. Ältere aus der Vorlage
übernommene Prüfberichte sind keine Abnahme dieser Version.

## Stand der Arbeiten

Die Arbeit läuft. Die abschließende Veröffentlichung und alle Live-Prüfungen sind
noch nicht abgeschlossen. Visuelle Abnahme ist derzeit durch die gespeicherte
Escape-Sperre der Desktop-Steuerung blockiert.

## Bereits nachgewiesen

- Stand einschließlich Reparaturen: 873 Client- und 746 Serverprüfungen bestanden
  (`artifacts-client-stage11.log`, `artifacts-server-stage11.log`). 192 Web-/LAN-
  Prüfungen und 78 native Python-Prüfungen bestanden.
- Echte native Inferenz: Qwen-Textantwort, rotes Testbild mit Vision erkannt und
  zwei nichtleere BGE-M3-Vektoren mit je 1024 Dimensionen nachgewiesen.
- Forschungssandbox: 28 Prüfungen bestanden, einschließlich realer Docker-Läufe
  für SMT, Python-Tests, drei Benchmark-Wiederholungen und Lean 4.30.0. Absichtlich
  fehlerhafte Programme und Beweise werden als fehlgeschlagen gemeldet.
- Echter Chat-PDF-Export: zwölf Seiten, 142.469 Bytes; Textmarker und Blobhash
  geprüft. SHA-256: `c412b227eaa64c62e3192b7386546f7fa723affbc32a30aaae2d5737609dd788`.
- Produkt, Namespaces, Dienste und Images auf Missum 2.0 umgestellt. Alte Bezeichner
  bleiben ausschließlich dort lesbar, wo vorhandene Daten und Caches migriert werden.
- Datenmigration am 28. September: 20 Serverläufe, 63.091 Ereignisse, 12 Coding-
  Sitzungskontexte und 11 Artefakte erhalten. SQLite-Snapshot mit Integritäts- und
  logischer Inhaltsprüfung; kopierte Dateien jeweils mit SHA-256 geprüft.
- Migrationsquellen bleiben erhalten. Quittung:
  `C:\ProgramData\Missum-AI-Stack\missum-migration.json`.
- Drei historische `WaitingForClient`-Läufe bleiben erhalten; sie wurden nicht
  pauschal gelöscht oder als abgeschlossene Läufe ausgegeben.
- Alle sechs Missum-Dienste sind gesund; die laufenden Images wurden mit den
  gebauten Tags verglichen (`artifacts-stack-smoke-stage10.log`).

## Im Test gefundene und behobene Fehler

- Qwens nativer Werkzeugparser verlor bei `coding.edit` wegen eines Root-`oneOf`
  die Parameter. Der erste echte Dateitest reproduzierte 111 leere Änderungsaufrufe
  in einem Modellturn und lief ins Zeitlimit. Das Transportschema wurde korrigiert;
  Hostvalidierung, Hashschutz und Originaljournal bleiben erhalten. Wiederholte
  ungültige Aufrufe werden begrenzt, ohne gültige Operationen zu entfernen. Die
  reale Modellwiederholung steht noch aus.
- Englische Live-Untertitel wurden erneut ins Deutsche übersetzt. Die deutsche
  Nachübersetzung gilt jetzt nur für den Transkriptionsmodus.
- Embeddings lieferten den Konfigurationsalias zurück, während der Client unter
  der installierten Modell-ID indizierte. Gateway-Antwort und Vektorindex verwenden
  jetzt durchgängig die tatsächlich vorbereitete Modell-ID. Audioanalyse beachtet
  die explizite Modellwahl. Sieben Serverregressionen dazu bestanden.
- Erster reparierter Coding-Livetest erfolgreich: echte Suche, Lesen, Schreiben,
  separater hashgeschützter Edit und Rücklesen (`run-893967f87098482e8bc5f5fb9de091d3`).
  Der zweite Test hatte die ausdrücklich benötigte Fähigkeit `coding.process`
  nicht gesendet. Dadurch fehlte der Testbefehl im Werkzeugkatalog; zugleich
  akzeptierte der Host identische Planupdates endlos. Testvertrag und begrenzte
  Behandlung solcher Plan-Wiederholungen werden korrigiert.
- Dateibasierte Transkription verwendete unter Linux `/data/Uploads` statt des
  tatsächlich gemounteten `/data/uploads`. Korrigiert; Worker-Neubau läuft.
- Defekte PNG-Prüfsummen lösten im Medienworker einen unbehandelten `SyntaxError`
  aus. Sie werden jetzt als ungültiges Bild gemeldet. Auch die tatsächlich defekte
  PNG-Livefixture wurde ersetzt. Ein eigener FFmpeg-Fixture-Zeitrahmen verhindert
  einen endlosen leeren Videotest. Fehlgeschlagene Medienanalysen dürfen nicht als
  `run.completed` gemeldet werden; der Serverpfad wird entsprechend korrigiert.
- Dokumentrevision und zugehöriges Artefakt werden atomar gespeichert; Abbruch,
  Importfehler, SQL-Fehler und Revisionskonflikte sind geprüft.
- Eingaben während eines Sitzungswechsels werden durch eine vollständige
  Composer-Sperre geschützt. Der Änderungs-Chip bleibt an Antwort und Run gebunden.
- XLSX/PPTX werden auch vom echten SQLite-Formatvertrag akzeptiert. Gemeinsame
  Formeln bleiben beim Import erhalten; unsichere Blattumbenennungen werden
  zurückgewiesen. Formeln werden dabei nicht als neu berechnet ausgegeben.

## Erste reale Science-Prüfung

`run-fac39e900fac44b6b7cf16646df46320` erreichte das 30-Minuten-Testlimit vor dem
vollständigen Abschluss. Der Bericht wurde danach noch gespeichert, bevor der
eigene Testauftrag explizit abgebrochen wurde. Kein bestandener End-to-End-Test:
`document.create`/`document.read`, fertiges Sitzungsartefakt und Wiederherstellung
dieses Ergebnisses stehen noch aus. Die Wiederholung hat 60 Minuten Prüfzeit und
ein Cleanup unabhängig vom lokalen Streamingstatus.

Der gesicherte Checkpoint verwendet `provider=searxng`, `isFallback=false` und vier
wirklich gelesene SQLite-Primärseiten. Alle acht Hauptbelege stimmen exakt mit
gespeicherten Originalausschnitten überein. Offen: Quellenbeleg zu Netzlaufwerken;
eine Backup-Einschränkung wurde zu allgemein formuliert. Trotz dieser Lücke setzte
die Graph-/Berichtslogik einen zu starken Abschlussstatus; diese Statuslogik wird
vor der Wiederholung korrigiert. Quittung:
`artifacts/research-quality/run-fac39e900fac44b6b7cf16646df46320.json`.

## Erste vollständige Gateway-Werkzeugrunde

Fünf von neun Bereichen bestanden: lokale SearXNG-Suche/Originalabruf,
modellgesteuertes `math.evaluate`, Hörbuchabschnitt/Sitzungsende, Live-Untertitel
mit englischer Übersetzung und Zustandsprüfung sowie echte Bildgenerierung.
Die generierte 512×512-PNG zeigt den angeforderten roten Apfel auf weißem Grund;
Hash, Metadaten, Dekodierung und fortgesetzter Download sind geprüft.
Vier Bereiche scheiterten an den oben beschriebenen Upload-/PNG-/Statusfehlern;
deren vollständige Wiederholung steht aus. Einzelquittungen und erzeugte Dateien:
`artifacts/validation/live-20260928-030243/gateway-tools-15e0f555862341d1947b927330087ca9/`.

## Geplante konkrete Abnahme

| Bereich | Prüfmethode | Status |
| --- | --- | --- |
| Office/PDF | Echte Dateien, Paketvalidierung, Reimport, Hash-/Versionsschutz | Office-Regression und Chat-PDF bestanden; direkter PDF-Auftrag noch ausstehend |
| Erweiterungen | Coordinator-Aktionskatalog und echter ExtensionHost | Testextension im realen Sidecar geprüft; Nutzerprofil enthält keine importierten Erweiterungen |
| Chat/Codex | Reale lokale Modelle, Streaming, Dateiänderung und unabhängiger Test | ausstehend |
| Claude Science | Reale SQLite-Recherche mit drei gelesenen Primärquellen, gespeicherter Bericht und Sitzungswechsel | ausstehend |
| Sprachausgabe | Reale Synthese und Wiedergabe, Pause/Fortsetzen, Streaming ohne Werkzeugtext | ausstehend |
| Medien | Bild/Audio/Video, Generierung, Artefakt-Hash und fortgesetzter Download | ausstehend |
| Forschungssandbox | Tatsächlicher Docker-Lauf, numerische/symbolische Rechnung, Netzsperre | bestanden, einschließlich erwarteter Fehlerfälle |
| Native Oberfläche | Maximieren, Hover, Popup, Scrollen, Forschungs-Tabs | Desktop-Steuerung gesperrt |
| Portable | Vollständiger Build, Manifest-/Dateihashes, isolierter Start | ausstehend |

Opt-in-Livetests zählen nur als ausgeführt, wenn ihre jeweilige Umgebungsvariable
aktiviert wurde und tatsächliche Lauf-/Artefaktnachweise vorliegen. Ein grüner
Standardtestlauf allein belegt diese externen Funktionen nicht.
