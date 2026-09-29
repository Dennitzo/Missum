# Missum

Native WinUI 3 desktop application inspired by the supplied ChatGPT/Codex reference.
The desktop chat uses XAML controls. Missum includes its coordinator, SQLite
storage, local tool broker, model runtime and Docker gateway.

## Build and run

Windows 10 19041+ / Windows 11, .NET SDK 10, Windows App SDK.

    dotnet build src/Missum.App/Missum.App.csproj -c Release -p:Platform=x64
    .\src\Missum.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\Missum.exe

    .\Start-Missum.ps1

The launcher uses the newest existing Release or portable executable, and builds
Release if neither exists. For a self-contained portable distribution:

    .\windows\publish.ps1 -Mode SingleFile

This produces `artifacts/portable/win-x64/Missum.exe` and its separate
`ExtensionHost` companion folder. Keep that folder beside the executable for
extension tools. The desktop executable extracts its bundled native runtime and
XAML resources automatically. `-Mode Folder` creates a conventional distribution
under `artifacts/windows/app-win-x64-folder` instead.

The publish script runs a native startup smoke check by default. It verifies all
manifest hashes, launches a fresh isolated profile, confirms the native page has
opened a persisted chat, and rejects a WebView2 profile. Smoke runs do not start
or stop shared model services. This checks packaging and native startup; it does
not replace a live model response and tool run.

Run the client regression suite separately with `./windows/test.ps1`. The full
`./windows/build.ps1` pipeline also invokes the context/server checks and portable
publish. Run these sequentially so outputs are not locked by concurrent builds.

## Data and models

Missum stores its own chats and settings in %LOCALAPPDATA%\Missum.
The default gateway is http://127.0.0.1:8080. The Missum Docker stack and
native model runtime serve the local application. Select an installed local model
in the composer. A ChatGPT subscription does not provide these local models.

The file deploy/missum-ai/compose.yaml defines the Missum deployment. Do not
start a duplicate on the same ports. See docs/DOCKER.md.

## Interface

The native window opens directly on the AI assistant; the Datei menu links the
AI assistant and settings without an additional outer navigation rail.
Switch ChatGPT/Codex/Claude Science above the session list. Enter sends, Shift+Enter adds a line.
Sessions, mode, project workspace and drafts persist locally.
Right-click a chat to rename/delete it. Attachments and file changes appear in
the floating output overlay. Tool calls use the Missum coordinator and local tool broker.

Chat formulas support inline `$...$` / `\(...\)` and display `$$...$$` / `\[...\]`
notation, including fractions, roots, sums, integrals, aligned equations and matrices.
The native desktop uses CSharpMath/SkiaSharp for the shared LaTeX/KaTeX syntax;
it does not host the JavaScript KaTeX engine or support every KaTeX extension.
Code examples remain literal, unfinished streaming formulas wait for their closing
delimiter, and unsupported formulas retain their readable source. Click a formula
to copy its original LaTeX. No browser or network is needed for typesetting.
The portable smoke verifies native inline/display rendering, streaming transitions
and safe fallbacks, and writes a `.math-preview.png` beside the publish manifest.

## Provenance and validation

Base: the supplied local template snapshot at commit 86d27c8. Projects and
namespaces are branded Missum; explicit legacy readers preserve older data.
Historical validation documents copied from that template describe prior runs.
Current Missum evidence is recorded separately in docs/visual-validation/.
App icon: supplied Missum_Logo.png, converted to a 256px PNG-backed Windows icon.
See docs/visual-validation/ for reference and staged runtime screenshots.
