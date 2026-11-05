# Forschungsstandard für Mathematik und Physik

Die De-Broglie-Bohm-Publikation ist das Strukturvorbild: Fragestellung, Modellumfang und Konventionen →
nachvollziehbare Herleitung und mathematischer Beweis → geprüfter Lean-Beweis tragender formalisierbarer
Aussagen → unabhängige numerische Python-Gegenprüfung → Ergebnisse, Gültigkeitsgrenzen und offene Fragen.
Dieses Muster ist hier festgehalten und ohne Zugriff auf das fremde Projekt anzuwenden; seine fachlichen
Aussagen werden nicht übernommen. Neue Rechenschritte nach dem Ausarbeiten nochmals unabhängig überprüfen.
Vor potenziell langen Python-Rechnungen Rechenkomplexität, Datenumfang und Parallelisierbarkeit mit einem
kleinen Kontrollfall oder Benchmark prüfen. Skripte geben Phasenfortschritt, Laufzeit und Zwischenresultate
unmittelbar aus und speichern bei Bedarf wiederaufnahmefähige Checkpoints. Zuerst vektorisierte oder
bibliotheksinterne Verfahren nutzen; unabhängige CPU-Arbeit mit begrenzter Prozesszahl über mehrere Kerne
verteilen und geeignete dichte Array- oder Tensorrechnungen bei vorhandener Toolchain und ausreichendem
Speicher auf CUDA ausführen. Kleine, serielle oder symbolische Schritte nicht blind parallelisieren.
Parallel- oder GPU-Ergebnisse gegen einen deterministischen kleinen CPU-Referenzfall prüfen und bei fehlender
Eignung sauber auf CPU zurückfallen.
Fachliche Belege über Websuche oder Deep Research beschaffen und passende vorhandene Originalquellen
wiederverwenden. Belegquellen und Originaldokumente im vorhandenen Projektordner inputs/ ablegen;
keinen zusätzlichen Quellen-Ordner anlegen. Plots mit Erklärung direkt beim zugehörigen Thema einarbeiten.
Diese passenden Prüfungen bereits im Etappenplan vorsehen. Nachweise müssen tatsächlich ausgeführt sein und die behauptete Aussage
unter ihren Voraussetzungen tragen; verbleibende Prüf- und Beweislücken ausdrücklich offen ausweisen.
