# Model Hub

Der sechste Modus neben ChatGPT, Codex, Claude Science, Claude Meetings und Webdesign. Er zeigt die neuesten
**unsloth-GGUF-Modelle** von Hugging Face und lädt sie so herunter, dass Missums lokaler Modellserver sie findet.
Es gibt kein Eingabefeld und keine Sitzungen (der Modus hält intern eine verborgene Platzhalter-Sitzung, Migration 61).

## Aufbau

| Bereich | Inhalt |
|---|---|
| Linke Seitenleiste | Suche „Unsloth-Modelle suchen“, darunter „Neueste Unsloth-Modelle“: Name und Datum (vor 2 Tagen), sonst keine Statistiken. Ein Punkt zeigt „lädt“ (blau) oder „heruntergeladen“ (grün). „Weitere laden“ blättert weiter. |
| Kopf des Hauptfensters | Download-Ordner, deutlich hervorgehoben, mit „Ändern …“ und „Öffnen“. Daneben „Vom Gateway erkannt“ oder „Nicht im AI-Modellordner“ mit „Als AI-Modellordner verwenden“, dazu die erkannte Hardware. |
| Hauptfenster | Titel (Repository, Aktualisierung, Basismodell), dann die vollständige Liste aller GGUF-Quantisierungen mit Größe, Eignung und Aktion, darunter die Modellbeschreibung von Hugging Face. |
| Rechte Seitenleiste | Nur Downloads (Fortschritt in %, GB, Geschwindigkeit, Restzeit; Pausieren, Fortsetzen, Abbrechen) und Fehler mit „Erneut versuchen“. |

## Hugging Face

- **Liste:** `api/models?author=unsloth&filter=gguf&sort=lastModified&direction=-1`. Weitere Seiten kommen über den
  `Link`-Header; die Suche hängt `&search=` an.
- **Details:** `api/models/{repo}` liefert Commit, `gguf` (Parameter, Kontext) und das Basismodell. Alle Dateien mit Größe
  und LFS-SHA-256 kommen aus `api/models/{repo}/tree/{commit}?recursive=true`.
- **Beschreibung:** `{repo}/raw/{commit}/README.md` ohne YAML-Front-Matter. Markdig rendert sie (GitHub-Markdown mit HTML)
  in einem WebView2 (`ModelHubReadmeView`). Relative Bilder laden aus dem Repository; Links öffnen im Standardbrowser.
  Die Höhe folgt dem Inhalt, und hell oder dunkel folgt dem App-Theme.
- Anonym, mit User-Agent `Missum/<Version>`. Bei 429 wartet der Client und versucht es erneut.

## Quantisierungen (`ModelHubQuantizations`)

- Einzeldateien im Wurzelordner (`Model-Q4_K_M.gguf`), Ordner pro Quantisierung (`UD-Q2_K_XL/…`) und geteilte Dateien
  (`-00001-of-00004.gguf`) werden zu einer Zeile. Geteilte Modelle erscheinen nur, wenn alle Teile vorhanden sind.
- `mmproj` (Vision) wird mitgeladen. Dabei gilt die nächste Ebene vom Modellordner aufwärts, F16 vor BF16.
- **Nicht aufgeführt** sind:
  - Entwurfsmodelle für spekulatives Dekodieren (`mtp`, `dspark`, `eagle`, `draft`);
  - `imatrix`-Dateien;
  - `Mainline_Support/`.

## Vorhandene Modelle und Updates

- **Ordner-Scan:** Der Download-Ordner wird nach unsloth-Repositories durchsucht (`LocalHubModels`), beim Öffnen, nach einem
  Ordnerwechsel und nach jedem Download. Ohne Suche stehen sie oben unter „Im Download-Ordner“; in der Liste markiert sie
  ein grüner Punkt.
- **Knopf pro Quantisierung** (`ModelHubVersions`):
  - **Herunterladen:** Keine Modelldatei dieser Quantisierung liegt im Ordner.
  - **Vorhanden:** ausgegraut, ohne Aktion. Alle Dateien liegen im Snapshot des aktuellen Commits, oder in einem älteren
    Snapshot, dessen Dateien laut Hugging Face dieselben SHA-256-Prüfsummen haben wie heute. Eine andere
    Vision-Projector-Variante (z. B. `mmproj-BF16` statt `mmproj-F16`) zählt mit.
  - **Aktualisieren:** Die Kopie ist älter (andere Prüfsumme oder Größe) oder unvollständig. Das Update lädt in den Snapshot
    des aktuellen Commits. Unveränderte Dateien werden per Hardlink übernommen (auf anderen Laufwerken kopiert), nur
    geänderte werden geladen. Der alte Snapshot bleibt liegen.
- **Vergleich:** Für ältere lokale Snapshots holt der Client die Dateiliste dieses Commits von Hugging Face (höchstens vier
  Commits, zwischengespeichert). Ist sie nicht abrufbar (offline, Ordner ist kein Commit), gelten gleiche Größen als
  unverändert.

