# Messung der Kontext-Effizienz

Der Vergleich ist vorbereitet, aber noch nicht mit echten Vergleichsläufen abgeschlossen. Die bisherigen Beobachtungen in `artifacts/context-efficiency/baseline-runtime.json` enthalten unter anderem abgebrochene General-Läufe und keine vollständigen nativen Turn-Metriken. Sie belegen Kontextgrößen und Cache-Nutzung, keinen Zeitgewinn eines vollständigen Arbeitsablaufs.

`windows/measure-context-efficiency.py` liest ausschließlich das Gateway-Journal. Es lädt keine Modelle, startet keine Anwendung und stellt keine Run-Anfragen. SQLite wird mit `mode=ro`, `query_only=ON` und einer konsistenten Lesetransaktion geöffnet. Nur ausdrücklich angegebene Berichtdateien werden geschrieben.

## Freigabe und Vergleichsvarianten

`MissumAiServerOptions.EnableCompactContextProfile` ist standardmäßig `false`; das Gateway bietet `compact-v1` nur mit `MISSUM_AI_COMPACT_CONTEXT_PROFILE=true` an (außerhalb Compose wird auch `MissumAiServer__EnableCompactContextProfile` akzeptiert). Ungültige Werte brechen den Start mit einer konkreten Diagnose ab; der Compose-Standard bleibt `false`. Ein fehlendes `RunRequest.ContextProfileVersion` bleibt der Legacy-Pfad. Ein ausdrücklich angefordertes `compact-v1` erlaubt einen isolierten Kandidatenlauf. Die allgemeine Freigabe folgt erst nach erfolgreichen, vollständigen Vergleichen.

Für beide Varianten dieselbe neue Gateway-Version mit vollständiger Instrumentierung verwenden: Legacy gegen `compact-v1`. Die alte EXE und das alte Image bleiben Referenz und Rückfallmöglichkeit. Die alten General-Beobachtungen können vollständige neue Legacy-Messungen nicht ersetzen.

Science muss in beiden Varianten durch den tatsächlichen Clientpfad laufen. Der kompakte Client unterdrückt seine bisherigen langen Anleitungen, wenn das Gateway das Profil anbietet. Deshalb reicht es nicht, bei identischem bereits kompaktem Science-Request nur `contextProfileVersion` umzubenennen. Für Legacy die tatsächliche Legacy-Clientanleitung verwenden, für den Kandidaten die ausgehandelte kompakte Anleitung; Nutzerdaten, Aufgaben und Abnahmekriterien bleiben gleich.

## Szenarien und Durchführung

Je Modus ein festes, repräsentatives Szenario vorbereiten. General umfasst initiale Aufgabe und Folgeprompt, Coding den tatsächlichen Lese-/Bearbeitungs-/Prüfablauf, Science Grundlagen, eine belegte Prüfung, PDF und Wiederaufnahme. Zusätzliche Folgeprompts oder Wiederaufnahme für General/Coding nur aufnehmen, wenn das gewählte Szenario sie verlangt. Die unveränderte Szenariodatei enthält alle Prompts, Ausgangsdateien, erwarteten Ergebnisse und die Abnahme. Ihre SHA-256 und die SHA-256 der Abnahmekriterien stehen im Manifest. Keine zufälligen Promptänderungen zwischen Wiederholungen.

| Modus | Repräsentativer Arbeitsablauf | Tatsächliche Abnahme |
| --- | --- | --- |
| ChatGPT / General | Eine Erklärung anhand fest gespeicherter Ausgangsdaten, einschließlich gezieltem Quellenbezug und nachvollziehbarer Berechnung; Folgefrage mit Bezug auf das Ergebnis. | Korrekte Aussagen, Quellenbezug und Rechnung, korrekte Folgeantwort, erhaltene Sitzung und Werkzeugrechte. |
| Codex / Coding | Eine konkrete kleine Fehlfunktion in einer identischen Arbeitskopie lesen und beheben; passenden vorhandenen Check ausführen. | Tatsächliche Dateidifferenz, bestandener gezielter Check, erhaltene Arbeitskopie und Berechtigungen. |
| Claude / Science | Ein festes wissenschaftliches Problem mit begrenzten erforderlichen Zielen bearbeiten, eine belegte Prüfung und kanonische Publikation erstellen; gezielte Abschnittsänderung im Folgeprompt und Wiederaufnahme. | Akzeptierte Abschnitte und Ziele, echte passende Prüfbelege, aktuelles validiertes PDF, erhaltene kanonische Revisionen. Numerik nur dann zwingend, wenn das Szenario sie benötigt. |

