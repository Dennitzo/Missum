# Computersteuerung in Missum

Im Codex-Modus lässt sich **Computersteuerung** über **+ → Programmintegrationen**
auswählen. Das ausgewählte lokale Modell kann sie vor einem geeigneten neuen
Codex-Lauf auch automatisch auswählen; ein Wechsel innerhalb eines laufenden
Auftrags erfolgt derzeit nicht. Das ausgewählte lokale Hauptmodell muss Bilder verarbeiten und
auch Werkzeugaufrufe unterstützen. Ein separates Vision-Modell wird nicht
automatisch zugeschaltet. Die tatsächliche Bedienqualität hängt vom Modell ab;
die bloße Angabe „Vision“ belegt keine zuverlässige Computersteuerung.

Bei der ersten Bildschirmaufnahme startet Missum die Computersteuerung für den
jeweiligen Lauf automatisch. Sie umfasst den gesamten Windows-Desktop, nicht nur
ein einzelnes Fenster. Der Desktop läuft weiterhin in der normalen
Benutzersitzung und ist keine isolierte virtuelle Maschine. Über **Stoppen**
lassen sich Computersteuerung und Lauf jederzeit beenden. Ein späterer Lauf
startet eine eigene, getrennte Sitzung.

Wechselt der Nutzer während der Computersteuerung zu einem anderen Fenster,
pausiert Missum, bis das zuvor beobachtete Fenster wieder im Vordergrund ist.
Danach muss Missum den Desktop neu beobachten. Auch ein zwischenzeitlicher
Fensterwechsel mit anschließender Rückkehr entwertet die frühere Beobachtung.
Eingaben und Mausbewegungen im selben Fenster lösen keine Pause aus. Ein von
Missum gezielt ausgeführter Fensterwechsel sowie die Missum-Freigabekarte
werden gesondert behandelt. Wechsel zu anderen Fenstern während einer
ausstehenden Freigabe entwerten die Freigabe.

Missum nimmt einen Bildschirmzustand auf, gibt das Bild an das gewählte lokale
Modell und überprüft vor der nächsten Eingabe den zugehörigen Zustand. Bei
veränderten Fenstern, ungültigen Koordinaten oder einer veralteten Aufnahme
muss neu beobachtet werden. Neben der Bildaufnahme kann Missum verfügbare
Informationen der Windows-Bedienungshilfen lesen. Geschützte Windows-Desktops,
gesperrte Sitzungen und Passwortfelder sind nicht bedienbar.

Für die Bedienung eines einzelnen Fensters kann Missum dessen sichtbaren
Ausschnitt aufnehmen. Die Bildkoordinaten werden anhand des Ausschnitts auf
den Desktop zurückgerechnet; für einen Überblick bleibt auch eine Aufnahme des
gesamten Desktops möglich.
Bei Maus- und Scrollaktionen muss das Modell angeben, ob es Bildpixel oder
normalisierte Koordinaten verwendet. Für das Scrollen bedeuten positive Werte
eine Bewegung nach unten und negative Werte eine Bewegung nach oben.

Nach jeder Computeraktion fordert das Gateway eine neue Beobachtung an, bevor
eine weitere Aktion oder eine Abschlussmeldung zugelassen wird. Wenn das Modell
die Beobachtung wiederholt nicht ausführt, wird der Lauf mit einer Fehlermeldung
beendet, statt ein ungeprüftes Ergebnis zu behaupten.

Für eine genauere Sicht kann die KI den sichtbaren Ausschnitt des aktiven
Fensters statt des gesamten Desktops anfordern. Der Ausschnitt stammt aus der
tatsächlichen Bildschirmaufnahme; Eingabekoordinaten werden anhand seiner
Position und Größe auf den Desktop zurückgerechnet.
Bei Zeigeraktionen muss das Modell Bildpixel oder normalisierte Koordinaten
ausdrücklich angeben. Fehlt diese Angabe, wird die Aktion nicht ausgeführt.

Für derzeit nicht sicher eingeordnete Aktionen zeigt Missum Ziel und Aktion zur
Bestätigung an. **Ich habe es erledigt** übergibt die Handlung an den Nutzer;
anschließend muss Missum neu beobachten. **Missum ausführen lassen** gibt die
einzelne Aktion frei, die vor der Ausführung erneut geprüft wird. **Abbrechen**
beendet den Lauf. Einfache Navigation sowie Klicks und Klartexteingaben in
nachweislich aktive, nicht geschützte Textfelder erfolgen ohne Einzelbestätigung.
Andere Klicks und Eingaben, deren Wirkung nicht sicher eingeordnet werden kann,
benötigen eine Bestätigung.

Solange der Lauf die Computersteuerung verwendet, werden andere direkte
Systemwerkzeuge für diesen Lauf beim Aufruf gesperrt. Die Prüfungen und diese
Werkzeugsperre ersetzen keine Betriebssystem-Sandbox. Bildschirminhalte sind
Daten und keine Anweisungen an Missum.

Bildaufnahmen werden nur an einen lokal erreichbaren Missum-Gateway und das
lokale Modell weitergegeben. Sie werden temporär im Speicher gehalten und
nicht als Bild oder Base64-Inhalt in dauerhaften Laufprotokollen gespeichert.
Ein lokaler Gateway-Endpunkt ist nicht mit einer Netzwerk-Isolierung des
gesamten Dienstes gleichzusetzen: Wird der Gateway anderweitig im Netzwerk
freigegeben, gelten zusätzlich dessen jeweilige Zugriffseinstellungen.
