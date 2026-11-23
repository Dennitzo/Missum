# Unreal Engine in Missum

Im Codex-Modus unter **+ → Programmintegrationen → Unreal Engine** wählen und einen
Projektordner öffnen. `unreal.status` zeigt erkannte Editorfenster, verfügbare
Engine-Installationen und den Epic Games Launcher. `unreal.connect` startet ein
steuerbares Arbeitsfenster und verbindet es. Eine vorhandene `.uproject` im
Projektordner wird geöffnet; bei mehreren Projekten `projectPath` und bei mehreren
Engine-Installationen `engineVersion` angeben. Ist noch kein Projekt vorhanden, legt
Missum im Projektordner ein Blueprint-Projekt mit der leeren Karte
`Content/Maps/Main.umap` an und trägt sie als Editor- und Spielstartkarte ein.

## Ein Kanal: Epics Python

Die Steuerung läuft ausschließlich über die von Epic mitgelieferte Python-Anbindung
der Engine. Missum startet den sichtbaren Editor mit

```
UnrealEditor.exe "<Projekt>.uproject" -ExecutePythonScript="<Paket>\runtime\unreal_bootstrap.py"
```

und verbindet sich danach über Loopback-TCP mit dem dort geöffneten Kanal. Es wird
**kein eigenes C++-Plugin gebaut**, weder `RunUAT.bat` noch `cmd.exe` sind beteiligt,
und es werden keine C++-Buildwerkzeuge benötigt. Der Editor bleibt zwischen
Aufrufen geöffnet.

Zwei Dinge sind für den Betrieb wichtig und in der Laufzeit berücksichtigt.

`-ExecutePythonScript` beendet den Editor nach dem Skript; die Engine prüft dazu den
Schalter `KeepPythonScriptAlive`. Die Laufzeit setzt ihn über
`unreal.EditorPythonScripting.set_keep_python_script_alive(True)`, damit das Fenster
stehen bleibt.

Der Kanal hat keinen eigenen Annahmefaden. Das Annahmesocket ist nichtblockierend
und wird über `unreal.register_ticker_callback` auf dem Spielthread gepumpt, mit
`register_slate_post_tick_callback` als Rückfall. Alle Editorzugriffe geschehen damit
auf dem Spielthread. Der Tokenvergleich läuft über `hmac.compare_digest`, Aufnahmen
und andere mehrschrittige Vorgänge werden über `operation.status` fortgeschaltet,
ohne den Thread zu blockieren.

## Gefundene Installationen

Die Enginesuche ist frei von festen Pfaden. Sie wertet die Launcher-Installationen,
den Registry-Zweig `HKLM\SOFTWARE\EpicGames\Unreal Engine` mit `InstallDirectory`,
die Umgebungsvariable `MISSUM_UNREAL_ENGINE_ROOT` sowie die Laufwerkswurzeln und die
üblichen Programm- und Benutzerordner bis zwei Ebenen aus, folgt dabei keinen
Verknüpfungspunkten und verwirft unlesbare Zweige. Jede Kandidatur wird über das
vorhandene `Engine/Binaries/Win64/UnrealEditor.exe` und `Engine/Build/Build.version`
verifiziert; akzeptiert werden nur Versionen aus der Reihe 5.

Der Epic Games Launcher wird in vier Stufen gesucht: laufender Prozess samt
Modulpfad, die Uninstall-Zweige von `HKLM` und `HKCU`, die Manifeste unter
`%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\Manifests` und zuletzt die bekannten
Installationsordner. Das Werkzeug `unreal.launcher.start` öffnet ihn, falls er nicht
läuft, und meldet Ausführungspfad, Erkennungsquelle und ob ein Fenster aktiv ist.
Der Launcher wird bewusst in einem Missum-Lauf geöffnet: ein Lauf mit
Computersteuerung darf nur die beiden Steuerwerkzeuge enthalten und kann ihn deshalb
nicht selbst starten.

## Rückbau eines früheren Editor-Plugins

