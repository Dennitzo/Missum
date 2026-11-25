# Webdesign-Modus

Webdesign ist Codex mit einer Live-Webseite. Ein Webdesign-Lauf ist ein normaler Coding-Lauf (`RunMode.Coding`):
Projektbindung, verpflichtender Arbeitsplan, Änderungen und Review verhalten sich wie im Codex-Modus. Neu sind der
feste Tab **Webseite**, die Serve-Logik pro Projekt und die visuelle Mitgestaltung.

## Tab „Webseite“

- Nativer WebView2 (eigenes Profil `DataDirectory\WebView2\Webdesign`). Er zeigt ausschließlich Seiten des
  Projektservers; Links zu anderen Seiten öffnen im Standardbrowser.
- Beim Öffnen des Tabs blendet sich die rechte Seitenleiste aus, damit die Seite die volle Breite hat. Beim
  Zurückwechseln in den Chat kommt sie wieder (die gespeicherte Einstellung bleibt unverändert).
- Ändert die AI (oder ein Editor) eine Datei, lädt der Tab im Hintergrund: eine geänderte HTML-Seite wird
  geöffnet, CSS wird ohne Neuladen ausgetauscht, sonst wird mit erhaltener Scrollposition neu geladen. Ein Punkt
  am Tab zeigt Änderungen, solange der Chat sichtbar ist.
- **Werkzeug-Overlay:** schwebende, halbtransparente Leiste im Stil des Codex-Eingabefelds. Am Griff links
  verschiebbar (Doppelklick setzt zurück), mit dem Pfeil rechts einklappbar.
  - Eine Zeile: Geräte Smartphone (390×844), Tablet (820×1180), Laptop (1440×900), Frei, Drehen; dazu Markieren,
    Rahmen, Feintuning, Vorher/Nachher und Rückgängig (mehrstufig: Feintuning-Schritte, Farben, Gerätewechsel).
  - Beim Feintuning wächst das Overlay um eine Zeile mit Farbrad und Knöpfen – ohne zusätzliche Textzeilen.
- Geräte nutzen DevTools-Emulation (`Emulation.setDeviceMetricsOverride`): Die Seite rendert in der echten
  Gerätebreite, inklusive Media Queries und Touch, und wird bei Platzmangel verkleinert angezeigt.

## Serve-Logik

- Jedes Projekt bekommt einen eigenen Server; zwei Projekte teilen nie einen Port, eine zweite Sitzung desselben
  Projekts nutzt denselben Server weiter.
- Der eingebaute statische Server bindet Port 0, Windows wählt also einen freien Port ohne Wettlauf. Der zuletzt
  genutzte Port steht als `preferredPort` in `.missum/webdesign.json` und wird zuerst versucht, außer er ist
  belegt oder gehört zu Missum (8080–8082, 8090–8099).
- Projekte mit `package.json` und `dev`/`start`-Skript (Vite, Next, Astro, Nuxt, Parcel) laufen als überwachter
  Hintergrundprozess in einem Job-Objekt; der Port wird mit Strict-Port-Flag übergeben, bei `EADDRINUSE` folgt bis
  zu dreimal ein neuer Port. Ein eigener Befehl kann in der Konfiguration stehen (`{port}`, `{host}`).
- Ein leeres Projekt zeigt eine Platzhalterseite aus dem Speicher. Missum schreibt nur `.missum/webdesign.json`
  (und `.missum/annotations.json` für Wünsche) ins Projekt.
- Ausgeliefert wird nur unterhalb des Roots; `.git`, `.missum`, `node_modules` und Pfadüberschreitungen sind
  gesperrt, `Cache-Control: no-store`.
- **LAN:** Schalter in der Hosting-Hilfe. Der Server bindet dann zusätzlich an das lokale Netz; die Hilfe zeigt
  Netzwerkadresse und QR-Code (eingebauter Encoder, Byte-Modus, Fehlerkorrektur M) zum Testen auf dem Smartphone.
  Externe Browser laden über ein kleines Live-Skript automatisch neu.
