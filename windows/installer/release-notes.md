## Installation

1. **`Missum-Setup-<Version>.exe`** herunterladen und ausführen (keine Administratorrechte nötig).
   Windows zeigt eventuell einen SmartScreen-Hinweis, weil der Installer noch nicht signiert ist:
   „Weitere Informationen“ → „Trotzdem ausführen“.
2. Der Installer prüft die Voraussetzungen und bietet bei Bedarf die Installation von Docker Desktop an.
3. Missum startet danach in **Einstellungen › Lokaler KI-Stack**: „Einrichten“ baut die Docker-Images auf
   diesem PC (Docker lädt die Basis-Images automatisch) und lädt die gewählten Sprach- und Bildmodelle.
   Die erste Einrichtung kann je nach Internetverbindung länger dauern; ein Abbruch setzt beim nächsten
   Start an derselben Stelle fort.
4. Sprachmodelle für den Chat lädst du im **Modell Hub**.

### Voraussetzungen
- Windows 10 (2004) oder Windows 11, x64
- Docker Desktop mit WSL2-Backend (Linux-Container)
- NVIDIA-Grafikkarte mit aktuellem Treiber
- etwa 60 GB freier Speicher für Images und Modelle (ohne Chat-Modelle)

### Weitere Dateien
- `Missum-<Version>-win-x64-portable.zip` – Missum ohne Installer
- `Missum-AiStack-*.zip` – Quellen des KI-Stacks für manuelle oder LAN-Installationen
- `SHA256SUMS.txt` – Prüfsummen aller Dateien
