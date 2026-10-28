# Spezialisierte Agenten

Spezialisierte Agenten sind ausdrücklich ausgewählte Arbeitsprofile. Das erste Profil ist **Loop-Agent**.
Es prüft nach einem Hauptagentenabschnitt Ziel, Plan, Belege und offene Probleme und gibt bei Bedarf einen
konkreten Folgeauftrag an den Hauptagenten. Es gibt keine feste Zahl an Forschungszyklen und keine Laufzeitgrenze.
Der normale Stop-Button und das Entfernen des Chips stoppen den gesamten Auftrag.

## Gemeinsames Grundgerüst

- `SpecializedAgentOptions` bindet Profil und ursprüngliches Ziel an die angenommene Laufanfrage.
- `SpecializedAgentProfiles.BuiltIn` liefert Namen/Beschreibungen für Menü, Chip und Auswahlvalidierung.
- `ISpecializedAgentProfile` liefert versionierte Prüfanweisungen; `SpecializedAgentRegistry` löst das Profil auf.
- `SpecializedAgentCheckpoint` speichert Ziel, Runde, Phase, Prüfinstanz, Entscheidung und nächste Prompt-ID.
- `RunProcessor.SpecializedAgents` führt die Prüfung seriell aus. Sie nutzt ausschließlich die bereits geladene
  Hauptinstanz, deren Sitzungs-Cache und unveränderte Werkzeugdefinitionen. Werkzeuge werden während dieser
  Prüfphase nicht ausgeführt. Fehlende aktuelle Prüfungen werden dem Hauptagenten als Folgeauftrag übergeben.
- Die Entscheidung hat `completed`, `continue` oder `needs_input`, begründete Prüfpunkte und offene Fragen.
  Ungültiges JSON oder eine Erfolgsbehauptung trotz offenem Plan gilt niemals als Zielerreichung.
- Folgeaufträge verwenden die atomare, wiederholbare Steering-Persistenz. Sie bleiben als Prompt-Einträge
  im Chat sichtbar, haben eine Agentenherkunft und verändern das ursprüngliche Nutzerziel nicht.
- Echte neue Nutzereingaben haben Vorrang und entwerten eine laufende Prüfung. Stop oder ein terminaler
  Lauf kann keine weitere automatische Übergabe mehr anlegen.
- Ein Abbruch wird auch bei getrennter App-Verbindung dauerhaft vorgemerkt. Bei verlorener
  Erstellungsbestätigung verwendet er Sitzung und Idempotenzschlüssel: Der Server sperrt diesen Auftrag,
  ohne ihn durch einen erneuten Erstellungsaufruf starten zu müssen. Offline-Abbrüche werden nachgeholt.
- Die Prüfungen erscheinen über die bestehende Subagentenprojektion mit Planet, Tab, Prompt und Ergebnis.
  Eine stabile Agentenkennung verbindet die aufeinanderfolgenden Prüfrunden; der Verlauf bleibt gespeichert.

Die Phasen `main → review → decision → handoff → main` werden vor ihren Übergaben gespeichert. Eine
Wiederverbindung oder ein Gateway-Neustart übernimmt diesen Stand; eine bereits angenommene Prompt-ID wird
nicht erneut ausgeführt. Die normale Kontextkomprimierung bleibt aktiv und erhält den Agentenstand separat
vom verdichteten Gespräch. Eine absichtlich gestoppte Sitzung wird nicht automatisch wieder gestartet.
Auch beim manuellen Fortsetzen bleiben ausgewähltes Profil und ursprüngliches Ziel erhalten. Echte
Nutzerkorrekturen ergänzen das Ziel; automatisch erzeugte Folgeaufträge ersetzen es nicht. Eine entfernte
Auswahl wird beim Fortsetzen nicht wieder eingeschaltet.

Die Prüfung erzeugt **keine zweite Modellinstanz**, auch nicht auf GPU1, und hat keinen Modell-Fallback.
Ist die Hauptinstanz nicht mehr geladen, darf die Prüfung sie nicht eigenständig nachladen. Die bestehende
Kapazitätsprüfung für gewöhnliche parallele Subagenten bleibt unverändert.
Nach einem vollständigen Runtime-Neustart stellt der reguläre Hauptlauf beim Wiederanlauf zunächst sein
ausgewähltes Modell wieder bereit; erst danach übernimmt die gespeicherte Prüfung dieselbe Instanz.

## Weiteres Profil ergänzen

1. Einen Metadateneintrag in `SpecializedAgentProfiles.BuiltIn` hinzufügen.
2. `ISpecializedAgentProfile` implementieren und eine eindeutige ID/Version angeben.
3. Das Profil in `MissumAiServerHostExtensions` registrieren. Beim Aktualisieren laufender Profile die alte
   Version weiterhin registrieren; neue Läufe verwenden die zuletzt registrierte Version derselben ID.
4. Profilentscheidung und Ablauf mit den vorhandenen Protokoll-, Persistenz- und Runtime-Tests prüfen.

Menü, Chip, Speicherung, Stop, Subagentenansicht und Promptübergabe müssen nicht erneut implementiert werden.
Das aktuelle Grundgerüst ist für kontextbasierte Nachprüfungen vorgesehen. Ein zukünftiges Profil, das selbst
Dateien verändern oder Client-Werkzeuge ausführen soll, benötigt dafür einen expliziten neuen Ausführungsvertrag;
virtuelle Prüfinstanzen dürfen nicht als gewöhnliche parallele Subagenten oder Client-Tool-Run-IDs ausgegeben werden.

Ein formell gültiges Entscheidungsobjekt beweist keine wissenschaftliche Wahrheit. Das lokale Modell bewertet
das fachliche Ziel anhand des verfügbaren Kontexts und kann weitere tatsächliche Gegenprüfungen anfordern.