Frühere Missum-Versionen haben ein eigenes C++-Plugin `MissumBridge` in das Projekt
gebaut. Beim ersten Verbindungsaufbau entfernt Missum den Ordner
`Plugins/MissumBridge` und den zugehörigen Eintrag in der `.uproject` — jedoch nur,
wenn die Besitzmarke `.missum-owned` den Ordner als eigenes Werk ausweist. Ein
gleichnamiger Ordner eines Nutzers bleibt samt Eintrag bestehen und wird gemeldet.
Zugleich setzt Missum `PythonScriptPlugin` in der Plugin-Liste der Projektdatei, wenn
es fehlt, und schreibt die Datei atomar ohne Bytezeichenmarkierung zurück.

## Werkzeuge

Karten: `unreal.level.list`, `unreal.level.create`, `unreal.level.open`,
`unreal.level.save`, `unreal.level.read`. Das Aufzählen unterscheidet Karten mit
Welt-Partition an den External-Actors-Ordnern.

Assets: `unreal.asset.list`, `unreal.asset.read`, `unreal.asset.import`. Importiert
werden FBX, obj und glTF aus Dateien innerhalb des Projektordners; Verknüpfungen im
Pfad werden aufgelöst, bevor die Grenze geprüft wird. Die Ablageverabredung für
Blender: das Blender-Werkzeug schreibt nach `Import/Blender` im Projektordner, von
dort holt `unreal.asset.import` die Datei.

Materialien: `unreal.material` mit den Operationen create, read, edit und assign.
Erzeugt werden Materialien mit Parameterausdrücken und
Instanzen auf einem Elternmaterial; `read` nennt Parameter, Ausdrucksknoten, die
Zuordnungen auf Basiseigenschaften und welche Schauspieler das Material in welchem
Feld verwenden.

Schauspieler: `unreal.actor` mit den Operationen place, read, set, remove und verify.
`verify` ist die datenbasierte
Gegenseite der Bildprüfung: tatsächliche Lage, Ausdehnung, umschlossene Grenzen,
belegte Materialfelder, Abstand zu Kamera oder Referenz und die Frage, ob der
Begrenzungsrahmen im Blickfeld liegt.

Licht, Himmel und Kamera: `unreal.light` mit create, set und read, `unreal.sky`
mit create und read, `unreal.camera` mit create, set und frame.

Blueprints und Klassen: `unreal.class` mit search sowie `unreal.blueprint` mit
create, read, edit, compile, save und template. Die Baupläne decken Spielmodus,
Spielfigur, NPC, Aufgabendaten und Aufgabenbauteil ab; beim Spielmodus setzt das
Werkzeug den Standardeintrag in `Config/DefaultEngine.ini` und liest ihn zurück.

## Aufnahmen und Sichtprüfung

`unreal.view.capture` arbeitet über den Editorweg `HighResShot` auf den aktiven
Viewport; für eine Kameraaufnahme wird der Viewport zuvor auf die Kamera gelegt und
danach wiederhergestellt. Das fertige PNG wird in die Missum-Aufnahmewurzel
übertragen und auf gültige PNG-Form, Größe und Identität von Projekt, Welt und
Editorinstanz geprüft. Jede Antwort nennt ihren Aufnahmezweig. Steht der Editorweg
nicht zur Verfügung oder läuft die Aufnahme nicht fristgerecht zu Ende, meldet das
Werkzeug das ausdrücklich; dann ist die Fensteraufnahme über `image.input` mit der
Fensterkennung des Editors der vorgesehene Rückfall. Eine Fensteraufnahme wird nie
als Editoraufnahme ausgegeben.

Das geladene PNG prüft `media.analyze`; der Befund geht mit `unreal.status` und
`visionReview` unter Angabe von Operation und Bildhash zurück. Eine Aufnahme, die
nicht zur offenen Änderung gehört, wird abgelehnt.

## Grenzen über Epics Python

Diese Engineversion stellt über Python keine Knotenerzeugung und keine
Pin-Verbindung in Blueprints zur Verfügung; es gibt keine SchemaAction- oder
Knotenfabrik-Klassen. Erlaubt bleiben Graphen anlegen und entfernen, Variablen,
Ereignisüberschreibungen und Kompilieren. `node.add`, `node.remove`, `pin.connect`
und `pin.disconnect` antworten mit dieser Fähigkeitsmeldung und dem Hinweis auf den
optionalen C++-Weg im Projekt. Ebenso ist das Speichern einer Karte unter neuer
Kennung über Python nicht vorgesehen; die Antwort weist darauf hin.

