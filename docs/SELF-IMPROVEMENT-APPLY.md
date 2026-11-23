# Selbstverbesserung: Übernahme eines geprüften Kandidaten

Stand: 2026-11-22. Dieses Dokument beschreibt den Übernahme-Ablauf (`selfimprovement.apply`), die durch Tests
belegten Szenarien und die bekannten Grenzen. Es behauptet nichts, was nicht durch einen der unten genannten
Tests oder Builds gelaufen ist.

## Ablauf

| Teil | Ort |
|---|---|
| Tool, Vorprüfung, Übergabe, Wiederaufnahme | `src/Missum.App/Services/SelfImprovementApply.cs` |
| Externer Broker (Supervisor + Worker) | `windows/self-improvement-apply.ps1` |
| Wiederaufnahme im Chat | `MissumAiAssistantService.RecoverIncompleteOutcomeAsync` |
| App-Start / geplanter Neustart | `App.xaml.cs` (RunCreationHold, PlannedRestartHandler, ResumeSnapshotProvider) |

1. **Vorprüfung in der App.** Kandidat eindeutig per ID, vollständig verifiziert; Quelle, Manifest, portable
   Dateien und Image-Digests werden unmittelbar vor der Übernahme erneut geprüft. Fremde Workspaces/Sitzungen,
   eine ausführbare Datei außerhalb der Installation, ein nicht-loopback Gateway und ein Kandidat ohne
   Änderungen werden abgelehnt.
2. **Durable Operation.** Die Op-ID ist `SHA256("apply:" + Session + ":" + ProposalId)[..32]`. Ein zweiter
   Aufruf desselben Vorschlags tritt der laufenden Operation bei; ein anderer Vorschlag erhält
   `apply_in_progress`. `active-apply.json` hält neue Läufe zurück, bis die Operation terminal ist.
3. **Broker** (läuft unabhängig von App und Gateway, Supervisor startet einen abgestürzten Worker neu,
   jede Phase wird nach Abschluss in `state.json` festgeschrieben):
   `revalidate → await-app-exit → drain → ownership → backup → swap-app → swap-stack → verify-stack →
   start-app → await-reattach`.
   - Vor dem Tausch: andere Läufe/Worker-Jobs werden abgewartet oder als pending gemeldet; Besitz von Gateway
     (Compose-Projekt + Daten-Bind-Mount) und Installation wird geprüft; Resume-Snapshot (Session, Run-ID,
     Checkpoint, Event-Position, offener Toolaufruf mit Idempotenzschlüssel, sichtbarer Chat) wird gespeichert.
     Der geplante Neustart bricht den Lauf nicht ab.
   - Backup der alten App und konsistente SQLite-Backups inkl. WAL (App-DBs und Gateway-DB).
   - Tausch genau der verifizierten App-Dateien und Images (`MISSUM_AI_IMAGE_VERSION` in der env-Datei, übrige
     Einträge bleiben), dann Prüfung von Gateway-Bereitschaft, Protokoll, Lauf-Zustand, App-Start und Migration.
   - Fehler in irgendeiner Phase → Rollback von App, env, Container und Datenbanken; die alte App wird neu
     gestartet, die konkrete Ursache (`cause`) geht an die AI und in den Chat.
4. **Wiederaufnahme.** Die neue App schreibt `reattach.json` nach Prüfung des Gateway-Laufs (gleiche Run-ID,
   Zustand WaitingForClient/Running). Der Toolaufruf war im Journal `executing`; beim Stream-Start liefert
   `ResolveRecoveredAsync` das terminale Broker-Ergebnis, das Journal speichert es, und es wird genau an die
   ursprüngliche Run-ID übermittelt. Ein verlorenes Ack führt zu einer byte-identischen Wiederholung derselben
   Quittung (das Gateway dedupliziert per Proposal-ID); spätere Starts übermitteln nichts mehr.

## Bestandene Szenarien

