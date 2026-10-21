# Wissenschaftliche Prüfung

Definitionen/Literaturbefunde mit tatsächlich gelesenen Originalstellen prüfen; vorhandene passende Quellen nutzen.
Websuche oder Deep Research nur für konkrete Wissens- und Beleglücken. Suche über den vorhandenen SearXNG-Dienst.
Ein Suchtreffer allein belegt keine gelesene Quelle. Reine Eigenherleitungen brauchen keine künstliche Websuche.
Algebraische/analytische Aussagen nachvollziehbar herleiten und unabhängig gegenprüfen, gegebenenfalls symbolisch.
Für tragende sinnvoll formalisierbare Sätze Lean nutzen, wenn es die relevante Aussage absichert.
Keine triviale Ersatzidentität, kein gewünschtes Ergebnis als Annahme. Satz und Voraussetzungen genau benennen.
Nur math.formalProof mit KernelAccepted und bestandenem axiomAudit ist ein formaler Nachweis.
Nutze eine deklarative theorem/lemma-Datei mit vollständigem Beweis. Missums Prüfprofil erlaubt keine
#-Zeichen, auch nicht in Kommentarüberschriften: keine #check/#eval/#print-Direktiven ergänzen.
Lean bestätigt die formalisierte Aussage, weder Naturmodell noch Messdaten. Nicht ohne Beleg formale Prüfung behaupten.
Numerik: unabhängiger Vergleich, Einheiten, Grenzfälle, Zielgenauigkeit und bei Bedarf Konvergenz.
Zweimal derselbe Code ist keine unabhängige Prüfung. Exitcode null bestätigt nur die Ausführung.
Physikalische Gültigkeit braucht geeignete empirische/fachliche Evidenz, nicht bloß Lean oder einen Simulationslauf.
Keine pauschale Prozenthürde. Genauigkeit aus Aufgabenstellung und begründetem Fehlerbudget ableiten.
Explizit verlangte Prüfungen als required=true abbilden. Unveränderte Ergebnisse nicht unnötig erneut prüfen.
Nach relevanten Dateiänderungen betroffene Nachweise aktualisieren; vollständige Belege bleiben im Laufjournal.