Was eine konkrete Engineversion tatsächlich freigibt, meldet der Status getrennt:
`launchChannel` für den Start, `editorCapabilities` für das, was der Editor
anbietet, und `capabilityLimits` für bekannte Grenzen.

## Paket bauen

```powershell
.\windows\package-unreal-integration.ps1 -PortableBuild -Install -DataDirectory "$env:LOCALAPPDATA\Missum"
```

Der Befehl veröffentlicht den Adapter, kopiert die Python-Laufzeit
(`unreal_bootstrap.py`, `unreal_ops.py`, `unreal_materials.py`, `unreal_capture.py`,
`unreal_light.py`, `unreal_blueprint.py`) in das Paket, prüft deren Vorhandensein und
signiert das `.aext` unter `artifacts\unreal-integration\package`. Ein Plugin-Binary
ist nicht mehr Teil des Pakets.

## Nachprüfung

Die tatsächlichen Prüfergebnisse dieser Umstellung, inklusive offener Punkte, stehen
in `docs/UNREAL-TOOL-VALIDATION.md`. Die Laufzeitlogik ohne Engine deckt
`tools/unreal-ops-selftest.py` ab; Kanallauf und Szenarien über den echten Adapter
lassen sich mit `tools/check-unreal-channel.ps1` wiederholen.

## Missum als Harness ohne eigenes Engine-Plugin (Stand 0.3.0)

Missum startet den Editor mit Epics eigenem `PythonScriptPlugin` (`-ExecutePythonScript=unreal_bootstrap.py`) und spricht über einen lokalen, tokengesicherten Socket mit ihm. Ein eigenes C++-Plugin ist nicht nötig.

| Weg | Werkzeug | Zweck |
| --- | --- | --- |
| Editor-Werkzeuge | `unreal.level`, `unreal.asset`, `unreal.actor`, `unreal.material`, `unreal.camera`, `unreal.light`, `unreal.sky`, `unreal.class`, `unreal.blueprint` | Strukturierte, quittierte Änderungen mit `operationId` |
| Editor-Python | `unreal.python` | Beliebiger Python-Code mit dem Modul `unreal` (Assets, Actors, Subsysteme, Projekteinstellungen, Imports). `print` und `result` kommen zurück, Änderungen laufen in einer Editor-Transaktion (Strg+Z). Prozessstarts und rekursives Löschen sind gesperrt. |
| Vision | `unreal.view.capture` + `image.input` + `media.analyze` | Aufnahme über `HighResShot`; liefert der Editor nach 12 s keine Datei, folgen `AutomationLibrary.take_high_res_screenshot` und ein `SceneCapture2D`-Render-Target. Scheitert alles, wird die Sichtprüfung als ausstehend gemeldet. |
| Computersteuerung | – | Nicht im Unreal-Lauf: Das Gateway erlaubt `computer.observe`/`computer.act` nur in einem isolierten Computersteuerungs-Lauf mit genau diesen beiden Werkzeugen (sonst `run.failed`). Klicks im Epic Games Launcher (`unreal.launcher.start` öffnet ihn) oder im Blueprint-Editor laufen im Modus Computersteuerung. |

## Brücke Blender → Unreal

1. Blender: `blender.scene.edit` mit `{"op": "project.export", "format": "fbx" | "glb" | "gltf" | "obj" | "usd", "objects": [...]}` schreibt nach `Import/Blender/<Name>.<ext>` im Projektordner (FBX mit Unreal-tauglichen Einstellungen: Einheiten skaliert, keine Leaf-Bones, Flächenglättung). Ziele außerhalb des Projektordners werden abgelehnt.
2. Unreal: `unreal.asset` mit `operation: "import"` und `filePath: "Import/Blender/<Name>.fbx"` legt das Asset unter `/Game/SourceAssets/Blender` an; `unreal.actor` `place` setzt es in die Szene, `unreal.view.capture` prüft das Ergebnis.

## Livetest MissumDivided (Stand 0.3.4)

- **Werkzeugwahl:** Nennt ein Codex-Prompt ein installiertes Programm (Unreal Engine, UE5, .uproject,
  Epic Games Launcher, Blender), wählt Missum dessen Integration, bevor die Desktop-Klassifizierung
  gefragt wird. Vorher landete „Erstelle ein neues Unreal Engine Projekt“ in der Computersteuerung.
