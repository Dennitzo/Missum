# Build- und Betriebsskripte

## Missum-Client

Der normale Clientbuild führt Restore, Release-Build und win-x64-Single-file-Publish aus. Er startet keine Tests und keinen Portable-Smoke:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1
```

Tests bleiben separat über `windows/test.ps1`, `windows/test-agent-context.ps1` und `windows/smoke.ps1` verfügbar.
Mit `-RunTests` beziehungsweise `-RunSmoke` können sie ausdrücklich zum Build zugeschaltet werden.
Die bisherigen Schalter `-SkipTests` und `-SkipSmoke` bleiben kompatibel und haben Vorrang vor diesen Optionen.
Die Veröffentlichung erstellt weiterhin das Manifest und prüft die SHA-256 der erzeugten `Missum.exe`.

Die vollständige Portable-Werkzeugabnahme wird ausdrücklich zugeschaltet. Sie veröffentlicht zuerst eine
frische, manifest- und SHA-256-geprüfte `Missum.exe` und führt danach die nativen Vertragsprüfungen sowie die
realen Gateway-, Medien-, Sprach-, Dokument-, Coding- und Claude-Science-Suiten seriell aus. Die Medienabnahme
enumeriert dabei echte Windows-Fenster, erstellt einen Desktop-Screenshot und zeichnet einen kurzen Bildschirmclip auf:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1 `
  -Configuration Release `
  -RunLiveToolAcceptance
```

Voraussetzungen sind ein laufender und gesunder Missum-AI-Stack unter `http://127.0.0.1:8080`, eine erreichbare
native Modellruntime, Docker Desktop für die realen Workerpfade sowie heruntergeladene, werkzeugfähige
DeepSeek-Profile für General, Coding und Vision. General und Coding müssen im Modellstatus die explizite Stufe
`none` anbieten. Die Abnahme setzt diese Stufe für jeden Modelllauf; Vision-Medienaufrufe besitzen selbst keinen
Reasoning-Parameter und werden stattdessen gegen das ausgewählte DeepSeek-Vision-Profil geprüft. Fehlende
DeepSeek-Identität, ein anderes Reasoning oder ein unvollständiger Modellstatus brechen bereits den Preflight ab.

Ohne explizite Modellparameter wählt das Skript die installierten DeepSeek-Profile selbst. Eine bestimmte
Installation und ein abweichender Gateway lassen sich festlegen:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\windows\build.ps1 `
  -Configuration Release `
  -RunLiveToolAcceptance `
  -AiServerUrl 'http://127.0.0.1:8080' `
  -DeepSeekModelId 'coding/DeepSeek-...~revision' `
  -DeepSeekVisionModelId 'vision/DeepSeek-...~revision'
```

`-RunLiveToolAcceptance` ist nicht mit `-SkipPublish` kombinierbar. Für ein ausdrücklich angefordertes vollständiges Release-Gate
werden zusätzlich `-RunTests -RunSmoke` gesetzt. Die Live-Abnahme kann wegen lokaler Modellinferenz, Bild-/Spracherzeugung und
Deep Research längere Zeit benötigen und darf nicht parallel zu einem zweiten Modell- oder Buildlauf gestartet
werden.

Jeder Lauf schreibt seine Belege standardmäßig nach
`artifacts\validation\portable-tools\<UTC-Zeitstempel>`. `summary.json` enthält Portable-Pfad und SHA-256,
DeepSeek-Modell-IDs, `reasoningEffort: none`, die Coverage-Matrix aller integrierten Aktionen und den Status jeder
Suite. Daneben liegen je Suite Konsolenlog und TRX; erzeugte Toolbelege werden im selben Laufverzeichnis abgelegt.
Auch ein fehlgeschlagener Preflight schreibt dort eine `summary.json`. Ein eigener Belegordner kann mit
`-ToolAcceptanceEvidenceDirectory` gewählt werden, muss aber unterhalb von `artifacts` liegen.


## Native Modelle und Docker-Gateway

General, Coding, Vision und Embedding laufen unter Windows mit einem von Missum verwalteten offiziellen
`llama-server.exe` aus dem GitHub-Repository `ggml-org/llama.cpp`. Die GGUF-Dateien werden direkt aus dem
lokalen Hugging-Face-Cache gelesen. Docker betreibt
das Missum-Gateway sowie die Sprach-, Bild- und Recherche-Worker. Der Gatewayzugriff auf die native Modellruntime
erfolgt über `http://host.docker.internal:8081`; der Missum-Client verbindet sich mit dem Gateway auf Port 8080.