Alle Tests laufen isoliert: eigene State-Root, App-Daten, SQLite-DBs, Test-Installation, Ports (Fake-Gateway auf
freiem Loopback-Port), eigenes Compose-Projekt `missum-sitest-<id>` mit eigenen Image-Tags. Docker-Tests
schlagen fehl statt übersprungen zu werden, wenn Docker fehlt.

`tests/Missum.Tests/SelfImprovementApplyBrokerTests.cs` (echter Broker-Prozess, Stand-in-App):

| Szenario | Test |
|---|---|
| App-Kandidat getauscht, WAL-Backup, Reattach, verlorenes Ack, Broker genau einmal gestartet | `AppOnlyCandidateIsSwappedVerifiedAndReattachedOnce` |
| Duplikat tritt bei, anderer Vorschlag abgelehnt | `DuplicateCallJoinsTheOperationAndAnotherProposalIsRefused` |
| Abbruch vor Übergabe ändert nichts, Hold freigegeben | `CancellingBeforeTheHandoffRejectsWithoutChangingAnything` |
| Broker in jeder der 10 Phasen getötet → fortgesetzt, genau einmal übernommen | `BrokerKilledAtPhaseIsResumedAndAppliesOnce` (Theory, 10 Fälle) |
| Worker-Absturz vor/nach Phasen → Supervisor setzt fort | `WorkerCrashIsRestartedBySupervisor` (8 Fälle) |
| Fehlgeschlagener Start / Migration → Rollback von App und DBs | `FailedStartOrMigrationRollsBackAppAndDatabases` (2 Fälle) |
| Absturz während des Rollbacks → Rollback wird abgeschlossen | `CrashDuringRollbackStillCompletesTheRollback` |
| Lauf nicht mehr fortsetzbar → Rollback vor Start der neuen App | `RunThatIsNoLongerResumableRollsBackBeforeTheNewAppStarts` |
| Reattach scheitert → Rollback | `ReattachFailureRollsBack` |
| Rollback scheitert → Chat und Checkpoint bleiben, Meldung sagt es | `FailedRollbackKeepsChatAndCheckpointAndSaysSo` |
| Portable Datei nach Verifikation verändert → abgelehnt vor jeder Änderung | `PortableFileChangedAfterVerificationIsRejectedBeforeAnyChange` |
| Unbekannter Image-Digest → abgelehnt | `UnknownImageDigestIsRejectedBeforeAnyChange` |
| Kandidat = Installation → `no_changes` | `CandidateEqualToTheInstallationIsRejected` |
| Andere aktive Läufe → blockiert, nichts geschrieben | `OtherActiveRunsBlockTheApplyBeforeAnythingIsWritten` |
| Lauf startet während der Übergabe → pending gemeldet, alte App neu gestartet | `RunStartedDuringTheHandoffIsReportedPendingAndTheAppRestarted` |
| Executable außerhalb der Installation → abgelehnt | `ExecutableOutsideTheInstallationIsRejected` |

`tests/Missum.Tests/SelfImprovementApplyStackTests.cs` (echtes Docker):

| Szenario | Test |
|---|---|
| Nur Gateway: Container läuft mit Kandidaten-Digest, env behält andere Werte, Gateway-WAL-Backup, App bleibt | `GatewayOnlyCandidateSwapsTheStackAndKeepsTheApp` |
| App und Gateway gemeinsam | `AppAndGatewayAreSwappedTogether` |
| Kandidaten-Gateway mit Protokollfehler / nicht erreichbar / Lauf nicht fortsetzbar, jeweils mit halb migrierter Gateway-DB → env, Container, Gateway-DB, App und App-Daten zurückgerollt | `FailingCandidateGatewayRollsBackEverything` (3 Fälle) |
| Container eines anderen Datenverzeichnisses → `ownership_mismatch`, unverändert | `ContainerOfAnotherDataDirectoryIsNotTouched` |

`tests/Missum.Tests/SelfImprovementApplyResumeTests.cs` (echter `MissumAiAssistantService`):

