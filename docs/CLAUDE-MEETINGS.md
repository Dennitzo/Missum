# Claude Meetings

Claude Meetings ist der vierte Modus neben ChatGPT, Codex und Missum Science. Eine Meetings-Sitzung ist ein Live-Meeting mit Kameras, globalem Chat, gemeinsamen Notizen und einem Whiteboard. Missum nimmt als eigener Teilnehmer teil: Es hört über das Transkript mit, liest den Chat und bereitet beides laufend in Notizen und Whiteboard auf.

## Bedienung

| Ort | Inhalt |
| --- | --- |
| Linke Seitenleiste | Projekte und Sitzungen wie in den anderen Modi |
| Tab **Meeting** | Kamera-Raster auf voller Breite, Steuerleiste, Einladungslink. Vor dem Start: Lobby mit „Meeting starten“ und „Link einfügen und beitreten“ |
| Tab **Notizen** | Dokumentliste und Block-Editor. Das erste Dokument enthält „Missums Live-Notizen“: Zusammenfassung, Entscheidungen, Aufgaben, offene Fragen |
| Tab **Whiteboard** | Große Fläche in fester Größe (kein Zoom) (6000 × 4000). Linke Maustaste auf freier Fläche verschiebt das Board, Neben den Spalten mit Haftnotizen gibt es frei platzierbare **Textfelder, Tabellen, Bilder (Datei oder Strg+V), Formen und Pfeile**. Klick wählt, Ziehen verschiebt, der Griff unten rechts ändert die Größe, Doppelklick bearbeitet, Entf löscht, Rechtsklick öffnet Farbe, Schriftgröße, Zeilen/Spalten, Duplizieren und Ebenen. |
| Tab **Whiteboard** (Spalten) | Spalten mit Haftnotizen. Ziehen verschiebt Notizen, Doppelklick legt eine an, Rechtsklick öffnet Reaktionen, Farbe, Verschieben und Löschen. „Von Missum ordnen lassen“ sortiert das Board neu |
| Kamera-Overlay | Kompaktes Panel oben rechts in Notizen und Whiteboard. Es zeigt Missums Kachel, die Teilnehmer und die Steuerung und lässt sich einklappen |
| Rechte Seitenleiste | Meeting-Chat ohne Tabs; das Transkript wird intern für Missum geführt, aber nicht mehr angezeigt. |
| Rechte Seitenleiste (alt) | Globaler Meeting-Chat mit Umschalter Chat/Transkript, Diktat-Mikrofon und Eingabe „Nachricht an alle oder @Missum“ |

- **Wer kann was bearbeiten:** Alle verbundenen Teilnehmer können Notizen und Whiteboard bearbeiten. Was ein Mensch an Missums Einträgen ändert, ist für Missum verbindlich und wird nicht mehr überschrieben.
- **Wann Missum arbeitet:**
  - nach 45 s ohne neue Inhalte
  - nach 8 neuen Chat- oder Transkript-Einträgen
  - sofort bei `@Missum` im Chat oder über „Von Missum ordnen lassen“

  Zwischen zwei automatischen Läufen liegen mindestens 20 s. Antworten auf Fragen erscheinen im Chat. Eine Sprachausgabe gibt es bewusst nicht.

## Tab „Transcript“

- Fester Tab nach „Meeting“: alles Gesprochene der Lobby, nach Teilnehmer, als Chatfenster ohne Eingabefeld.
- **Eigene Sätze** stehen rechts in einer Blase, wie die eigenen Nachrichten im ChatGPT-Modus.
- **Alle anderen** stehen links mit Profilbild, Name und Uhrzeit. Aufeinanderfolgende Sätze eines Sprechers bilden einen
  Block.
- **Neue Zeilen** erscheinen wie gestreamter Text, Zeichen für Zeichen. Wer unten ist, dem folgt die Ansicht.
- **Rechte Seitenleiste:** zeigt wie in Notes und Whiteboard den globalen Chat. Das kompakte Lobby-Overlay schwebt über dem
  Tab.