Die portable `Missum.exe` prüft beim Start einer noch nicht laufenden nativen Laufzeit automatisch die aktuelle
GitHub-Version, lädt die offiziellen Windows-CUDA-Archive und verifiziert deren von GitHub veröffentlichte
SHA-256-Digests. Vollständig installierte Versionen bleiben getrennt erhalten. Ist GitHub vorübergehend nicht
erreichbar, wird die zuletzt vollständig geprüfte Version verwendet. Eine laufende Modellinstanz wird durch
ein Update nicht beendet; die neue Version gilt ab dem nächsten Start. Die erforderlichen Hilfsdateien sind
in der EXE enthalten;
der Start funktioniert auch außerhalb des Repository-Verzeichnisses und nach einem Windows-Neustart.
Die Modellkataloge werden beim Öffnen der Einstellungen geladen. Ein bereits laufender Server wird wiederverwendet.

Alle folgenden Befehle werden im Repository-Verzeichnis ausgeführt. Benötigt werden Docker Desktop,
die für Missum verwendete .NET-SDK-Version und Python für den lokalen Modellkatalog. Eine externe llama.cpp-
Installation ist nicht erforderlich.

| Einstellung | Standard |
|---|---|
| Native Modellablage | `%USERPROFILE%\.cache\huggingface\hub` |
| Native Serverdatei | `<Assistent-Datenverzeichnis>\NativeRuntime\llama.cpp\versions\<GitHub-Tag>\llama-server.exe` |
| Unsloth-Python | `%USERPROFILE%\.unsloth\studio\unsloth_studio\Scripts\python.exe` |
| Native Prozessdaten und Logs | `%USERPROFILE%\.missum\native-runtime` |
| Stackdaten (`DataRoot`) | `%PROGRAMDATA%\Missum-AI-Stack` |
| Sprach- und Bildmodelle (`ModelRoot`) | `<DataRoot>\Models` |
| Compose-Umgebung | `<DataRoot>\stack.env` |

`ASSISTANT_LLAMA_INSTALL_ROOT` legt bei Bedarf einen anderen verwalteten Installationsordner fest.
`ASSISTANT_NATIVE_BINARY_PATH` bleibt ein expliziter Administrator-Override; bei gesetztem Wert wird das
automatische GitHub-Update bewusst übersprungen.

`-NativeModelRoot` wählt bei den Stackskripten einen anderen Cacheordner. Diesen Wert beim Bauen, Starten,
Deployen und Aktualisieren konsistent übergeben. `-ModelRoot` bezeichnet bei diesen Skripten die separaten
Worker-Modelle. Beim nativen Manager bezeichnet `-ModelRoot` hingegen die GGUF-Ablage; dort ist
`-NativeModelRoot` als Alias verfügbar.

Der Manager heißt aus Kompatibilitätsgründen weiterhin `manage-coding-llama.ps1` und verwaltet alle vier
Modellrollen. Textmodelle können unabhängig im Dropdown **General AI Modell** und **Coding AI Modell**
gewählt werden. Vision benötigt ein passendes `mmproj`-GGUF; Embedding-Modelle werden als eigene Rolle erkannt.
Die nativen Kontextfenster werden aus den GGUF-Metadaten ermittelt und an den verfügbaren GPU-Speicher angepasst.

## Vorhandene Modelle übernehmen

`migrate-local-models-to-unsloth.ps1` übernimmt vorhandene, im
[Modellmanifest](../deploy/missum-ai/models.manifest.json) verzeichnete Dateien in die Unsloth-Ablage. Der Quellordner
muss die Manifeststruktur `<Herausgeber>\<Repository>\<Datei>` enthalten. Quelle und Ziel müssen auf demselben
Windows-Laufwerk liegen; symbolische Verknüpfungen und andere Reparse Points werden abgewiesen.

Zunächst den vollständigen Prüfplan erzeugen; dabei bleiben die Modelldateien unverändert:

```powershell
.\windows\migrate-local-models-to-unsloth.ps1 -SourceRoot 'C:\AI\Modelle'
```

Das Skript prüft Dateigrößen, SHA-256 und NTFS-Dateiidentitäten und schreibt standardmäßig den Bericht
`artifacts\coding-validation\model-migration.json`. Unbekannte Dateien, abweichende Hashes oder vorhandene
Zieldateien brechen die Prüfung vor dem ersten Verschieben ab. Prozesse, die Quelldateien geöffnet halten,
müssen vor dem Verschieben beendet sein.

Den geprüften Bestand anschließend übernehmen:

```powershell
.\windows\migrate-local-models-to-unsloth.ps1 -SourceRoot 'C:\AI\Modelle' -Apply
```

`-DestinationRoot`, `-ManifestPath` und `-ReportPath` überschreiben die jeweiligen Standards. Die Übernahme
verschiebt Dateien innerhalb des Laufwerks ohne erneuten Download oder Überschreiben. Die Ablage folgt
`models--<Herausgeber>--<Repository>\snapshots\<Revision>\<Datei>`; `refs\main` enthält die genaue Revision ohne
Zeilenumbruch. Bei einem Fehler versucht das Skript, bereits verschobene Dateien an ihre ursprünglichen
Pfade zurückzusetzen, und hält den Status im Bericht fest.