Das ist keine zusätzliche breite Werkzeugsuite. Die Laufzeitmessung verwendet die vorhandenen gezielten Abnahmen. Zusätzlich separat unter `technicalCoverage` Belege für `subagentResultHandoff` (tatsächliche Aufgabenübergabe und direkte Ergebnisübernahme) und `noSecondaryGpu` (korrekte Fortsetzung ohne zugelassene zusätzliche GPU/Replika) aufbewahren. Diese technischen Belege sind keine Pflicht, in jedem General-/Coding-Prompt künstlich einen Child zu starten. GPU0/GPU1-Belegung und das Fehlen einer dritten GPU müssen mit echten Runtime-/Hostbelegen geprüft werden; ein benannter `gpu0Gpu1Only`-Boolean beweist das für sich nicht. Ein erfolgreiches Werkzeug allein ersetzt keine Aufgabenabnahme.

Pro Modus und Cache-Bedingung drei unabhängige Legacy/Kandidat-Paare ausführen: drei Modi × kalt/warm × drei Paare = 18 Paare. Nicht denselben Run mehrfach verwenden. Alle Wiederholungen eines Modus verwenden identisches Modell, Reasoning-Effort, Szenario und Abnahmekriterien, auch über kalt/warm hinweg.

Die Reihenfolge Legacy/Kandidat zwischen Wiederholungen wechseln, damit Aufwärmung und thermische Effekte nicht systematisch einer Variante zugutekommen. Dieselbe Hardware, GPU-Belegung, Modellversion, Kontextgrenze, Werkzeugrechte und Ausgangsdateien verwenden. Hintergrundarbeit vermeiden und Modellresidenz protokollieren. Warmup und Cache-Vorbereitung außerhalb des gemessenen Szenarios durchführen und für beide Varianten gleich halten.

* Kalt: erster tatsächlich gemessener Root-Turn hat `cachedPromptTokens=0`.
* Warm: erster Root-Turn hat mindestens 50 % tatsächlich gemessene Wiederverwendung (`cachedPromptTokens / inputTokens`). `minimumWarmReuseFraction` kann von 0.5 bis 1 strenger gewählt werden.
* Folgeprompts: in jedem Szenario dieselbe Reihenfolge und inhaltliche Abhängigkeit. Sie können ihrerseits Cache nutzen; die kalt/warm-Klassifikation bezieht sich auf den ersten Turn des gesamten Szenarios.
* Wiederaufnahme: bestehende Sitzung und kanonischen Zustand fortsetzen. Ein Gateway-Neustart darf keinen stillschweigenden Wechsel von Profil, Aufgabe oder Rechten verursachen.

## Manifest und Belege

Eine noch unvollständige Vorlage erzeugen:

```powershell
python windows/measure-context-efficiency.py --example-manifest --output artifacts/context-efficiency/scenarios.json
```

Die Vorlage enthält die 18 benötigten Plätze. `null`-Felder mit den tatsächlichen Identitäten und Belegen füllen. Ein Seitenobjekt kann genau einen `runId` oder eine geordnete Liste `runIds` für initiale Aufgabe, Folgeprompt und Fortsetzung enthalten. Die Runs müssen zeitlich aufeinanderfolgen und vollständig abgeschlossen sein. Child-Runs stehen nicht in dieser Liste; der Sammler ermittelt sie anhand ihrer dauerhaft gespeicherten Parent-Beziehung.