## Projektpflicht und Modellwahl

- Ein Meeting gehört immer zu einem Projekt. Ohne Projektordner zeigt jeder Tab „Projekt erforderlich“; Starten und Beitreten sind gesperrt, und es werden keine Dateien geschrieben.
- Der Host wählt unten im Meeting-Chat Modell, Denkaufwand und Kontextlimit, wie im Codex-Composer (dieselbe Auswahl, Rolle „coding“). Der Ring daneben zeigt den Kontext des letzten Missum-Laufs. Gäste nutzen das Modell des Hosts.
- „Meeting starten“ geht erst live, wenn Missum arbeiten kann: zuerst lädt das gewählte Modell (`/v1/models/prepare`),
  dann die Spracherkennung (Whisper, über die gemeinsame Live-Caption-Session). Die Lobby zeigt den jeweiligen Schritt;
  schlägt einer fehl, bleibt das Meeting geschlossen und eine Meldung erklärt den Grund.
- Missum benennt das Meeting, solange es nur einen Platzhalter trägt („Neue Sitzung“, „Meeting“). Ein Platzhalter sperrt
  den Titel nie; erst ein echter Name vom Host in der Seitenleiste tut das.
- Eine Frage an Missum bekommt immer eine Antwort: Liefert das Modell keine `chatReply`, fragt Missum einmal gezielt nach;
  bleibt sie leer, sagt Missum das im Chat, statt eine Statusmeldung an ihre Stelle zu setzen.
- Bilder auf dem Whiteboard liegen als `assets/<id>.jpg` im Meeting-Ordner.

## Architektur

```
Missum (Host)                                     Missum (Gast)
 ├ MeetingHostService  Kestrel :8090  ◄─ WebSocket ─  MeetingClient
 │    /v1/meetings/{id}/ws   Text: JSON-Operationen · Binär: JPEG-Video, PCM-Audio
 ├ MeetingAgentService     → Gateway /v1/runs (JSON-Antwort, MeetingAgentMerge)
 ├ MeetingTranscriptionHub → Live-Caption-Dienst der Sprachsteuerung (Whisper)
 └ MeetingProjectStore     → <Projekt>\Meetings\<Datum>-<Titel>-<Sitzung>\
```

- **Ein Codepfad für alle:** Auch das Host-Fenster verbindet sich per Loopback als Client. Jede Replik wendet dieselben Operationen mit `MeetingRoom` an. Der Host prüft jede Operation, vergibt die Revision und verteilt sie in Commit-Reihenfolge.
- **Identität:** Die Identität eines Teilnehmers stammt immer aus seiner Verbindung, nie aus seiner Nachricht. Operationen, die nur der Host senden darf, werden von Gästen verworfen: Transkript, Missums Updates und Agent-Status.
- **Dauerdienst:** `MeetingHostService` startet in `App.OnLaunched` und stoppt im Shutdown-Schritt „meetings“. Sein Status erscheint als Chip „Meetings-Host“ in der Fußzeile.
- **Port:** Standard ist 8090. `MISSUM_MEETING_PORT` überschreibt ihn; ist der Port belegt, werden die nächsten neun probiert.

## Beitreten

- **Linkformat:** `missum-<profil>://join/<host>:<port>/<meeting>?t=<token>`
- **Registrierung:** Das Schema ist dasselbe, das Missum ohnehin für Sitzungslinks registriert. Ein Klick öffnet Missum oder holt das laufende Fenster nach vorn, wechselt in Claude Meetings und tritt bei.
- **Token:** Er wird in `meeting.json` gespeichert, damit Einladungen einen Neustart überstehen.
- **Netz:** Vorerst nur im lokalen Netzwerk. Beim ersten Lauschen fragt die Windows-Firewall nach einer Freigabe. Gäste müssen den Host direkt erreichen können, NAT-Traversal und TURN gibt es nicht.

## Dateien im Projektordner