- Inaktive Projekte stoppen nach zehn Minuten, alle beim Beenden von Missum.

## AI-Werkzeuge (Capability `webdesign`)

- `webdesign.serve`: `status`, `start`, `stop`, `restart`, `log`, `configure(root, startPage, kind, command)`.
  Server werden nie mit `coding.command` gestartet (dessen Prozesse enden mit dem Aufruf).
- `webdesign.preview`: `open(path)`, `capture(device)` → `uploadId` für `media.analyze`, `console` (JS-Fehler,
  HTTP-Fehler, fehlgeschlagene Ressourcen), `inspect(selector)`, `annotations`, `resolve(ids, note)`.
- Die Modusanweisung nennt Server, offene Wünsche und die Regeln (`WebdesignWorkflowPrompt`).

## Mitgestaltung

Alle Wünsche gehen **sofort** an Missum: als neuer Auftrag oder, wenn gerade ein Lauf arbeitet, als Steuerung
dieses Laufs. Jede Markierung bekommt eine Nummer (#1, #2 …), ein Badge auf der Seite und einen Bildausschnitt,
der dem Auftrag beiliegt. Missum ermittelt zusätzlich die vermutete Quelldatei (z. B. `index.html:5`).

- **Markieren:** Element anklicken und beschreiben, was sich ändern soll.
- **Rahmen:** Bereich aufziehen und beschreiben.
- **Feintuning:** gilt immer für genau ein Element; ein Klick auf ein anderes übernimmt es dorthin und setzt das
  vorherige zurück. Element wählen; direkt auf der Seite erscheinen Griffe: Rahmen ziehen verschiebt, Ecken
  skalieren (Umschalt: proportional), der obere Knopf dreht (Umschalt: 15°-Schritte), der Punkt rundet die Ecken.
  Farben für Text oder Fläche kommen aus einem Farbrad (Farbton am Rand, Weiß in der Mitte) mit
  Helligkeitsregler oder als Hex-Eingabe. „Umsetzen“ übergibt die exakten Unterschiede
  (z. B. `transform none → translate(40px, 0px) rotate(0deg)`), „Zurücksetzen“ verwirft die Vorschau.
- **Vorher/Nachher:** Vor jedem Lauf und danach wird die Seite aufgenommen; ein Regler vergleicht beide Stände.
- Meldet Missum eine Nummer mit `resolve` als erledigt, wird das Badge grün und das Element blinkt kurz.

Das Overlay-Skript wird per `AddScriptToExecuteOnDocumentCreatedAsync` eingespielt, nie in Projektdateien
geschrieben. Nachrichten der Seite tragen ein Sitzungs-Nonce und erzeugen nur Wünsche, keine Aktionen.

## Computersteuerung im Tab

`computer.observe` mit `scope: "webview"` (Standard in Webdesign-Sitzungen) liefert die gerenderte Seite plus
eine Liste sichtbarer Bedienelemente mit Box in Bildpixeln. `computer.act` wirkt über DevTools-Eingaben direkt im
Tab – ohne Maus des Nutzers und ohne Fensterfokus. Eine Aktion wird abgelehnt, wenn sich URL oder Scrollposition
seit der Beobachtung geändert haben. Freigaben fragt Missum nur bei Passwortfeldern, Dateiauswahl und Links zu
anderen Seiten.

## Prüfung

- Unit-Tests: `WebdesignModeTests` (Ports, Server, Pfade, Wünsche, QR, Farbrad, Computersteuerung im Tab),
  `WebdesignToolCatalogTests` (Gateway).
- UI-Smoke: `MISSUM_SMOKE_UI_WEBDESIGN=1` schreibt Aufnahmen `webdesign-*.png` und
  `native-webdesign-validation.json` ins Datenverzeichnis.
- Stabilität: Die DevTools-Ereignisempfänger müssen referenziert bleiben; sonst stürzt der Prozess zufällig mit
  einer Zugriffsverletzung im WebView2 ab. Alle WebView2-Handler laufen zusätzlich hinter einem Schutz.