```json
{
  "id": "science-warm-1",
  "mode": "science",
  "cache": "warm",
  "fixtureId": "science-representative-v1",
  "fixtureSha256": "<SHA-256 der unveränderten vollständigen Szenariodatei>",
  "acceptanceSha256": "<SHA-256 der unveränderten Abnahmekriterien>",
  "modelId": "<tatsächliche kanonische Modell-ID>",
  "reasoningEffort": "high",
  "requiredChecks": ["taskComplete", "permissionsPreserved", "endToEndTimingVerified", "foundationsPdf", "scientificCheck", "restartContinuation"],
  "before": {
    "runIds": ["<initialer Legacy-Run>", "<Folgeprompt>", "<Fortsetzung>"],
    "acceptanceFile": "evidence/science-warm-1-before.json",
    "acceptanceEvidenceSha256": "<tatsächliche SHA-256 dieser Belegdatei>",
    "firstPdfFile": "evidence/science-warm-1-before-first-pdf.json",
    "firstPdfEvidenceSha256": "<tatsächliche SHA-256 der PDF-Erfassungsdatei>"
  },
  "after": { "<gleiche Felder mit den tatsächlich gemessenen Kandidaten-Runs>": "" }
}
```

Die Abnahmedatei wird nach der tatsächlichen Aufgabenprüfung durch den vorhandenen Test/Harness erstellt. Sie bindet das Ergebnis an dieselben Fixture- und Kriterienhashes und an die **vollständige geordnete** `runIds`-Liste. Bei einer einzigen Anfrage stattdessen `runId` verwenden. `passed` und jeder verlangte Check müssen echte JSON-Booleans `true` sein. Freitext des Modells gilt nicht als Abnahme.

```json
{
  "runIds": ["<initialer Run>", "<Folgeprompt>", "<Fortsetzung>"],
  "passed": true,
  "fixtureSha256": "<Fixture-SHA-256>",
  "acceptanceSha256": "<Kriterien-SHA-256>",
  "checks": {
    "taskComplete": true,
    "permissionsPreserved": true,
    "endToEndTimingVerified": true,
    "foundationsPdf": true,
    "scientificCheck": true,
    "restartContinuation": true
  },
  "clientTiming": {
    "captureMode": "actual-ui-submit-to-visible-completion",
    "legs": [{"runId": "<zugehöriger Root-Run>", "submittedAt": "<erfasst vor Clientvorbereitung>", "firstVisibleAnswerAt": "<tatsächliche sichtbare Antwort>", "visibleCompletedAt": "<sichtbarer Abschluss>", "milliseconds": "<tatsächlich gemessene Dauer>"}]
  }
}
```

Die Vorlage verlangt gemeinsam `taskComplete`, `permissionsPreserved` und `endToEndTimingVerified`. Dazu General: `followupComplete` und `targetedSource`; Coding: `readEditVerified`; Science: `foundationsPdf`, `scientificCheck`, `restartContinuation`. Weitere vorhandene gezielte Checks können ergänzt werden. Die zugehörigen Rohbelege, Dateihashes und Prüfergebnisse aufbewahren; der Sammler prüft die Hashbindung und Checkwerte, führt die fachliche Abnahme und UI-Erfassung aber nicht erneut aus.

Für **beide Seiten jedes Science-Paars** zusätzlich eine echte Erfassung des ersten akzeptierten PDFs liefern. Der Harness muss bei dessen erstmaliger erfolgreicher Validierung innerhalb dieses Szenarios den Zeitpunkt erfassen. Projekt-Erstellungszeit und Zeitstempel eines schon vorhandenen PDFs sind dafür ungeeignet.

```json
{
  "runId": "<zugehöriger Root-Run innerhalb des Szenarios>",
  "runIds": ["<initialer Run>", "<Folgeprompt>", "<Fortsetzung>"],
  "captureMode": "run-scoped-first-pdf",
  "passed": true,
  "startedAt": "<exaktes run.started des ersten Root-Runs>",
  "firstPdfAt": "<erfasster Zeitpunkt der erstmaligen erfolgreichen PDF-Abnahme>",
  "pdfPath": "<unveränderliche PDF-Kopie, relativ zur Belegdatei oder absolut>",
  "pdfSha256": "<tatsächlich berechnete SHA-256 dieser Kopie>"
}
```

