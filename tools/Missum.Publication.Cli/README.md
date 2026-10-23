# Standalone publication renderer

This console tool source-links Missum's production prose/image preparation and
ships its exact Markdown, KaTeX, CSS, font and Edge PDF exporter assets. No WinUI
application or database is started. Publication content is never rewritten.

Publish for Windows x64:

```powershell
dotnet publish tools/Missum.Publication.Cli/Missum.Publication.Cli.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/publication-cli
```

Copy that entire output to `.publication-renderer/` in a project. Copy
`Bundle/Publikation-erzeugen.ps1` and `Bundle/PDF-ERZEUGEN.md` beside it.
The script defaults to its own directory, which must contain `Publikation.md`.
Runtime requirements are Windows PowerShell 5.1 and Microsoft Edge only.

CLI: `Missum.Publication.Cli.exe --project <directory> [--check-only]`.
Each run writes JSONL progress to stdout and `publication-build.log`, then writes
an atomic `publication-build.json` report including source locations for content
errors. Exit codes: 0 success, 2 content, 3 dependency/runtime, 4 changed source.
The last successful render receipt survives errors and check-only runs.
Unchanged source, images, renderer and PDF bytes are reused; changing any of
them reruns the deterministic typesetting pipeline. PDF metadata on a fresh
Chromium print includes its creation time. No test is added to the Windows build
script, and the tool imposes no wall-clock export limit.
