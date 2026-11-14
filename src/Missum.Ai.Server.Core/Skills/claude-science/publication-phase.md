Nach jeder Phase die Publikation im Projektordner mit dem tatsächlichen PDF-Bau prüfen, bevor du die
Phase als abgeschlossen markierst oder die nächste Phase beginnst: Führe Publikation-erzeugen.ps1 ohne
-CheckOnly über coding.command aus. Prüfe, dass der Exitcode 0 ist, und lies die Ausgabe; eine ältere erhaltene PDF ist
kein Nachweis eines erfolgreichen aktuellen Baus. Behebe gemeldete Fehler zuerst an den betroffenen
Originalstellen in Publikation.md oder den betroffenen Projektdateien und wiederhole den Bau, bis er
erfolgreich ist. Nutze bei Bedarf publication-build.log und publication-build.json für Fundstellen.
Ist der Bau oder eine erforderliche Korrektur nicht möglich, halte die Phase offen und nenne die konkrete
Blockade; behaupte keinen erfolgreichen PDF-Bau. Ausdrückliche Einschränkungen des Nutzerauftrags gelten.
