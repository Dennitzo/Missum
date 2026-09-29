# Missum AI Hybrid-Stack

Missum betreibt Gateway, Orchestrierung, SQLite, SearXNG sowie Speech-, Media- und Image-Worker in Docker.
General, Coding, Vision und Embeddings verwenden einen nativen Windows-`llama-server.exe`
aus der vorhandenen Unsloth-Installation und lesen die lokalen GGUF-Dateien direkt.
Datei- und Prozesswerkzeuge des Coding-Agenten führt der Missum-Client im ausgewählten Projekt aus.

## Architektur

```text
Missum -> http://GPU-SERVER:8080 -> Caddy -> Missum Gateway (Docker)
                                               |- llama.cpp :8081 (Windows-Host, alle LLM-Rollen)
                                               |- Speech Worker
                                               |- Media Worker
                                               |- Image Worker
                                               `- SearXNG
```

Caddy veröffentlicht den Missum-Endpunkt auf Port `8080`. Die Docker-Worker besitzen keine Hostports. Das Gateway erreicht
die native Modellruntime über `host.docker.internal:8081`.
Der Client sendet Modellanfragen an das Gateway. Missum verwendet keine
API-Schlüssel, Zertifikate oder Verbindungspakete und darf deshalb nicht ins öffentliche Internet freigegeben werden.

## Modelle und Speicherorte

Alle lokalen GGUF-Modelle liegen im von Unsloth erkannten Hugging-Face-Cache:

```text
C:\Users\AMD\.cache\huggingface\hub
```

Standardprofile:

- General AI: beispielsweise GPT-OSS 120B oder 20B, 32.768 Kontexttoken in dieser Runtime.
- Vision: `qwen3-vl-30b-a3b-instruct`.
- Embeddings: `text-embedding-bge-m3`.

`windows/manage-coding-llama.ps1 -ModelRoot <absoluter Pfad>` kann eine andere vorhandene Windows-Modellwurzel
verwenden. Der Scanner `workers/coding/catalog.py` erstellt daraus lokale Router-Presets. Vollständige
GGUF-Sprachmodelle einschließlich kompletter Split-GGUFs erscheinen in **General AI Modell** und
über `GET /v1/models/coding` im separaten Dropdown **Coding AI Modell**. Vision-Modelle erhalten einen
passenden Projektor; Embeddingmodelle eigene Embedding-Presets. Projektionsdateien werden nicht als
eigenständige Textmodelle angeboten. Unvollständige Downloads werden ausgeschlossen. Die Dateien bleiben
auf Windows; es gibt keinen LLM-Modellcontainer und keine Linux-Kopie.

Nur Speech- und Bildgenerierungsressourcen bleiben unter `C:\ProgramData\Missum-AI-Stack\Models`, weil diese von den
spezialisierten Docker-Workern direkt gelesen werden. Revisionen, Größen und SHA-256-Werte der migrierten GGUF-Dateien
stehen in `deploy/missum-ai/models.manifest.json`.

Das Gateway nutzt den nativen Router für Modellkatalog, Load und Unload sowie die OpenAI-kompatiblen
Endpunkte `/v1/chat/completions` und `/v1/embeddings`. Ein bereits geladenes Modell wird wiederverwendet.
Vorbereitung und Inferenz teilen eine Sperre, damit Modellwechsel keine laufende Anfrage verdrängen.
Der verwaltete Router hält höchstens ein Modell gleichzeitig geladen; die Studio-eigenen Prozesse
werden nicht übernommen oder beendet.

## Persistente Daten

```text
C:\ProgramData\Missum-AI-Stack
|- data\database
|- data\uploads
|- data\artifacts
|- data\logs
|- Models\speech
`- Models\image
```

Die serverseitige SQLite-Datenbank speichert AI-Läufe, Warteschlange, Tool-Checkpoints und Artefaktmetadaten. Chats,
Sitzungen, Projekte und UI-Zustände bleiben in der lokalen SQLite-Datenbank des Missum-Clients.

## Installation und Betrieb

Voraussetzungen sind Docker Desktop mit NVIDIA-Unterstützung, die lokal installierten Modelldateien,
das vorhandene native Unsloth-llama-Binary und ein Python-Interpreter.
Der Launcher verwendet standardmäßig:

```text
%USERPROFILE%\.unsloth\llama.cpp\build\bin\Release\llama-server.exe
%USERPROFILE%\.unsloth\studio\unsloth_studio\Scripts\python.exe
```

Andere vorhandene Installationen lassen sich mit `-BinaryPath` und `-PythonPath` angeben. Es werden keine Modelle
oder neuen Runtime-Abhängigkeiten heruntergeladen.