## Modelle herunterladen

Für einen neuen Modellbestand lädt das allgemeine Downloadskript gepinnte GGUFs sowie die separaten
Worker-Modelle und prüft deren Integrität:

```powershell
.\windows\download-ai-models.ps1
```

`-SkipWorkerModels` beschränkt den Lauf auf native GGUFs; `-SkipLlmModels` lädt nur die Worker-Modelle.
Native Downloads landen im HF-Snapshotlayout, unvollständige Downloads im Staging-Verzeichnis. Die
Snapshotreferenz wird erst veröffentlicht, wenn alle zugehörigen Dateien einschließlich Shards und
Projektoren erfolgreich geprüft wurden. Bereits vorhandene Dateien werden geprüft und weiterverwendet.

Qwen3-Coder-Next Q8_0 ist ein separater, optionaler Download von vier Shards mit zusammen rund 79 GiB:

```powershell
.\windows\download-qwen3-coder-next-q8.ps1 -PlanOnly
.\windows\download-qwen3-coder-next-q8.ps1
```

`-PlanOnly` zeigt Pfade, Größen und Prüfsummen ohne Dateisystemänderungen. Nach dem Download die
Modellliste in den Missum-Einstellungen aktualisieren. Upstream-Herausgebernamen in Manifest und Cache
bezeichnen die Herkunft der GGUF-Dateien.

## Bauen, Starten und Stoppen

Der Serverbuild führt die Servertests und Compose-Prüfung aus und baut anschließend die Docker-Images:

```powershell
.\windows\build-ai-stack.ps1
```

Er startet oder aktualisiert noch keine laufenden Container. Das Deployment prüft den lokalen Modellbestand,
bereitet Stackdaten und Firewall vor und baut und startet standardmäßig den Stack:

```powershell
.\windows\deploy-ai-stack.ps1
```

Für die Firewallregel ist eine administrative PowerShell erforderlich. `-SkipBuild` und `-SkipStart`
überspringen die jeweiligen Schritte. Die Integritätsprüfung berücksichtigt vorhandene gepinnte Dateien;
es müssen nicht alle historischen Modellalternativen aus dem Manifest installiert sein. Erforderlich sind
vollständige native Modelle für General, Vision und Embedding sowie die Worker-Ressourcen.
`-LegacyDatabasePath` übernimmt bei noch fehlender Zieldatenbank eine bestehende SQLite-Datenbank und
legt eine Sicherung unter `<DataRoot>\migration-backups` an.

Für den laufenden Betrieb:

```powershell
.\windows\start-ai-stack.ps1
.\windows\manage-coding-llama.ps1 -Action Status
.\windows\smoke-ai-stack.ps1
.\windows\smoke-ai-stack.ps1 -IncludeInference
.\windows\stop-ai-stack.ps1
```

Start lädt den nativen Modellmanager und startet die Docker-Dienste. Stop beendet die Compose-Dienste
und den von Missum verwalteten nativen Prozess. Die Statusabfrage prüft den Manager und den lokalen
Modellkatalog. Smoke prüft Gateway, Modellrollen, Coding-Katalog und die veröffentlichten Docker-Ports;
`-IncludeInference` führt zusätzlich einen General-Testprompt aus.

Der native Manager kann auch einzeln mit `-Action Start`, `-Action Status` oder `-Action Stop` verwendet
werden. Abweichende Installationen lassen sich dort über `-BinaryPath` und `-PythonPath` angeben.
Für geänderte Runtimepfade zuerst den verwalteten Prozess stoppen und anschließend erneut starten.

`update-ai-stack.ps1` baut die Images mit Servertests neu und ruft danach den Stackstart auf. `-Pull`
aktualisiert dabei die Basisimages. Ein bereits laufender nativer Manager wird mit seiner bisherigen
Konfiguration weiterverwendet.

## Weitere aktive Hilfsskripte

| Skript | Zweck |
|---|---|
| `remove-obsolete-artifacts.ps1` | regenerierbare Alt-Builds und frühere Modelltestausgaben entfernen; Diagnose-Traces behalten |
| `remove-legacy-ai-server-autostart.ps1` | Autostart der früheren Windows-Server-App entfernen |
| `remove-legacy-ai-server-installation.ps1` | bereits migrierte frühere Serverinstallation bereinigen; aktive Docker-Daten und Migrationsbackup erhalten |
| `common.ps1` | gemeinsame Pfad-, Cache-, Compose- und .NET-Funktionen bereitstellen |

Weitere Informationen zu Gateway, Werkzeugen und Protokoll stehen in [Missum-AI-SERVER.md](../Missum-AI-SERVER.md).