Bei einem einzigen Run `runIds` weglassen. `firstPdfAt` muss zwischen dem ersten Root-Start und der letzten dauerhaften Completion liegen. Der Sammler prüft diese Bindung, den PDF-Header und die tatsächliche Datei-SHA-256. Die PDF-Inhalts- und Darstellungsprüfung bleibt Aufgabe der bestehenden echten Abnahme; ein PDF-Header allein belegt keine wissenschaftliche Qualität. Bei einzelnen Beobachtungen ist eine solche Zusatzmessung optional; ohne sie bleibt ein Science-Vergleich unvollständig.

## Bericht lesen

```powershell
python windows/measure-context-efficiency.py `
  --database C:/ProgramData/Missum-AI-Stack/data/database/missum-ai-server.db `
  --manifest artifacts/context-efficiency/scenarios.json `
  --baseline artifacts/context-efficiency/baseline-runtime.json `
  --output artifacts/context-efficiency/comparison.json
```

Einzelne vorhandene Runs rein lesend beobachten:

```powershell
python windows/measure-context-efficiency.py --database C:/ProgramData/Missum-AI-Stack/data/database/missum-ai-server.db --run <run-id>
```

Der Vergleich verwendet `gatewayActiveMilliseconds`: die Summe aller sequenziellen Gateway-Anfragen von ihrer tatsächlichen Speicherung bis zur dauerhaften Completion. Damit sind Vorbereitung im Gateway, Modellturns, Werkzeuge, Client-Wartezeiten und Child-Wartezeiten enthalten. Die zusätzliche `gatewayEnvelopeMilliseconds` umfasst auch Lücken zwischen Folgeprompts. Solche Nutzerpausen gehen nicht in die aktive Vergleichsdauer ein.

**Nicht gemessen:** Clientstart und Vorbereitung vor dem Absenden der API-Anfrage. Die Release-Freigabe verlangt zusätzlich eine tatsächliche UI-Erfassung vom Nutzer-Absenden **vor** der Clientvorbereitung bis zum sichtbaren Abschluss für sämtliche Workflow-Anfragen. Deren rohe Zeitbelege und passende Run-IDs stehen in der gebundenen Abnahmedatei; `endToEndTimingVerified` wird erst nach dieser Prüfung gesetzt. Der Sammler berechnet diese UI-Zeiten nicht selbst. Ein schneller Gateway-Vergleich allein reicht nicht, `EnableCompactContextProfile` allgemein einzuschalten. Ohne zusätzlichen UI-Beleg ist auch keine Aussage über die komplette Dauer vom Appstart bis zum Ergebnis erlaubt. Reine Inferenzzeiten werden ebenfalls nicht als Gesamtdauer bezeichnet.

`firstVisibleAnswerMillisecondsFromRequest` basiert auf der ersten tatsächlichen nichtleeren `text.delta` des Hauptagenten und enthält keine Antworttexte im Bericht. Weitergeleitete Child-Texte und Reasoning zählen nicht. Dieser Wert ist getrennt von nativem `timeToFirstTokenMilliseconds`, das auch versteckte Generierung oder einen Werkzeugaufruf betreffen kann. Die zusätzliche Science-PDF-Erfassung misst den tatsächlich akzeptierten ersten PDF-Erfolg.

Native Arbeit wird pro tatsächlicher `model.turn.metrics`-EventId gezählt, auch wenn mehrere native Antworten dieselbe logische Runde haben. Passende `coding.metrics`-Duplikate werden ausgeschlossen, eigenständige Summarization-Metriken bleiben erhalten. Legacy-Coding-Metriken werden verwendet, wo neue Metriken fehlen. Weitergeleitete Child-Events werden nicht nochmals gezählt; die eigenen Child-Journalzeilen werden gelesen. Parallel ausgeführte native Millisekunden werden als `summedNativeWorkMilliseconds` ausgewiesen und niemals zur Wall-Time addiert.

