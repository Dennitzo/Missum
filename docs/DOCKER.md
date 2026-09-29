# Docker-Dienste

Missum verwendet den vorhandenen Missum-Stack über http://127.0.0.1:8080.
Der native Modellprozess bleibt auf 8081, sein Kontrollport auf 8082.

Übernommene Dienste: Caddy, Gateway, SearXNG, Speech, Image und Media.
Die Compose-Datei unter deploy/missum-ai ist die Infrastrukturvorlage.
Ihre Container-Namen und Ports gehören zum vorhandenen Stack. Ein zweiter
gleichzeitiger Start würde kollidieren. Missum muss keinen zweiten Stack starten.

Prüfen:

```powershell
docker ps
Invoke-RestMethod http://127.0.0.1:8080/v1/health/live
Invoke-RestMethod http://127.0.0.1:8080/v1/health/ready
```

Ein gesunder Container bedeutet nicht, dass ein Sprachmodell geladen ist.
Die Readiness-Antwort benennt dies ausdrücklich. Missum zeigt Modell-Laden,
Werkzeugschritte, Abbruch und Fehler im nativen Chat.

Missum wurde als Quelle gelesen und nicht verändert. Die Chat-Datenbank von
Missum ist separat; große Modell-Dateien und Docker-Images werden wiederverwendet.

Der Portable-Smoke-Test verwendet eine neue temporäre Datenbank und Instanzkennung.
Auch bereits gesetzte `ASSISTANT_DATA_ROOT`- und `ASSISTANT_INSTANCE_KEY`-Variablen
werden dafür überschrieben und anschließend wiederhergestellt. Der Autostart des
nativen Modellservers ist während des Tests deaktiviert. Das normale Beenden von
Missum lässt den gemeinsam verwendeten Modellprozess weiterlaufen.
