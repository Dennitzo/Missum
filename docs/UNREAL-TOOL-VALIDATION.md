# Unreal-Werkzeug auf Epics Python — Prüfnachweis

Stand: Lauf vom 22.11.2026, Engine Unreal 5.8.4 (`E:\UnrealEngine\UE_5.8`),
Testprojekt `C:\Users\AMD\Documents\GitHub\GrandTheftCyberpunk`.

## Kanal und Umgebung

Die Erweiterung arbeitet ausschließlich über Epics Python: der Editor startet mit
`-ExecutePythonScript=` auf die Laufzeit unter `extensions/unreal/runtime/`, die
Quittung (Bereitschaft, Kanal, Fähigkeiten) landet in einem temporären Prüfverzeichnis.
Die Quittung meldet `channel.type=python`, `runtimeVersion=0.2.0`, Pumpe
`register_ticker_callback` und Transport `loopback-tcp`; rund 46 Fähigkeiten.
Bekannte Stolpersteine sind im Code und hier dokumentiert: `-NoShaderCompile` verursacht
eine Assertion beim Editorstart und darf nicht gesetzt werden; der Editor beendet sich
nach dem Skript selbst, deshalb hält die Laufzeit ihn mit
`EditorPythonScripting.set_keep_python_script_alive(True)` am Leben.

## Werkzeugkatalog

Das Gateway erlaubt höchstens 16 Modellwerkzeuge pro Aktion
(`ExtensionManifestValidator.MaximumToolsPerAction`). Der Katalog ist deshalb auf
13 Gruppenwerkzeuge konsolidiert: status, connect, launcher.start, level, asset,
material, actor, camera, light, sky, class, blueprint, view.capture. Die Teiloperation
steht im Feld `operation`; für Blueprint- und Materialbearbeitung trägt das Feld `edit`
die eigentliche Teiloperation. Der Adapter übersetzt das in die granularen
Editorbefehle und entfernt die Steuerfelder. Ältere Szenarien unter
`tools/scenarios/` mit granularen Namen wie `unreal.actor.place` laufen mit dem
neuen Adapter nicht mehr; die maßgeblichen Nachweise liegen in den Szenarien
`phaseX-grouped-tools.json`, `phaseY2-quest-component.json`, `phaseS-sky-light.json`
und `phaseW-verify.json`.

## Geprüfte Fähigkeiten

Karten und Schauspieler: Öffnen, Platzieren, Lesen, Ändern, Entfernen und Speichern
liefen mehrfach fehlerfrei; die Karte `MissumCityGrid` meldete zuletzt 20 Schauspieler.
`actor.verify` meldet Lage, Ausdehnung, Materialfelder, Abstand zur Kamera und
Blickfeldlage; die Zuordnung von `M_NeonCyan` und `M_ConcreteGrey` auf die richtigen
Felder zweier Geometrien ist datenbasiert belegt. Materialien mit Parameterausdrücken
und Instanzen sind über die Materialbearbeitung erzeugt und zugewiesen worden.
Himmel und Licht: `sky.create` mit Sonnenhöhe −90 meldet `isNight=true`, die
Himmelsablesung zählt je System eine Instanz, `light.read` und `level.save` laufen grün.
Blueprints: `BP_MissionGameMode`, `BP_PlayerCharacter`, `BP_NPC_Citizen` und
`BPC_QuestTracker` liegen als Assets im Projekt; der Spielmodus steht in
`Config/DefaultEngine.ini`. Graphen (`TickProgress` und weitere) werden angelegt und
kompiliert, das Speichern läuft über das Objekt, nicht über den Pfad.

## Grenzen über Epics Python

Knotenerzeugung und Pin-Verbindung gibt es in dieser Engineversion nicht; es fehlen
SchemaAction- und NodeFactory-Klassen. Variablen lassen sich nicht anlegen: der Probebeweis
zeigt, dass `EdGraphPinType` die Eigenschaft `pin_category` für Python nicht exponiert
(Fehler „Failed to find property ‚pin_category'“), `add_member_variable` scheitert damit
systematisch; die Bauplanvorlagen melden das je Variable mit der echten Fehlermeldung.
Datenassets (`DT_QuestObjective`) lassen sich nicht erzeugen: die Python-Datenfabrik
öffnet ohne gesetzte Klasse einen modalen Auswahldialog und blockiert den Kanal; der
Weg ist deshalb abgeschirmt und bricht mit klarer Fehlermeldung ab. Aufnahmen: der
Editorweg `HighResShot` lieferte im Editor ohne laufendes Spiel keine Datei, und das
Editorfenster stand der Fensteraufnahme nicht offen; die Sichtprüfung wird deshalb
korrekt mit `verdict=unavailable` gebucht (`matchesCapture` greift), die Platzierung
selbst ist datenbasiert über `actor.verify` bestätigt.

## Launcher und Fab

Der Launcher-Locator findet die Installation ohne feste Pfade über laufenden Prozess,
Uninstall-Registry, Launcher-Manifeste und bekannte Ordner; die ausführbare Datei ist
auf diesem Rechner unter dem Portal-Pfad vorhanden, der Prozess lief während der
Prüfung nicht. Der Start läuft über das Anwendungsprogramm, das den Locator-Befund an
den Adapter übermittelt; im Prüfwerkzeug-Kontext fehlt dieser Befund, deshalb meldet
`unreal.launcher.start` dort berechtigt „nicht gefunden“. Der Fab-Durchlauf per
Computersteuerung ist bewusst interaktiv und bleibt offen; alles Vorbereitende
(Locator, Start, Kanal) ist geprüft.

## MissumBridge-Rückbau

Der frühere eigene C++-Editor-Speicher ist vollständig entfernt: im Testprojekt
existiert `Plugins/MissumBridge` nicht, die `.uproject` enthält keinen Eintrag, die
Besitzmarke `.missum-owned` ist gelöscht. Die Entnahme-Logik in
`UnrealApplicationLauncher.cs` bleibt als Schutz für fremde Projekte bestehen; ein
Live-Test im Testsuite verlangt das Fehlen beider Spuren.

## Testnachweise

Gezielte Läufe: `UnrealIntegrationTests`, `UnrealPythonChannelTests` und der
Akzeptanzkatalog-Test in `tests/Missum.Tests` (9 von 9 bestanden), dazu
`UnrealVisionReviewTests` in `tests/Missum.Ai.Server.Tests` (bestanden) und der
Adapter-Bau ohne Fehler. Vom Unreal-Auftrag unabhängige Testfehler in der Gesamtsuite
(Dokumentformel- und Artefakt-Tests, Neustart- und Wissenschaftsklient-Tests) sind
nicht Teil dieser Änderung und bleiben offen; sie wurden hier weder reparabel benannt
noch als erledigt ausgegeben. Zusätzlich wurde die Abdeckliste der tragbaren
Werkzeugakzeptanz um den fehlenden Eintrag der Computersteuerung ergänzt.