## Eignung für diesen PC (`SystemResources`, `ModelFitEstimator`)

- **Hardware:** `nvidia-smi` liefert jede GPU einzeln, `GlobalMemoryStatusEx` den RAM. Beides läuft lokal, also auch ohne
  Docker-Gateway.
- **Bedarf:** Dateien × 1,05 + q8_0-KV-Cache für 32 768 Token (aus der Parameterzahl, mindestens 6 % der Modellgröße)
  + 0,5 GiB.
- **Stufen** (verglichen mit Gesamtspeicher, weil die Runtime andere Modelle vor dem Laden entlädt; Reserve 2 GiB pro
  GPU wie in `catalog.py`):
  - **Passt in VRAM** (grün): auf einer GPU, oder bei mehreren GPUs „Passt in VRAM · n GPUs“ (Layer-Split).
  - **Offload VRAM + RAM** (gelb): VRAM plus 85 % des RAM abzüglich 8 GiB Systemreserve. Der Tooltip nennt den RAM-Anteil.
  - **Zu groß** (rot): passt auch mit Offload nicht. Der Download bleibt möglich, nach einer Rückfrage.

## Downloads (`ModelDownloadService`)

- **Ablage:** wie der Hugging-Face-Cache, `<Ordner>\hub\models--unsloth--X\snapshots\<commit>\<Pfad>`, zuletzt `refs\main`.
  Der native Katalog (`workers/coding/catalog.py`) durchsucht diesen Ordner alle 10 s und vergibt die Id
  `coding/<Name>~sha256(Pfad relativ zu hub)[:12]`. `ModelHubCatalogId` rechnet genauso.
- **Zwischenspeicher:** `snapshots\<commit>\.download\<Pfad>.part`. Diese Dateien enden nicht auf `.gguf` und tauchen deshalb
  nie als halbes Modell auf.
- **Fortsetzen:**
  - Per HTTP-Range ab dem letzten Byte.
  - Die signierte CDN-Adresse wird jedes Mal neu über `resolve` geholt.
  - Pausierte und unterbrochene Downloads stehen in `DataDirectory\ModelHub\downloads.json`. Nach einem Neustart laufen
    unterbrochene von selbst weiter, pausierte warten.
- **Prüfung:** SHA-256 gegen den LFS-Hash. Bei Abweichung startet die Datei einmal neu, danach ist es ein Fehler.
- **Reihenfolge:** Begleitdateien und die übrigen Teile zuerst, der `-00001-`-Teil zuletzt. So sieht der Katalog das Modell
  erst, wenn es vollständig ist.
- **Vor dem Start (beim Klick):** Missum prüft den freien Platz auf dem Laufwerk des Download-Ordners. Gezählt wird, was
  noch geschrieben werden muss: vorhandene Dateien, übernommene Dateien eines älteren Snapshots und bereits geladene Teile
  zählen nicht. Dazu kommt, was laufende oder pausierte Downloads auf demselben Laufwerk noch brauchen, plus 5 GB Reserve.
  Reicht es nicht, erklärt ein Hinweis Bedarf und freien Platz, und nichts wird eingereiht. Beim tatsächlichen Start prüft
  der Download-Dienst noch einmal.
- **Abbrechen** löscht die `.part`-Dateien; **Pausieren** behält sie.
- **Nach dem Abschluss** fragt der Dienst `GET /v1/models/coding`, bis die Id erscheint, und zeigt dann „Bereit in Missum“.
  Liegt der Ordner nicht im AI-Modellordner, heißt es „Fertig – Ordner ist nicht der AI-Modellordner“.

## Einstellungen

- `ModelHubDownloadDirectory`: Download-Ordner. Ohne Wert gilt der AI-Modellordner (`LocalAiModelDirectory`), sonst
  `%USERPROFILE%\.cache\huggingface`.
- „Als AI-Modellordner verwenden“ setzt `LocalAiModelDirectory`. Der lokale Modellserver wechselt, sobald er im Leerlauf ist.

## Prüfung

- **Unit-Tests:** `ModelHubTests` deckt ab:
  - Gruppierung der Quantisierungen;
  - Katalog-Id gegen die bekannte Id `coding/Qwen3.8-Flash-Next-UD-Q2_K_XL~8838fac19bdc`;
  - Eignungsstufen;
  - `nvidia-smi`-Parser;
  - Download mit Pause, Range-Fortsetzung, Abbruch und Prüfsummenfehler gegen einen simulierten Server.

  `ModelHubModeTests` prüft Migration 61 und die Modus-Aliasse.
- **UI-Smoke:** `MISSUM_SMOKE_UI_MODELHUB=1` läuft gegen die echte Hugging-Face-API und prüft:
  - Liste, Sortierung und Suche;
  - Beschreibung und Eignungsabzeichen;
  - dass es kein Eingabefeld gibt;
  - einen echten kleinen Download mit Pause und Fortsetzen in einen temporären Ordner.

  Dazu schreibt er `native-modelhub-validation.json` und `modelhub-*.png`.