- **Aufrufprüfung:** Der Launcherpfad heißt im Aufruf `launcherPath`; `executable` galt als
  Prozessanforderung und ließ jeden Unreal-Aufruf scheitern. Unreal-Paketpfade (`/Game`, `/Engine`,
  `/Script`) in `folder` gelten nicht als Dateipfade.
- **Neues Projekt:** Die leere Startkarte entsteht mit `new_level(Assetpfad, WorldPartition)`.
- **Materialien:** Farben als `{r,g,b}`, `[r,g,b]` oder `#RRGGBB`; eine Farbe ohne r/g/b wird
  abgelehnt statt weiß. `edit` ohne Teiloperation setzt Startwerte (`values`), `create` mit
  `replace` übernimmt sie und ergänzt die Emissive-Kette.
- **Licht:** Intensität, Temperatur (mit `use_temperature`), Radius, Kegel, Schatten und Farbe
  werden am Lichtbauteil gesetzt, nicht am Actor.
- **Blueprints:** Bauteile (auch geerbte) kommen über `SubobjectDataSubsystem`. Knotengenaue
  Graphverdrahtung bleibt über Epics Python gesperrt; der Agent weicht auf ein C++-Modul aus.
- **Editor-Python:** Bei `AttributeError` nennt das Ergebnis passende vorhandene Namen.
- **Computersteuerung:** Eine Beobachtung bleibt 15 Minuten gültig (lokale Vision-Modelle brauchten
  rund 2,5 Minuten bis zur Aktion); Vordergrund- und Pixelprüfung am Zielpunkt bleiben bestehen.

## Sitzungsprüfung MissumDivided (Stand 0.3.5)

Befunde aus den Werkzeugaktionen des Agenten und ihre Korrekturen:

- **Bildnamen im Chat:** Der Medienworker nennt jedes Original `original.png`. Der Chat zeigt jetzt den Namen, mit dem image.input das Bild geladen hat (`primaryUpload.fileName`, sonst Pfad des image.input-Schritts). Die Analysetexte gehörten nachweislich zum jeweiligen Bild (gleiche SHA-256). Verwirrend waren die Bilder selbst: der Agent hatte eigene `HighResShot`-Dateien aus `Saved/Screenshots` geraten, darunter ein altes Spielvorschaubild.
- **Drehungen:** `unreal.Rotator` erwartet in Python positionell (roll, pitch, yaw). `make_rotator` und der Sonnenstand übergeben jetzt Schlüsselwörter. Vorher wurden der Nickwinkel als Rollwinkel und der Gierwinkel als Nickwinkel gesetzt (`camera.frame` lieferte pitch 45 / roll 155).
- **Kameraaufnahme:** Die SceneCapture-Rückfallstufe scheiterte für `source=camera` immer (Ort und Drehung lagen als Wörterbuch vor) und wartete dann 120 s. Beides ist behoben; ein Fehlschlag der letzten Stufe meldet sich sofort mit allen Versuchen.
- **Aufnahmedateien:** `HighResShot` und die Automationsaufnahme schreiben mit eindeutigem Dateinamen. Nur eine Datei mit diesem Namen gilt als die Aufnahme, nie eine verspätete fremde Datei.
- **Wiederholte Aufnahmen:** Eine bereits vorliegende, ungeprüfte Aufnahme blockiert keine neue mehr (`vision_operation_mismatch` entfällt).
- **Spielvorschau:** `unreal.view.capture` meldet `unreal.pie_running` statt „keine Editorwelt“. `unreal.python` mit eigenem `HighResShot` liefert einen Hinweis (`captureNotice`).
- **camera.frame:** ohne `cameraName` richtet es den Editor-Viewport aus. Himmel, Wolken, Nebel, Lichter und Weltverwaltung zählen nicht mehr zur Ausdehnung.
- **coding.read:** liest Dateien, die ein anderes Programm zum Schreiben offen hält (Unreal-Log).
- **Prompt:** Hinweise zur Rotator-Reihenfolge und zur Blickrichtung (yaw 90 = +Y) sowie zu unreal.view.capture statt eigenem HighResShot.

Live geprüft im Testprojekt bridge-ws: Kamera mit pitch -35/yaw 90 wird exakt so angelegt, frame für Kamera und Viewport zeigt die Landschaft, Viewport- und Kameraaufnahme liefern Bilder, zwei Aufnahmen mit eigenen operationIds hintereinander laufen durch.