Fehlende native Zeit- und Tokenwerte bleiben `null`. Reasoning-Tokens dürfen fehlen, weil manche Provider sie nicht getrennt melden. Vollständige übrige native Metriken, abgeschlossene Root- und Child-Runs sowie ausreichende Tokenabdeckung gegenüber der Terminalquittung sind Pflicht. Zusätzliche gemessene native Arbeit gegenüber logischen Terminalzählern wird sichtbar ausgewiesen. Bekannte unterbrochene Retry-Versuche ohne vollständige eigene Metriken sperren die vollständige Vergleichsaussage.

`sourceActions`, `repeatedSourceActions`, native Retries und Fallbacks stammen ausschließlich aus vorhandenen Journalereignissen. Diese Zähler beweisen keine fachliche Qualität; dafür sind die gebundenen Abnahmen erforderlich.

Nur wenn alle 18 unabhängigen Paare erfolgreich und vollständig sind, ist `canClaimSpeedComparison=true` für den gemessenen Gateway-Vergleich. Je Modus/kalt/warm wird der Median von Kandidatdauer / Legacydauer gezeigt. `allModeCacheGroupsFaster=true` verlangt einen Median kleiner als 1 in **allen sechs** Gruppen. Für die Release-Freigabe müssen zusätzlich die geprüften UI-Gesamtzeiten dieselben erfolgreichen Arbeitsabläufe schneller abschließen und die technischen Belege vorliegen. Ein schneller Einzelturn, ein kleinerer Startkontext oder eine teilweise Gruppe genügt nicht. Bei fehlenden Belegen lautet der Status `incomplete`, Vergleichsaussagen bleiben aus und der Prozess endet mit Code 2. Das Skript schaltet kein Profil automatisch ein.

Die gezielten reinen Python-Tests starten keinerlei Runtime:

```powershell
python -m unittest discover -s tests -p test_context_efficiency_metrics.py -v
```

## Opt-in-Liveharness nach dem Portable-Build

`ContextEfficiencyLiveTests.PublishedNativeClientCompletesMatchedContextWorkflowsAndWritesBoundEvidence` startet die veröffentlichte Anwendung in eigenen Profilen. Der Native-Partial verwendet den unveränderten Composer-/`SendAsync`-Pfad. Er beobachtet tatsächlich geladene Antwortblöcke bei `CompositionTarget.Rendering`; er erzwingt keine frühere Darstellung. Damit stammen Submit-, erste sichtbare Antwort- und Completion-Zeiten aus WinUI, einschließlich Clientvorbereitung. Science wird zur Fortsetzung wirklich beendet und mit derselben eigenen Datenbank wieder gestartet. Der kanonische Zustand muss vor der Fortsetzung exakt erhalten sein.

Zwei Gatewayinstanzen desselben instrumentierten Images mit getrennten Datenbanken verwenden: Legacy ohne kompaktes Capability-Angebot, Kandidat mit `compact-v1`. Beide dürfen denselben nativen Worker nutzen, müssen dann aber **sequenziell** arbeiten. Die Instanzen isolieren keine GPU-/KV-Slots. Das Harness alterniert AB/BA zwischen den drei Wiederholungen und stoppt beim ersten fachlichen Fehlschlag. `progress.json` meldet Fallstart, Erfolg oder Abbruch ohne Prompts; ausführliche Belege liegen im jeweils angegebenen eigenen Artefaktordner.

Erforderliche Umgebungsvariablen nur für die gezielte Testinvokation setzen und danach wieder entfernen:

```powershell
$env:MISSUM_CONTEXT_BENCHMARK_LIVE = '1'
$env:MISSUM_CONTEXT_BENCHMARK_EXCLUSIVE_RUNTIME = '1'
$env:MISSUM_CONTEXT_BENCHMARK_EXE = '<absoluter Pfad zur frisch geprüften Portable Missum.exe>'
$env:MISSUM_CONTEXT_BENCHMARK_EVIDENCE = '<neuer absoluter Evidence-Ordner>'
$env:MISSUM_AI_LIVE_GENERAL_MODEL = '<kanonische installierte General-Modell-ID>'
$env:MISSUM_AI_LIVE_CODING_MODEL = '<kanonische installierte Coding-Modell-ID>'
$env:MISSUM_CONTEXT_BENCHMARK_REASONING = '<ausdrücklich gewählter unterstützter Reasoning-Effort>'
$env:MISSUM_CONTEXT_BENCHMARK_LEGACY_DATABASE = 'C:/Users/AMD/Documents/GitHub/Missum/artifacts/context-efficiency/runtime/legacy-gateway/database/missum-ai-server.db'
$env:MISSUM_CONTEXT_BENCHMARK_COMPACT_DATABASE = 'C:/Users/AMD/Documents/GitHub/Missum/artifacts/context-efficiency/runtime/compact-gateway/database/missum-ai-server.db'
# Defaults: Legacy18080, Compact18081, Native8081, Control8082.
dotnet test tests/Missum.Tests/Missum.Tests.csproj -c Release -p:Platform=x64 --no-build --filter FullyQualifiedName~ContextEfficiencyLiveTests
```

`MISSUM_CONTEXT_BENCHMARK_MODE=general|coding|science`, `MISSUM_CONTEXT_BENCHMARK_CACHE=cold|warm` und `MISSUM_CONTEXT_BENCHMARK_REPETITION=1|2|3` begrenzen einen ersten gezielten Versuch. Standard `all` führt alle 18 Paare aus. Eine begrenzte Manifestdatei ist ausdrücklich ein unvollständiger Vergleich.

Cold-Vorbereitung setzt die zuvor ausdrücklich reservierte, freie Runtime voraus: gegebenenfalls Replika und Hauptinstanz einzeln über den bestehenden Router `/models/unload` entladen, auf tatsächliche Entladung warten, danach den normalen Worker `/models/load` verwenden. Es gibt keinen rohen KV-Slot-Erase-Bypass und keine Änderung fremder Sitzungsdateien. Die Modellvorbereitung liegt außerhalb der anschließenden Submit-Zeit. Für Warm wird im selben eigenen Sessionkey ein festes dokumentiertes Prelude abgeschlossen; die gemessene eigentliche Aufgabe ist neu und enthält eigene Abnahmen. Die tatsächliche Native-Metrik entscheidet weiterhin, ob kalt/warm erfüllt ist.

Die gezielten Abnahmen prüfen General-Quellenbeleg plus echte 9-J-/36-J-Werkzeugresultate und Folgeantwort; Coding unveränderte Testbytes, tatsächliche Änderung, gemeldete Werkzeugausführung und unabhängige drei unittest-Tests; Science echte 9-J-Prüfung, aktuelle inhaltliche Grundlagen-/Herleitungs-PDF und persistente Fortsetzung ohne verpflichtende Simulation. Der erste beobachtete neue kanonische PDF-Stand wird sofort als unveränderliche Kopie gesichert und anschließend mit PdfPig inhaltlich geprüft. Kopflose Coordinator-Events oder wiedergegebene Alttexte gelten nicht als native Zeitmessung.

Das Harness schreibt `native-workflows-manifest.json`, gehashte Abnahmen und Science-PDF-Erfassungen passend zum Sammler. Jede `before`-/`after`-Referenz enthält zusätzlich `database`, den absoluten Pfad ihrer tatsächlichen Gateway-Datenbank. Der Sammler öffnet beide ausschließlich lesend; `--database` bleibt der Fallback für Referenzen ohne eigenen Datenbankpfad. Echte Runtime-/Aufgabenübergabe- und Ressourcen-Fallbackbelege bleiben die separate `technicalCoverage`; ein Proxy-Deny wäre simulierte Admission und darf nicht als physischer Ein-GPU-Test bezeichnet werden.
