# Unreal Engine in Missum

Im Codex-Modus unter **+ → Programmintegrationen → Unreal Engine** wählen und einen
Projektordner öffnen. Mit `unreal.status` lassen sich erkannte Editorfenster und
Engine-Versionen abfragen. `unreal.connect` startet ein steuerbares Arbeitsfenster.
Eine vorhandene `.uproject` im Projektordner wird geöffnet. Bei mehreren Projekten
`projectPath` und bei mehreren Engine-Installationen `engineVersion` angeben.
Ist noch kein Projekt vorhanden, wird im Projektordner ein Blueprint-Projekt
mit einer leeren Karte `Content/Maps/Main.umap` angelegt. Diese Karte wird als
Editor- und Spielstartkarte eingetragen.
Missum berücksichtigt auch Unreal-Installationen unter `E:\UnrealEngine\UE_*`.
Alternativ kann `MISSUM_UNREAL_ENGINE_ROOT` auf eine Engine-Installation oder
einen Ordner mit `UE_*`-Installationen gesetzt werden.

Die Erweiterung baut ihr Editor-Plugin mit den Werkzeugen der ausgewählten
Unreal-Engine-Installation. Dafür müssen die zu dieser Version passenden C++- und
Windows-Buildwerkzeuge installiert sein. Die Editor-Anbindung läuft im sichtbaren
Unreal Editor und bleibt zwischen Werkzeugaufrufen geöffnet. Bereits geöffnete,
nicht verbundene Editorfenster erscheinen im Status. Missum startet für seine
Werkzeuge eine eigene Sitzung.

Blueprint-Assets unter `/Game/` werden über `unreal.blueprint.read`, `create`,
`edit`, `compile` und `save` bearbeitet. Die Knoten- und Pin-IDs liefert
die Leseoperation. Bei einer externen Änderung der gespeicherten Assetdatei seit
dem Laden meldet das Speichern einen Konflikt.

`unreal.view.capture` erfasst den Editor-Viewport oder rendert die Perspektive einer
benannten Projektkamera. Bei Kameraaufnahmen können Bildformat und
Nachbearbeitung vom Editorfenster abweichen. `image.input` lädt das PNG und `media.analyze` prüft es mit dem
konfigurierten Vision-Modell. Der Befund wird mit `unreal.status` und
`visionReview` unter Angabe von Operation und Bildhash zurückgemeldet.
`unreal.camera.create` legt eine Kamera im offenen Level an. Das Level ist danach
ungespeichert und kann im Editor gespeichert werden.

## Paket bauen

```powershell
.\windows\package-unreal-integration.ps1 -PortableBuild -Install -DataDirectory "$env:LOCALAPPDATA\Missum"
```

Der Befehl erstellt und prüft das signierte `.aext`-Paket unter
`artifacts\unreal-integration\package`, baut einen separaten Portable-Client
und merkt die Erweiterung für das angegebene Datenverzeichnis vor. Beim nächsten
Missum-Start wird das Paket geprüft und aktiviert. Ohne `-Install` bleibt das
Paket im Artefaktordner.

Die tatsächliche Kompatibilität des Editor-Plugins ist für jede Unreal-Version
durch einen Build und eine reale Editorprüfung zu bestätigen.
