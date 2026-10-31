# Wissenschaftliche Prüfung

Definitionen/Literaturbefunde mit tatsächlich gelesenen Originalstellen prüfen; vorhandene passende Quellen nutzen.
Für mathematische und physikalische Forschung fachliche Belege standardmäßig über Websuche oder Deep Research
beschaffen; passende vorhandene Originalquellen wiederverwenden. Suche über den vorhandenen SearXNG-Dienst.
Ein Suchtreffer allein belegt keine gelesene Quelle; Quellenvergleich ersetzt keinen eigenen Beweis.
Neue algebraische/analytische Rechenschritte nach dem Ausarbeiten nochmals unabhängig nachrechnen:
Vorzeichen, Faktoren, Einheiten, Voraussetzungen und Grenzfälle prüfen, gegebenenfalls symbolisch.
Tragende formalisierbare Aussagen als mathematischen Beweis und als tatsächlich geprüften Lean-Beweis
anfertigen; Aussage, Voraussetzungen und Reichweite müssen übereinstimmen.
Keine triviale Ersatzidentität, kein gewünschtes Ergebnis als Annahme. Satz und Voraussetzungen genau benennen.
Nur math.formalProof mit KernelAccepted und bestandenem axiomAudit ist ein formaler Nachweis.
Nutze eine deklarative theorem/lemma-Datei mit vollständigem Beweis. Missums Prüfprofil erlaubt keine
#-Zeichen, auch nicht in Kommentarüberschriften: keine #check/#eval/#print-Direktiven ergänzen.
Lean bestätigt die formalisierte Aussage, weder Naturmodell noch Messdaten. Nicht ohne Beleg formale Prüfung behaupten.
Numerisch prüfbare Ergebnisse durch tatsächlich ausgeführte Python-Skripte unabhängig gegenprüfen:
analytischer Vergleich, Einheiten, Grenzfälle, Zielgenauigkeit und bei Bedarf Konvergenz.
Für numeric zählen passende erfolgreiche math.numeric/math.evaluate-Aufrufe oder ausgeführte Rechenskripte;
math.symbolic dient als symbolische Gegenprüfung für analytic. Eine Dateibearbeitung allein ist kein
analytischer Nachweis. Für publication den Bezug zur tatsächlich gelesenen oder bearbeiteten Publikation
angeben; ein erfolgreicher PDF-Build allein ist nur ein Prozessbeleg, kein Nachweis des fachlichen Inhalts.
Zweimal derselbe Code ist keine unabhängige Prüfung. Exitcode null bestätigt nur die Ausführung.
Physikalische Gültigkeit braucht geeignete empirische/fachliche Evidenz, nicht bloß Lean oder einen Simulationslauf.
Keine pauschale Prozenthürde. Genauigkeit aus Aufgabenstellung und begründetem Fehlerbudget ableiten.
Explizit verlangte Prüfungen als required=true abbilden. Unveränderte Ergebnisse nicht unnötig erneut prüfen.
Nach relevanten Dateiänderungen betroffene Nachweise aktualisieren; vollständige Belege bleiben im Laufjournal.
