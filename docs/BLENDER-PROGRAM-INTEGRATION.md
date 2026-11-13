# Blender in Missum

Im Codex-Modus unter **+ → Programmintegrationen → Blender** wählen. Missum findet eine lokale
Blender-Installation und erkennt bereits geöffnete Blender-Fenster. Für Modellierungsaufträge
startet Missum bei Bedarf ein sichtbares Arbeitsfenster mit Python-Unterstützung. Das signierte
Missum-Erweiterungspaket bringt die Startskripte mit. In Blender sind keine Add-on-Installation
und keine geänderten Einstellungen erforderlich.

## Eine Szene bearbeiten

- Mit `blender.status` und `connect: true` das Arbeitsfenster öffnen oder verbinden. Eine eindeutige `.blend`-Datei im
  Workspace-Hauptordner öffnet Missum direkt. Bei mehreren Dateien den gewünschten Pfad als
  `projectPath` angeben. Damit lassen sich auch Dateien in Unterordnern gezielt öffnen. Ohne
  vorhandene Projektdatei legt Missum `Szene.blend` im Workspace an.
- Mit `blender.scene.read` die Szene untersuchen und bevorzugt über `blender.custom.run` mit der
  Blender-Python-API modellieren. `blender.scene.edit` stellt feste Operationen bereit.
  `save: true` speichert auf demselben Pfad. `project.open` öffnet eine andere Datei im
  Arbeitsfenster. Nach einem Szenenwechsel lässt sich das Ziel mit `blender.status` und der
  gemeldeten `instanceId` bestätigen.
- `set_render_engine('eevee')` in `blender.custom.run` und `render.set` mit `engine: 'eevee'`
  wählen die zur laufenden Blender-Version passende Render-Engine. Auch `workbench` und `cycles`
  werden unterstützt. Nach einem Python-Ausführungsfehler den aktuellen Szenenstand prüfen.
- Bei maßhaltigen Modellen das Grundmodell vor der Detailarbeit prüfen. Für Quader übernimmt
  `object.create` unter `dimensions` die vollen Kantenlängen. `blender.scene.read` mit
  `include: ["bounds"]` und gezielten `objectNames` zeigt die achsparallelen Grenzen im Weltkoordinatensystem.
  Damit lassen sich beispielsweise Geschosshöhen und Lücken zwischen Auflagern erkennen.
- `blender.view.capture` nimmt standardmäßig das sichtbare Fenster als PNG auf. Mit `instanceId`
  lässt sich ein bestimmtes Fenster wählen. Viewport und Render stehen im steuerbaren
  Arbeitsfenster zur Verfügung.
- Missum verwendet die Aufnahme für die Sichtprüfung. Der Agent vergleicht sie mit dem Auftrag
  und bei Bedarf mit einem angehängten Referenzbild. Bei sichtbaren Abweichungen korrigiert er die
  Szene gezielt und lässt den neuen Stand erneut prüfen. Steht keine Vision-Auswertung zur
  Verfügung, bleibt die Sichtprüfung offen.

Blender bleibt zwischen den Werkzeugaufrufen geöffnet. Änderungen von Hand im Missum-Arbeitsfenster
sind für die Werkzeuge sichtbar. Separat gestartete Blender-Fenster lassen sich visuell prüfen.
Zum Bearbeiten derselben Datei Änderungen im separat geöffneten Fenster zuerst speichern und die
Datei dort schließen.

`blender.status` mit `connect: false` fragt den Status ab, ohne ein Arbeitsfenster zu öffnen.
Wenn die Installation nicht automatisch gefunden wird, lässt sich der vollständige Pfad zu
`blender.exe` über `MISSUM_BLENDER_EXECUTABLE` für den Missum-Prozess angeben.

## Erweiterungspaket bauen oder aktualisieren

```powershell
.\windows\package-blender-integration.ps1 -PortableBuild -Install -DataDirectory "$env:LOCALAPPDATA\Missum"
```

`-PortableBuild` erzeugt die kompatible Anwendung in
`artifacts\blender-integration\win-x64`. Das signierte Paket
`com.missum.blender-1.2.3.aext` liegt unter `artifacts\blender-integration\package` und enthält den
Adapter sowie `runtime/bootstrap.py` und `runtime/scene_ops.py`. Mit `-Install` wird das Paket für
den nächsten Start vorgemerkt. Für eine separate Testinstallation das zugehörige Datenverzeichnis
angeben. Beim nächsten Start übernimmt Missum die höhere Paketversion.

Die Registrierung, die fünf Werkzeugbeschreibungen und der Betrieb des Adapters laufen weiterhin
über Missums Erweiterungssystem. Missum startet Blender außerhalb des kurzlebigen ExtensionHost.
Die Startskripte bearbeiten Anfragen im Blender-Hauptthread. Die Verbindung bleibt auf den lokalen
Rechner begrenzt und wird mit einer sitzungsbezogenen Kennung abgesichert. Projektpfade werden gegen
den ausgewählten Workspace geprüft.