| Datei | Inhalt |
| --- | --- |
| `meeting.json` | Meeting-ID, Rolle (Host/Gast), Einladungslink oder Token, Teilnehmer, Dokumentliste |
| `notes/NN-<Titel>.md` | Lesbares Markdown; Block-Metadaten stehen als HTML-Kommentare darin |
| `whiteboard.json` | Spalten, Notizen, Reihenfolge, Reaktionen |
| `chat.jsonl`, `transcript.jsonl` | Eine Nachricht pro Zeile |

- **Schreiben:** Alles wird atomar geschrieben (temporäre Datei, dann Move), verzögert um 1,5 s nach der letzten Änderung.
- **Gäste:** Sie spiegeln den Stand in ihren eigenen Projektordner. Später geöffnet, zeigt ihre Sitzung diese Kopie zum Lesen und bietet „Erneut beitreten“ an.
- **Ohne Projekt:** Liegt die Sitzung in keinem Projekt, verwendet Missum `%LOCALAPPDATA%\Missum\Meetings`.

## Medien

- **Video:** `MediaCapture` mit `MediaFrameReader`; JPEG mit etwa 640 px Breite und bis zu 12 Bildern pro Sekunde. Ein langsamer Empfänger verliert Bilder, bekommt aber keine Verzögerung.
- **Audio:** PCM16, 16 kHz, mono, 100-ms-Rahmen. Der Host leitet es weiter, jeder Client mischt die Stimmen der anderen und spielt sie ab. Jeder Teilnehmer lässt sich lokal stummschalten. Kopfhörer vermeiden Echo; eine Echounterdrückung gibt es nicht.
- **Transkription:** Sprachpausen schneiden das Audio in Äußerungen von höchstens 6 s. Der Sprachdienst erlaubt nur eine
  Live-Caption-Session gleichzeitig; deshalb teilen sich alle Sprecher eine Session, die Äußerungen werden nacheinander
  erkannt. Ist die Session abgelaufen, öffnet der Host einmalig eine neue, ohne die Äußerung zu verlieren.
- **Wer spricht:** Ein deutlicher Mikrofonausschlag (Pegel ≥ 0,18) oder erkannte Sprache lässt den Namen in der Kamera
  grün leuchten (grüner Rahmen, grüner Namens-Chip mit Schein). Im Meeting-Tab bleiben die Kacheln fest. Das kompakte
  Overlay in Notes und Whiteboard zeigt die gerade Sprechenden (höchstens zwei, andere vor dir) und wechselt mit dem
  Gespräch; fünf Sekunden nach dem letzten Wort fällt es auf die eigene Kamera bzw. die Teilnehmerliste zurück.
- **Virtuelle Geräte:** `MISSUM_MEETING_VIRTUAL_MEDIA=1` ersetzt Kamera und Mikrofon durch ein bewegtes Testbild und
  die WAV-Datei aus `MISSUM_MEETING_VIRTUAL_MIC` (einmal in Echtzeit, danach Stille). Alles ab der Aufnahme läuft
  unverändert – für PCs ohne Geräte und für Live-Tests.

## Tests

- **Unit- und Integrationstests:** `MissumMeetingsModeTests`, `MeetingCoreTests`, `MeetingAgentMergeTests`, `MeetingStorageTests` und `MeetingHostServiceTests`. Letzterer betreibt einen echten Kestrel-Host mit zwei WebSocket-Clients.
- **UI-Smoke:** `MISSUM_SMOKE_UI_MEETINGS=1` zusammen mit den üblichen Smoke-Variablen. Er wechselt in den Modus, füllt ein Demo-Meeting und rendert alle Tabs in Dunkel, Hell und im schmalen Fenster als PNG in das Datenverzeichnis. Dazu schreibt er `native-meetings-validation.json`.
- **Live-Test mit zwei Instanzen:** `MISSUM_SMOKE_UI_MEETINGS=live-host` bzw. `live-guest` mit gemeinsamem
  `MISSUM_SMOKE_MEETINGS_LIVE_DIR` und virtuellen Geräten. Der Host startet mit Kamera und Mikrofon und legt den Link
  ab, der Gast tritt bei, schaltet die Kamera ein und schreibt Chat, Notiz und Whiteboard-Zettel; dann fragt der Host
  Missum mit dem gewählten lokalen Modell. Beide schreiben `native-meetings-live-*.json` und `live-*.png`.