| Szenario | Test |
|---|---|
| Gleicher Chat, gleiche Run-ID, Ergebnis genau einmal angenommen, verlorenes Ack → identische Wiederholung, weitere Starts übermitteln nichts, Tool-Schritt im selben Chat abgeschlossen | `ResumedChatSubmitsTheBrokerResultToTheSameRunExactlyOnceDespiteALostAck` |
| Apply ohne Broker-Operation → `tool_outcome_unknown`, keine Wiederholung | `ApplyWithoutBrokerOperationIsReportedAsUnknownAndNotRepeated` |
| Op-ID deterministisch je Session + Vorschlag | `OperationIdIsStablePerSessionAndProposal` |
| Gateway außerhalb dieses Rechners gilt nicht als lokal verwaltet | `GatewayOutsideThisMachineIsNotAcceptedAsLocallyManaged` |

Ergebnisse der Läufe vom 2026-11-22 stehen im Abschnitt „Testläufe“ unten.

## Grenzen

- **Kein Live-UI-End-to-End.** Die Kette ist in drei Teilen belegt (Broker mit Stand-in-App, Docker-Stack mit
  Fake-Gateway-HTTP, Assistant-Service mit Fake-Gateway). Ein Lauf mit der echten WinUI-App, echtem Gateway und
  echtem Modell, der sich selbst ersetzt, wurde nicht ausgeführt. „Vollständige Wiederaufnahme“ ist daher nur
  für diese Teilketten belegt, nicht für die installierte App.
- Im Stack-Test schlafen die Container nur (`sleep infinity`); Health/Protokoll/Lauf-Zustand kommen vom
  Fake-Gateway. Geprüft wird der Tausch und Rollback von Image, Container, env und Gateway-DB, nicht der echte
  Gateway-Code des Kandidaten.
- Getauscht werden nur Images (`MISSUM_AI_IMAGE_VERSION`), nicht die Compose-Struktur. caddy und searxng
  werden nicht getauscht.
- Worker-Jobs werden über die Gateway-Laufaktivität (`/v1/runs/activity`) erfasst, nicht über eine eigene
  Job-Queue.
- Stirbt der Supervisor selbst, nachdem die alte App beendet ist, läuft nichts weiter, bis Missum erneut
  gestartet wird (`EnsureActiveBrokerRunning` beim App-Start setzt dann fort).
- Scheitert der Rollback, wird keine App neu gestartet; Chat und Checkpoint bleiben erhalten, und die
  Meldung fordert zur Prüfung der Installation auf.
- Zwischen `Process.Start` der neuen App und dem Speichern ihrer PID liegt ein sehr kleines Fenster; ein
  Worker-Absturz genau dort kann einen zweiten Start der neuen App auslösen.

## Testläufe

Alle am 2026-11-22 auf Windows 11 mit Docker Desktop ausgeführt, ohne übersprungene Tests:

| Lauf | Ergebnis |
|---|---|
| `dotnet test tests/Missum.Tests --filter "SelfImprovement\|ClientContinuationTests\|CodingToolJournalRecoveryTests"` | 96/96 bestanden (5 m 40 s) |
| `SelfImprovementApplyResumeTests` (nach Ergänzung der Op-ID- und Loopback-Tests) | 4/4 bestanden |
| `windows/build.ps1` (Release, SingleFile-Publish, Manifest-Hash geprüft) | erfolgreich |
| `windows/build-ai-stack.ps1` mit isolierter `-DataRoot`, `-ImageVersion sitest-build` | erfolgreich, inkl. `Missum.Ai.Server.Tests` 1804/1804 |

Nach den Läufen waren keine `sitest`-Container, Test-Images oder `msi-`-Temp-Verzeichnisse übrig; die Images
des isolierten Stack-Builds wurden entfernt. Der Live-Stack unter `ProgramData\Missum-AI-Stack` wurde nicht
angefasst.