```powershell
Set-Location C:\Users\AMD\Documents\GitHub\Missum
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\deploy-ai-stack.ps1
```

Lifecycle:

```powershell
.\windows\build-ai-stack.ps1
.\windows\start-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\stop-ai-stack.ps1
```

`start-ai-stack.ps1` startet beziehungsweise prüft den nativen Supervisor auf Port `8081`
und anschließend Compose. `-NativeModelRoot` setzt die gemeinsame Windows-Modellwurzel.
`stop-ai-stack.ps1` beendet die Docker-Dienste und den eigenen nativen Supervisor samt Modellprozessen.

Die native Modellruntime kann unabhängig verwaltet werden:

```powershell
.\windows\manage-coding-llama.ps1 -Action Start
.\windows\manage-coding-llama.ps1 -Action Status
.\windows\manage-coding-llama.ps1 -Action Stop
```

Der Launcher prüft Binary-Fähigkeiten und Portbelegung und startet den Supervisor ohne sichtbares Konsolenfenster.
Zustand, generierte Presets und Logs liegen standardmäßig unter `%LOCALAPPDATA%\Missum-CodingLlama`.
Die Gateway-Konfiguration verwendet `MISSUM_AI_MODEL_RUNTIME_URL=http://host.docker.internal:8081`;
`MISSUM_AI_CODING_MODEL_ROOT` beschreibt die tatsächlich mit `-ModelRoot` gewählte Windows-Modellwurzel.

Vorhandene Modelle aus einer bisherigen Windows-Modellablage lassen sich mit dem geprüften Migrationsskript
in den Hugging-Face-Cache verschieben:

```powershell
.\windows\migrate-local-models-to-unsloth.ps1 -SourceRoot C:\Pfad\zur\bisherigen\Modellablage -Apply
```

Das Skript prüft vor der ersten Verschiebung sämtliche Größen und SHA-256-Werte gegen das gepinnte
Manifest. Es verschiebt nur auf demselben Windows-Laufwerk, überschreibt keine Dateien und verifiziert
anschließend die unveränderte NTFS-Dateiidentität. Die Cache-Struktur lautet
`models--Publisher--Repository/snapshots/<revision>/<Dateiname>` mit passender `refs/main`.
Worker-Ressourcen bleiben am Docker-Pfad. Das Migrationsprotokoll ermöglicht die genaue Zuordnung alter
und neuer Dateipfade.

## Client und Tools

Der Client verbindet sich standardmäßig mit `http://192.168.0.67:8080`. **General AI Modell** und **Coding AI Modell**
besitzen unabhängige gespeicherte Auswahlen. Für Coding wird unter **Tools → Coding** der Modus gewählt und über den
Projektchip ein vorhandener Ordner ausgewählt. Der Projektordner gehört zur jeweiligen Sitzung und wird beim
Sitzungswechsel wiederhergestellt. Eine Sitzung ohne zugeordneten Ordner kann keine Coding-Dateiwerkzeuge ausführen.

General verwendet einen kompakten Toolkatalog mit nachgeliefertem Einzelschema. Serverseitige Recherche-, Medien- und
Berechnungswerkzeuge werden mit den freigegebenen Servertools kombiniert. Der Client kann Sitzungsdokumente lesen und

native Seitenwechsel und Folgeprompts aktiv, bis er im Tools-Menü abgewählt wird; jeder Folgeprompt läuft als

Coding wird als `RunRequest.mode="coding"` mit `clientCapabilities=["coding"]` und `preferredCodingModelId`
gestartet. Die ID stammt unverändert aus `/v1/models/coding`; der Projektpfad bleibt im Client. Die Werkzeuge
`coding.list`, `coding.search`, `coding.read`, `coding.write`, `coding.edit`, `coding.gitDiff` und `coding.command`
arbeiten mit begrenzten Ausgaben. Bestehende Dateien benötigen für Änderungen den beim Lesen erhaltenen SHA-256-Wert.
Dateipfade dürfen den gewählten Projektordner nicht verlassen. Die angebotenen lokalen Werkzeuge werden im Rahmen
des Nutzerauftrags automatisch ausgeführt. Programme und Argumente bleiben am Werkzeugschritt sichtbar; das
Arbeitsverzeichnis eines gestarteten Prozesses ist keine Betriebssystem-Sandbox.