## Werkzeuge

Missums Meeting-Läufe sind normale Gesprächsläufe mit den Server-Werkzeugen aus Codex: Websuche (`web.search`), Webseiten abrufen (`web.fetch`), Rechnen (`math.evaluate`) und Bildanalyse (`media.inspect`, `media.analyze`). Die Kamera-Kachel zeigt dabei z. B. „Suche im Web …“. Werkzeuge, die den Projektordner verändern (Dateien schreiben, Befehle ausführen), stehen im Meeting bewusst nicht zur Verfügung.

## Zusammenspiel aller Bereiche

Meeting, Gespräch, Chat, Notizen und Whiteboard teilen sich einen Kontext. Jede inhaltliche Änderung eines Menschen landet in einem gemeinsamen Änderungsprotokoll (z. B. „Vinny verschob ‚Keynote‘ nach ‚Programm‘“). Missum bekommt bei jedem Durchgang dieses Protokoll, alle Dokumente, das ganze Whiteboard sowie Chat und Gespräch und übernimmt Neues sinngemäß in die jeweils anderen Bereiche: Entscheidungen und Aufgaben vom Whiteboard oder aus weiteren Dokumenten in die Notizen, wichtige Punkte aus Notizen, Chat und Gespräch als Haftnotiz auf das Whiteboard.

- Chat und Gespräch: Durchgang nach 45 s Ruhe oder nach 8 neuen Beiträgen.
- Notizen und Whiteboard: Durchgang 25 s nach der letzten Bearbeitung (damit niemand beim Tippen gestört wird).
- `@Missum`: sofort, mit demselben Kontext.
- Hat Missum Änderungen spürbar übertragen, sagt es das höchstens alle 2 Minuten mit einem Satz im Chat. Die Kamera-Kachel zeigt z. B. „Übernehme Whiteboard in die anderen Bereiche …“.
- Eigene Änderungen von Missum lösen keinen neuen Durchgang aus; scheitert ein Durchgang, gehen die Änderungen in den nächsten über.

## Werkzeug-Overlays, Titel und Autoren (Stand Webdesign-Runde)

- Notes und Whiteboard tragen ihre Werkzeuge in einem schwebenden, halbtransparenten Overlay wie im Webdesign-Tab
  (`Controls/FloatingToolbar.cs`): verschiebbar am Griff, einklappbar. Die Kopfzeilen zeigen nur noch den Titel.
- Notes: „Zusammenfassen“ sitzt kompakt im Overlay. Vor jedem Block steht ein Balken in der Farbe seines Autors
  (Missum in seiner eigenen, mehrere Autoren als Segmente); eine Autorenspalte gibt es nicht. Beim Überfahren nennt
  ein Tooltip Nickname und Uhrzeit jedes Autors. Autoren und Zeiten setzt der Raum aus dem Absender der Änderung
  (`Authors`, `AuthorTimes`).
- Es gibt höchstens ein leeres Feld: Leere Blöcke, in denen niemand schreibt, verschwinden; „+ Absatz hinzufügen“
  erscheint nur, wenn kein leeres Feld existiert, und Enter auf einer leeren Zeile legt keine weitere an.
- Ein Titel überall: Notes, Whiteboard und der Sitzungsname in der Seitenleiste zeigen den Raumtitel. Missum
  benennt das Meeting anhand des Inhalts kompakt (`title` in seiner Antwort, höchstens 48 Zeichen). Nur der Host
  benennt um – in der Seitenleiste; danach behält Missum diesen Namen (`TitleLocked`).
- Das kompakte Kamera-Overlay ist halbtransparent, am Kopf verschiebbar und an den Seitenrändern skalierbar.