Zusätzlich bietet der Client automatisch `web.search`, `web.fetch` und `web.deepResearch` an.
Das lokale Modell entscheidet anhand des Auftrags, ob eine kurze SearXNG-Suche oder mehrstufige Recherche
nötig ist. Deep Research plant Teilfragen, liest bestätigte Fundstellen und prüft Belege für die Synthese.
Die inneren Modell- und Toolaufrufe zählen gegen das Budget des Coding-Laufs; ein manueller Start entfällt.
Recherchephasen senden laufend Status. Werkzeug-Start und -Ende tragen eine stabile Operations-ID.
Die WebView zeigt aufklappbare Schritte mit Eingaben, tatsächlichen Ergebnissen und Fehlerdiagnosen;
Migration 32 speichert diese Historie pro Nachricht. Die bisherigen Nachrichtenfooter bleiben erhalten.

`model.generation`-Ereignisse unterscheiden Laden (`codingLoading`), Warten (`codingWaiting`), Prompt-Verarbeitung,
Tokenfortschritt und Tool-Auswahl. `elapsedSeconds` macht Wartezeiten sichtbar, ohne Tokenfortschritt vorzutäuschen.
Prompt-Caching bleibt für die Coding-Tool-Schleife aktiv. Coding läuft standardmäßig ohne Gesamtzeit-, Runden-
oder Werkzeuganzahllimit (`0 = unbegrenzt` für `limits.timeoutSeconds`, `MISSUM_AI_CODING_MAXIMUM_MODEL_ROUNDS` und
`MISSUM_AI_CODING_MAXIMUM_TOOL_CALLS`). Ein fehlendes Coding-Zeitlimit bedeutet ebenfalls unbegrenzt. Optional gesetzte
positive Grenzen gelten weiter; ein endlicher Budgetabschluss benennt offene Arbeit und bleibt ein Fehlerstatus.
`coding.command.timeoutSeconds` ist standardmäßig ebenfalls `0`; ausdrücklich positive Prozessfristen sind möglich.
Abbruch stoppt laufende lokale Prozesse einschließlich ihres Windows-Job-Prozessbaums.

Ein Coding-Checkpoint wird nach Gateway-Neustart automatisch fortgesetzt. Ausstehende Coding-Clientwerkzeuge
verfallen beim Offlinegehen nicht; Missum verwendet bei Wiederaufnahme dieselbe Proposal-ID und das Ausführungsjournal.
Ältere abgeschlossene Tool-Turns werden vor vollem Kontext durch das ausgewählte Modell in einen gespeicherten
Arbeitsstand verdichtet. Originalauftrag, aktuelle vollständige Tool-Antworten und die vollständigen Belege im
Laufjournal bleiben erhalten. Höchste Reasoning-Einstellung und das verfügbare Modellausgabebudget bleiben aktiv.
Vorübergehende native Verbindungs-/HTTP-Fehler werden bei unbegrenzten Coding-Läufen persistent mit 5–300 Sekunden
Backoff erneut versucht (`providerRetryWaiting`); permanente Argument-/Toolfehler werden nicht blind wiederholt.
Die native Coding-Inferenz hat eine Stillstandsfrist von 20 Minuten: neue Prompt-/Tokenfortschritte verlängern sie,
Heartbeat-Pings und unveränderte Zähler nicht. Eine aktive Generierung kann deshalb länger dauern; ein tatsächlicher
Stillstand beendet den Versuch mit konkreter Diagnose. Die Modellladefrist bleibt fünf Minuten.
Begrenzte native Transportversuche, einzelne Research-Aufrufe, JSON-/Dateiausgaben und interne Git-Prüfungen schützen
weiterhin vor unbrauchbaren oder blockierten Operationen. Datenbankmigration 52 entfernt die frühere Bibliothek samt
Auswahlreferenzen endgültig und legt vor der einmaligen Konvertierung eine lokale Datenbanksicherung an.

General-Antworten, Coding und Function Calls laufen über den OpenAI-kompatiblen
`/v1/chat/completions`-Endpunkt des nativen llama.cpp-Routers. Missum fasst
anfängliche Systemblöcke zusammen und erhält chronologische Retry- und Werkzeughinweise an ihrer Position.

## Abnahme

```powershell
dotnet test .\tests\Missum.Ai.Server.Tests\Missum.Ai.Server.Tests.csproj -c Release
dotnet test .\tests\Missum.Tests\Missum.Tests.csproj -c Release
.\windows\build-ai-stack.ps1
.\windows\smoke-ai-stack.ps1
.\windows\build.ps1
```

Direkte Diagnose:

```powershell
.\windows\manage-coding-llama.ps1 -Action Status
Invoke-RestMethod -Uri http://127.0.0.1:8080/v1/models/coding
docker compose --env-file C:\ProgramData\Missum-AI-Stack\stack.env -f .\deploy\missum-ai\compose.yaml ps
docker compose --env-file C:\ProgramData\Missum-AI-Stack\stack.env -f .\deploy\missum-ai\compose.yaml logs --tail 200
```
