[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$WebAssetsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [switch]$ScientificPublication,

    [switch]$ChatTranscript,

    [string]$DiagnosticsPath,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ScientificPublication -and $ChatTranscript) {
    throw 'Publikationsprüfung und Chatarchiv sind unterschiedliche PDF-Exportarten.'
}
# The native process reads redirected diagnostics as UTF-8. Windows PowerShell
# otherwise uses the console code page and corrupts German text and TeX symbols.
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $OutputEncoding
$diagnosticLimit = if ([string]::IsNullOrWhiteSpace($DiagnosticsPath)) { 8 } else { [int]::MaxValue }
$diagnosticTextLimit = if ([string]::IsNullOrWhiteSpace($DiagnosticsPath)) { 500 } else { [int]::MaxValue }

function Write-DocumentProgress([string]$Message) {
    if (-not [string]::IsNullOrWhiteSpace($DiagnosticsPath)) { Write-Output $Message }
}

function Save-DocumentDiagnostics([string]$Dom) {
    if ([string]::IsNullOrWhiteSpace($DiagnosticsPath)) { return }
    $counts = [ordered]@{}
    foreach ($entry in @(@('mathRendered', 'katex-rendered'), @('mathInvalid', 'katex-invalid'),
        @('headingInvalid', 'heading-invalid'), @('figureInvalid', 'figure-invalid'))) {
        $count = 0
        if ($Dom -match ('data-missum-' + $entry[1] + '="([0-9]+)"')) { $count = [int]$Matches[1] }
        $counts[$entry[0]] = $count
    }
    $issues = [Collections.Generic.List[object]]::new()
    foreach ($entry in @(@('math', 'katex-errors'), @('heading', 'heading-errors'))) {
        if ($Dom -match ('data-missum-' + $entry[1] + '="([^"]*)"')) {
            $items = [Uri]::UnescapeDataString($Matches[1]) | ConvertFrom-Json
            foreach ($item in @($items)) {
                if ($null -eq $item) { continue }
                $issue = [ordered]@{ kind = $entry[0] }
                foreach ($property in $item.PSObject.Properties) { $issue[$property.Name] = $property.Value }
                $issues.Add($issue)
            }
        }
    }
    if ($counts.figureInvalid -gt 0) {
        $issues.Add([ordered]@{ kind = 'figure'; reason = "$($counts.figureInvalid) nicht lesbare Abbildungen." })
    }
    $report = [ordered]@{ schema = 'missum.pdf.diagnostics.v1'; source = $source; counts = $counts; errors = @($issues.ToArray()) }
    $destination = [IO.Path]::GetFullPath($DiagnosticsPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllText($destination, (ConvertTo-Json -InputObject $report -Depth 8), [Text.UTF8Encoding]::new($false))
}

function Resolve-EdgeExecutable {
    $configured = [Environment]::GetEnvironmentVariable('MISSUM_EDGE_PATH')
    $candidates = @(
        $configured,
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:ProgramFiles 'Microsoft\Edge\Application\msedge.exe'),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\Edge\Application\msedge.exe')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw 'Microsoft Edge wurde nicht gefunden. Setze bei Bedarf MISSUM_EDGE_PATH auf msedge.exe.'
}

function Convert-ToFileUri([string]$Path) {
    return ([Uri](Resolve-Path -LiteralPath $Path).Path).AbsoluteUri
}

function Convert-ToExtendedFilePath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if ($absolute.StartsWith('\\?\', [StringComparison]::Ordinal)) { return $absolute }
    if ($absolute.StartsWith('\\', [StringComparison]::Ordinal)) {
        return '\\?\UNC\' + $absolute.Substring(2)
    }
    return '\\?\' + $absolute
}

function Invoke-EdgeProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Executable,

        [Parameter(Mandatory = $true)]
        [string[]]$CommandArguments
    )

    # Chromium can emit harmless diagnostics on stderr while returning exit
    # code zero. Windows PowerShell otherwise promotes those lines to a
    # terminating NativeCommandError because this script runs in Stop mode.
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $capturedOutput = (& $Executable @CommandArguments 2>&1 | Out-String)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    return [pscustomobject]@{
        ExitCode = [int]$exitCode
        Output = [string]$capturedOutput
    }
}

$source = (Resolve-Path -LiteralPath $SourcePath).Path
$webAssets = (Resolve-Path -LiteralPath $WebAssetsPath).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $output
$outputFilePath = Convert-ToExtendedFilePath $output
[IO.Directory]::CreateDirectory((Convert-ToExtendedFilePath $outputDirectory)) | Out-Null

$stylesUri = Convert-ToFileUri (Join-Path $webAssets 'styles.css')
$markdownUri = Convert-ToFileUri (Join-Path $webAssets 'markdown.js')
$katexRoot = Join-Path $webAssets 'vendor\katex\0.16.10'
$katexCssUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.css')
$katexScriptUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.js')

$sourceText = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
Write-DocumentProgress 'Manuskript wird mit Missum-Markdown, Publikationslayout und KaTeX 0.16.10 gesetzt.'
$outlineBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('[]'))
$outlineMatch = [regex]::Match($sourceText, '<!-- MISSUM_PUBLICATION_OUTLINE:(?<payload>[A-Za-z0-9+/=]+) -->')
if ($outlineMatch.Success) {
    $outlineBase64 = $outlineMatch.Groups['payload'].Value
    $sourceText = $sourceText.Replace($outlineMatch.Value, '')
}
$localFigures = [Collections.Generic.List[object]]::new()
if ($ScientificPublication) {
    $sourceDirectory = [IO.Path]::GetDirectoryName($source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $sourceText = [regex]::Replace($sourceText, '!\[(?<label>[^\]\r\n]*)\]\((?<path>[^\)\r\n]+)\)', {
        param($match)
        $requested = $match.Groups['path'].Value.Trim().Trim('<', '>')
        if ($requested -notmatch '^figures/(?<hash>[0-9a-f]{64})\.(?<type>png|jpg)$') { return $match.Value }
        if ($localFigures.Count -ge 64) { throw 'Die Publikation enthält mehr als 64 Abbildungen. Reduziere Wiederholungen oder teile den Bildumfang fachlich auf; es werden keine Bilder still ausgelassen.' }
        $expectedHash = $Matches['hash']
        $imageType = $Matches['type']
        try { $figurePath = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ([Uri]::UnescapeDataString($requested)))) }
        catch { return $match.Value }
        if (-not $figurePath.StartsWith($sourceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $figurePath -PathType Leaf)) { return $match.Value }
        $fileInfo = Get-Item -LiteralPath $figurePath
        $figureDirectory = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($figurePath))
        if ($fileInfo.Length -gt 8MB) { throw 'Eine Publikationsabbildung ist größer als 8 MiB. Komprimiere den Plot; die bisherige PDF bleibt erhalten.' }
        if (($fileInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($figureDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $fileInfo.Length -lt 8) { return $match.Value }
        $totalBytes = 0L
        foreach ($existingFigure in $localFigures) { $totalBytes += $existingFigure.length }
        if ($totalBytes + $fileInfo.Length -gt 128MB) { throw 'Die Publikationsabbildungen überschreiten zusammen 128 MiB. Komprimiere die Plots oder teile den Bildumfang fachlich auf; es werden keine Bilder still ausgelassen.' }
        $bytes = [IO.File]::ReadAllBytes($figurePath)
        $validSignature = if ($imageType -eq 'png') {
            [BitConverter]::ToString($bytes, 0, 8) -eq '89-50-4E-47-0D-0A-1A-0A'
        } else { $bytes[0] -eq 255 -and $bytes[1] -eq 216 -and $bytes[2] -eq 255 }
        if (-not $validSignature) { return $match.Value }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = [BitConverter]::ToString($hasher.ComputeHash($bytes)).Replace('-', '').ToLowerInvariant() }
        finally { $hasher.Dispose() }
        if ($actualHash -ne $expectedHash) { return $match.Value }
        $mime = if ($imageType -eq 'png') { 'image/png' } else { 'image/jpeg' }
        $placeholder = 'MISSUMLOCALFIGURE' + ($localFigures.Count + 1).ToString('D4')
        $localFigures.Add([pscustomobject]@{ token = $placeholder; url = 'data:' + $mime + ';base64,' + [Convert]::ToBase64String($bytes); label = $match.Groups['label'].Value; length = $bytes.Length })
        return "`n`n$placeholder`n`n"
    })
}
$figurePayload = ConvertTo-Json -InputObject @($localFigures.ToArray()) -Compress -Depth 3
$figureBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($figurePayload))
$title = [IO.Path]::GetFileNameWithoutExtension($source)
$sourceBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sourceText))
$titleBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($title))

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('Missum-DocumentPdf-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$htmlPath = Join-Path $temporaryRoot 'document.html'
$validationProfilePath = Join-Path $temporaryRoot 'edge-profile-validation'
$printProfilePath = Join-Path $temporaryRoot 'edge-profile-print'
[IO.Directory]::CreateDirectory($validationProfilePath) | Out-Null
[IO.Directory]::CreateDirectory($printProfilePath) | Out-Null
# Chromium's PDF writer can silently fail at MAX_PATH even though the .NET
# publication service accepts the longer destination. Print to the short local
# renderer directory, then stage on the destination volume for atomic publish.
$temporaryPdf = Join-Path $temporaryRoot 'document.pdf'
$stagedPdf = Convert-ToExtendedFilePath (Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp.pdf'))
$backupPdf = Convert-ToExtendedFilePath (Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.bak.pdf'))

$bodyClass = 'pdf-exporting'
if ($ChatTranscript) { $bodyClass += ' chat-transcript' }
$chatPrintStyles = ''
if ($ChatTranscript) {
    # Measure with the production print rules already applied. Screen list/table
    # widths differ from A4 and can otherwise shrink perfectly valid formulas.
    $styleSource = [IO.File]::ReadAllText((Join-Path $webAssets 'styles.css'), [Text.Encoding]::UTF8)
    $printBlocks = [regex]::Matches($styleSource, '(?ms)^@media print[ \t]*\{(?<rules>.*?)^\}')
    if ($printBlocks.Count -eq 0) { throw 'Die lokalen PDF-Druckstile fehlen.' }
    $chatPrintStyles = ($printBlocks | ForEach-Object { $_.Groups['rules'].Value }) -join "`n"
}
$publicationStyles = ''
if ($ScientificPublication) {
    $bodyClass += ' scientific-publication'
    # This stylesheet is local, fixed application content. Manuscript data still
    # reaches the sanitized Markdown/KaTeX renderer only through its UTF-8 payload.
    $publicationStyles = @'
    @page {
      size: A4 portrait;
      margin: 20mm 17mm 19mm 24mm;
      @top-left { content: counter(page); color: #008fbd; font: 700 8pt Arial, sans-serif; vertical-align: bottom; padding-bottom: 3mm; }
      @top-center { color: #151515; font: 8pt Arial, sans-serif; vertical-align: bottom; padding-bottom: 3mm; }
      @bottom-right { content: counter(page) " / " counter(pages); color: #68727a; font: 7.5pt Arial, sans-serif; }
    }
    .scientific-publication .pdf-book {
      width: 169mm !important;
      font: 9.8pt/1.22 "Times New Roman", Cambria, Georgia, serif !important;
      color: #101010 !important;
    }
    .scientific-publication .message,
    .scientific-publication .message-body,
    .scientific-publication .message.assistant .message-body,
    .scientific-publication .message.assistant .message-content {
      display: block !important; width: 100% !important; max-width: none !important;
      font: inherit !important; line-height: inherit !important;
    }
    .scientific-publication .message-content { column-count: 1; column-gap: 0; width: 100% !important; }
    .scientific-publication .pdf-book__header,
    .scientific-publication .pdf-book__end-mark { display: none !important; }
    .scientific-publication .message { margin: 0 !important; }
    .scientific-publication .message-content > h1:first-child {
      column-span: all;
      margin: 0 0 3mm !important;
      font: 700 17pt/1.16 "Times New Roman", Cambria, Georgia, serif !important;
      color: #111 !important;
      text-align: left;
      hyphens: none;
      break-after: avoid-page;
    }
    .scientific-publication .publication-metadata {
      column-span: all;
      margin: 0 0 2mm !important;
      font: 7.5pt/1.35 Arial, sans-serif !important;
      text-align: left !important;
      color: #53626c !important;
      break-after: avoid-page;
    }
    .scientific-publication .publication-metadata strong {
      color: #008fbd !important;
      font-weight: 600;
      letter-spacing: .015em;
    }
    .scientific-publication .publication-note {
      column-span: all;
      margin: 0 0 4mm !important;
      padding: 1.8mm 2.5mm !important;
      border: 0 !important;
      border-left: 1.5pt solid #008fbd !important;
      background: #eff8fb !important;
      color: #3d515e !important;
      font: 7.5pt/1.3 Arial, sans-serif !important;
      break-inside: avoid-page;
    }
    .scientific-publication .publication-note p { text-align: left !important; }
    .scientific-publication .scientific-abstract {
      padding: 0;
      margin: 0 0 4mm;
      font-size: 9.8pt;
      line-height: 1.22;
      font-weight: 700;
    }
    .scientific-publication .message-content .scientific-abstract h2 {
      margin: 0 0 2mm !important;
      font: 700 10pt/1.2 "Times New Roman", Cambria, serif !important;
      text-align: left;
      color: #111 !important;
    }
    .scientific-publication .message-content h2 {
      margin: 5mm 0 2.5mm;
      font-size: 12.5pt !important;
      color: #111 !important;
      overflow-wrap: anywhere; hyphens: auto; break-after: avoid-page;
    }
    .scientific-publication .message-content h3 {
      margin: 3.5mm 0 2mm;
      font: 700 9.5pt/1.2 Arial, sans-serif !important;
      color: #008fbd !important;
      overflow-wrap: anywhere; hyphens: auto; break-after: avoid-page;
    }
    .scientific-publication .message-content h4,
    .scientific-publication .message-content h5,
    .scientific-publication .message-content h6 {
      margin: 3mm 0 1.8mm;
      font: 700 9pt/1.25 Arial, sans-serif !important;
      color: #111 !important;
      overflow-wrap: anywhere; hyphens: auto; break-after: avoid-page;
    }
    .scientific-publication .message-content p { margin-bottom: 2mm; orphans: 3; widows: 3; text-align: justify; }
    .scientific-publication .message-content li { margin-bottom: 1mm; }
    .scientific-publication .message-content a { color: #008fbd !important; overflow-wrap: anywhere; }
    .scientific-publication .publication-figure { margin: 3mm 0 4mm; padding: 0; break-inside: avoid; }
    .scientific-publication .publication-figure img { display: block; width: 100%; max-height: 92mm; object-fit: contain; border: 0; }
    .scientific-publication .publication-figure figcaption { margin-top: 1.5mm; font: 7.5pt/1.2 Arial, sans-serif; text-align: left; color: #151515; }
    .scientific-publication .publication-figure figcaption strong { color: #008fbd; }
    .scientific-publication .message-content pre {
      margin: 3mm 0 !important;
      padding: 2mm !important;
      background: #f3f8fa !important;
      border: .4pt solid #b9d5df !important;
      border-radius: 0 !important;
      font-size: 7.5pt !important;
      line-height: 1.3 !important;
      white-space: pre-wrap !important;
      overflow-wrap: anywhere !important;
    }
    .scientific-publication .message-content table { font-size: 8pt !important; break-inside: avoid; }
    .scientific-publication .message-content th { background: #eaf6fa !important; color: #111 !important; }
    .scientific-publication .message-content th, .scientific-publication .message-content td { padding: 1.3mm !important; }
    .scientific-publication .math-selectable.display {
      display: flex !important; align-items: center; gap: 2mm;
      position: relative; box-sizing: border-box; width: 169mm !important; max-width: 100% !important;
      margin: 3mm 0 !important; padding: 2mm !important; border: 0 !important; border-radius: 0 !important;
      background: #fffbe6 !important; break-inside: avoid; overflow: visible !important; font-size: 9.2pt !important;
    }
    .scientific-publication .math-selectable:not(.display) { display: inline !important; margin: 0 !important; padding: 0 !important; vertical-align: baseline; font-size: 1em !important; }
    .scientific-publication .math-selectable:not(.display) .math-render { display: inline !important; }
    .scientific-publication .math-selectable.display .math-render { display: block !important; flex: 1 1 auto; min-width: 0; font-size: inherit; }
    .scientific-publication .math-selectable.display.wide-equation { column-span: all; width: 169mm !important; }
    .scientific-publication .equation-number { flex: 0 0 auto; font: 8pt "Times New Roman", serif; color: #222; }
    .scientific-publication .katex-display { margin: 0 !important; }
    .scientific-publication .katex { font-size: 1em !important; }
    .scientific-publication .publication-toc { margin: 7mm 0 0; break-after: page; }
    .scientific-publication .publication-toc > h2 { margin: 0 0 3mm !important; break-after: avoid; }
    .scientific-publication .publication-toc ul { list-style: none !important; margin: 0 !important; padding: 0 !important; }
    .scientific-publication .publication-toc li { margin: 0 0 1.6mm !important; padding-left: calc(var(--toc-depth) * 3.5mm); break-inside: avoid; }
    .scientific-publication .publication-toc a { display: block; color: #006f94 !important; text-decoration: none !important; line-height: 1.35; overflow-wrap: anywhere; }
    .scientific-publication .publication-toc .publication-toc-chapter { font-weight: 700; }
    .scientific-publication .publication-reference-entry { text-align: left; overflow-wrap: anywhere; break-inside: avoid; }
    .scientific-publication .publication-reference-entry p { text-align: left; }
'@
}

$html = @"
<!doctype html>
<html lang="de">
<head>
  <meta charset="utf-8">
  <meta name="color-scheme" content="light">
  <link rel="stylesheet" href="$katexCssUri">
  <link rel="stylesheet" href="$stylesUri">
  <style>
    @page { size: A4 portrait; margin: 20mm 20mm 24mm 24mm; }
    html, body { margin: 0 !important; padding: 0 !important; background: #fff !important; color: #171717 !important; }
    .pdf-book { position: static !important; display: block !important; width: 100% !important; visibility: visible !important; }
    $chatPrintStyles
    /* Measure chat formulas at the same A4 width and font used when printing. */
    .chat-transcript .pdf-book { width: 166mm !important; }
    /* Validation needs the rendered DOM, not a second layout of megabytes of logs. */
    .chat-transcript.pdf-validation .pdf-book { display: none !important; }
    .chat-transcript .math-render { font-size: 10pt; }
    .chat-transcript .pdf-book .message-content p,
    .chat-transcript .pdf-book .message-content li { overflow-wrap: anywhere !important; }
    .chat-transcript .pdf-book .message-content p { text-align: left !important; }
    .chat-transcript .pdf-book .math-selectable:not(.display):not(.invalid) {
      width: auto !important; margin: 0 !important; padding: 0 !important;
    }
    .chat-transcript .math-selectable.invalid {
      display: block !important; max-width: 100% !important;
      break-inside: auto !important; white-space: normal !important;
    }
    .chat-transcript .pdf-book .math-selectable.invalid .math-source-text.fallback {
      display: inline !important; position: static !important; color: #171717 !important;
      white-space: pre-wrap !important; overflow-wrap: anywhere !important; word-break: normal !important;
      font: 9pt/1.4 Consolas, "Courier New", monospace !important;
    }
    .chat-transcript .math-export-note {
      display: block; color: #555 !important; font: 8pt/1.4 "Segoe UI", Arial, sans-serif;
      white-space: normal; margin-top: 1mm;
    }
    .chat-transcript .pdf-book .code-block.print-code-part { margin: 0 !important; border-radius: 0; }
    $publicationStyles
  </style>
  <script src="$katexScriptUri"></script>
  <script src="$markdownUri"></script>
</head>
<body class="$bodyClass">
  <article class="pdf-book pdf-book--message">
    <header class="pdf-book__header">
      <div class="pdf-book__eyebrow">Missum · DOKUMENT</div>
      <h1 id="document-title"></h1>
      <p>Erzeugtes Dokument · A4-Buchformat</p>
    </header>
    <section class="pdf-book__content">
      <article class="message assistant">
        <div class="message-body">
          <div id="document-content" class="message-content"></div>
        </div>
      </article>
    </section>
    <footer class="pdf-book__end-mark">◆</footer>
  </article>
  <script>
    const decodeUtf8 = value => new TextDecoder().decode(Uint8Array.from(atob(value), character => character.charCodeAt(0)));
    if (new URLSearchParams(location.search).has('missum-validation')) document.body.classList.add('pdf-validation');
    document.getElementById('document-title').textContent = decodeUtf8('$titleBase64');
    const documentContent = document.getElementById('document-content');
    documentContent.append(globalThis.missumMarkdown.render(decodeUtf8('$sourceBase64')));
    if (document.body.classList.contains('chat-transcript')) {
      // Large raw tool results can be several hundred kilobytes in one line.
      // Chromium pagination of one giant pre consumes gigabytes and stalls.
      // Bound its layout units while preserving every original character.
      for (const pre of documentContent.querySelectorAll('.code-block pre')) {
        const original = pre.querySelector('code')?.textContent;
        if (!original || original.length <= 6000) continue;
        const chunks = document.createDocumentFragment();
        for (let offset = 0; offset < original.length;) {
          let end = Math.min(offset + 6000, original.length);
          if (end < original.length) {
            const newline = original.slice(offset + 3000, end).lastIndexOf('\n');
            if (newline >= 0) end = offset + 3000 + newline + 1;
            const unit = original.charCodeAt(end - 1);
            if (unit >= 0xd800 && unit <= 0xdbff) end -= 1;
          }
          const block = document.createElement('div');
          block.className = 'code-block print-code-part';
          if (offset === 0) {
            const header = pre.parentElement.querySelector('.code-header');
            if (header) block.append(header);
          }
          const part = document.createElement('pre');
          const code = document.createElement('code');
          code.textContent = original.slice(offset, end);
          part.append(code); block.append(part); chunks.append(block);
          offset = end;
        }
        pre.parentElement.replaceWith(chunks);
      }
      for (const formula of documentContent.querySelectorAll('.math-selectable.invalid')) {
        // Keep the exact escaped source as text; never guess mathematical meaning.
        const note = document.createElement('span');
        note.className = 'math-export-note';
        note.textContent = 'LaTeX nicht darstellbar; Ausdruck unverändert übernommen.';
        formula.append(note);
      }
    }
    if (document.body.classList.contains('scientific-publication')) {
      const title = documentContent.querySelector(':scope > h1');
      const headingText = title ? title.textContent.trim() : 'Wissenschaftliche Untersuchung';
      const chapterText = headingText;
      const knownNumberedTitles = new Set(JSON.parse(decodeUtf8('$outlineBase64')));
      const runningHeader = document.createElement('style');
      runningHeader.textContent = '@page { @top-center { content: ' + JSON.stringify(chapterText) +
        '; white-space: nowrap; } @left-middle { content: ' + JSON.stringify(chapterText) +
        '; writing-mode: vertical-rl; background: #008fbd; color: white; width: 14mm; height: 110mm; margin-right: 10mm; padding-top: 3mm; padding-bottom: 3mm; text-align: center; vertical-align: middle; font: 700 9pt Arial, sans-serif; } }';
      document.head.append(runningHeader);
      if (title && title.nextElementSibling && title.nextElementSibling.tagName === 'P') {
        title.nextElementSibling.classList.add('publication-metadata');
      }
      let numberedSection = true;
      let referencesSection = false;
      const headingStack = [];
      const headingCounters = [];
      const headingNumbers = new Map();
      const ambiguousHeadingNumbers = new Set();
      for (const heading of documentContent.querySelectorAll('h2, h3, h4, h5, h6')) {
        heading.dataset.missumOriginalHeading = heading.textContent.trim();
        const level = Number(heading.tagName.slice(1));
        while (headingStack.length && headingStack[headingStack.length - 1].level >= level) headingStack.pop();
        const prefix = heading.dataset.missumOriginalHeading.match(/^\s*(\d+[a-z]?(?:\.\d+[a-z]?)*)([.)])?\s+/);
        const originalNumber = prefix?.[1];
        const numericSubject = prefix && !prefix[2] && /^(?:(?:[kMGTmunpμµ]?eV|[kMGTmunpμµ]?Hz|[kMGTmunpμµ]?W|[kMGTmunpμµ]?V|[kMGTmunpμµ]?A|[kMGTmunpμµ]?J|[kMGTmunpμµ]?s|[kMGTmunpμµ]?m|K|Pa|kg)\b|[%°])/u.test(heading.dataset.missumOriginalHeading.slice(prefix[0].length));
        // A full stop/parenthesis is explicit numbering. Bare numeric titles
        // remain meaningful text ("3 Dimensionen", "1.5 eV") unless legacy
        // metadata or a subordinate multi-part label identifies numbering.
        // Stale/repeated parent prefixes must not survive as duplicate numbers.
        const removableNumber = prefix && (knownNumberedTitles.has(heading.dataset.missumOriginalHeading)
          || Boolean(prefix[2]) || (!numericSubject && level > 2 && originalNumber.includes('.')));
        if (removableNumber) {
          let remaining = prefix[0].length;
          const textNodes = document.createTreeWalker(heading, NodeFilter.SHOW_TEXT);
          let textNode;
          while (remaining && (textNode = textNodes.nextNode())) {
            const count = Math.min(remaining, textNode.textContent.length);
            textNode.textContent = textNode.textContent.slice(count);
            remaining -= count;
          }
        }
        if (level === 2) {
          numberedSection = !/^(Zusammenfassung|Abstract|Inhaltsverzeichnis)$/i.test(heading.textContent.trim());
          referencesSection = /^(Literatur(?: und Daten)?|Quellen|Erg.nzende Originalquellen|References|Bibliography)(?:$|\s*[:–—-])/i.test(heading.textContent.trim());
        }
        if (referencesSection) {
          heading.classList.add('publication-references-heading');
          for (let element = heading.nextElementSibling; element && !/^H[1-6]$/.test(element.tagName); element = element.nextElementSibling) {
            element.querySelectorAll('li').forEach(entry => entry.classList.add('publication-reference-entry'));
          }
        }
        if (!numberedSection) continue;
        const depth = headingStack.length;
        headingCounters[depth] = (headingCounters[depth] || 0) + 1;
        headingCounters.length = depth + 1;
        headingStack.push({ level });
        heading.dataset.missumOutlineDepth = String(depth);
        heading.dataset.missumMainTopic = String(headingCounters[0]);
        const number = document.createElement('span');
        const newNumber = headingCounters.join('.');
        number.textContent = newNumber + '  ';
        const oldNumber = removableNumber ? originalNumber : null;
        if (oldNumber) {
          if (headingNumbers.has(oldNumber)) ambiguousHeadingNumbers.add(oldNumber);
          else headingNumbers.set(oldNumber, newNumber);
        }
        heading.prepend(number);
      }
      // Preserve explicit textual section references when their original number
      // identifies exactly one heading. Mathematical values, code and ambiguous
      // references remain unchanged and require the scientific author's review.
      const references = document.createTreeWalker(documentContent, NodeFilter.SHOW_TEXT);
      let referenceText;
      while ((referenceText = references.nextNode())) {
        if (referenceText.parentElement?.closest('h1,h2,h3,h4,h5,h6,pre,code,.math-selectable,.katex,script,style')) continue;
        referenceText.textContent = referenceText.textContent.replace(/(\b(?:Abschnitt|Kapitel|Unterabschnitt|Kap\.|Abschn\.)\s+)(\d+[a-z]?(?:\.\d+[a-z]?)*)(?=[\s,;:)\].]|$)/g,
          (match, prefix, oldNumber) => headingNumbers.has(oldNumber) && !ambiguousHeadingNumbers.has(oldNumber)
            ? prefix + headingNumbers.get(oldNumber) : match);
      }
      const numberedHeadings = Array.from(documentContent.querySelectorAll('[data-missum-main-topic]'));
      const mainTopicFor = element => {
        let topic = '';
        for (const heading of numberedHeadings) {
          if (!(heading.compareDocumentPosition(element) & Node.DOCUMENT_POSITION_FOLLOWING)) break;
          topic = heading.dataset.missumMainTopic;
        }
        return topic;
      };
      const nextTopicNumber = (counters, element) => {
        const topic = mainTopicFor(element);
        const value = (counters.get(topic) || 0) + 1;
        counters.set(topic, value);
        return topic ? topic + '.' + value : String(value);
      };
      const equationNumbers = new Map();
      for (const formula of documentContent.querySelectorAll('.math-selectable.display')) {
        const equationNumber = nextTopicNumber(equationNumbers, formula);
        const existingTag = formula.querySelector('.tag');
        const number = document.createElement('span');
        number.className = 'equation-number';
        number.textContent = existingTag ? existingTag.textContent.trim() : '(' + equationNumber + ')';
        if (existingTag) existingTag.remove();
        formula.append(number);
      }
      const localFigures = JSON.parse(decodeUtf8('$figureBase64'));
      const figureNumbers = new Map();
      for (const entry of localFigures) {
        const placeholder = Array.from(documentContent.querySelectorAll('p')).find(item => item.textContent.trim() === entry.token);
        if (!placeholder) continue;
        const figure = document.createElement('figure');
        figure.className = 'publication-figure';
        const image = document.createElement('img');
        image.src = entry.url;
        image.alt = entry.label;
        image.loading = 'eager';
        const caption = document.createElement('figcaption');
        const label = document.createElement('strong');
        label.textContent = 'Abbildung ' + nextTopicNumber(figureNumbers, placeholder) + '  ';
        caption.append(label, document.createTextNode(entry.label));
        figure.append(image, caption);
        placeholder.replaceWith(figure);
      }
      const note = documentContent.querySelector(':scope > blockquote');
      if (note) note.classList.add('publication-note');
      const abstractHeading = Array.from(documentContent.querySelectorAll(':scope > h2'))
        .find(heading => heading.textContent.trim() === 'Zusammenfassung');
      if (abstractHeading) {
        const abstract = document.createElement('section');
        abstract.className = 'scientific-abstract';
        abstractHeading.before(abstract);
        let current = abstractHeading;
        do {
          const next = current.nextElementSibling;
          abstract.append(current);
          current = next;
        } while (current && current.tagName !== 'H2');
      }
      // Use the final rendered order and numbering, including unnumbered front
      // matter and references. Unique targets keep repeated titles unambiguous.
      const outlineHeadings = Array.from(documentContent.querySelectorAll('h2, h3, h4, h5, h6'));
      if (outlineHeadings.length) {
        const contents = document.createElement('nav');
        contents.className = 'publication-toc';
        contents.setAttribute('aria-label', 'Inhaltsverzeichnis');
        const contentsHeading = document.createElement('h2');
        contentsHeading.textContent = 'Inhaltsverzeichnis';
        const entries = document.createElement('ul');
        outlineHeadings.forEach((heading, index) => {
          heading.id = 'missum-publication-section-' + (index + 1);
          const entry = document.createElement('li');
          entry.style.setProperty('--toc-depth', heading.dataset.missumOutlineDepth || String(Number(heading.tagName.slice(1)) - 2));
          const destination = document.createElement('a');
          destination.href = '#' + heading.id;
          // Keep the displayed KaTeX label rather than mixing its visible text,
          // hidden MathML and raw TeX into a duplicated plain-text caption.
          const label = heading.cloneNode(true);
          label.querySelectorAll('.math-source-text, .katex-mathml').forEach(source => source.remove());
          label.querySelectorAll('a').forEach(link => link.replaceWith(...link.childNodes));
          const caption = label.textContent.trim();
          heading.setAttribute('aria-label', caption);
          destination.setAttribute('aria-label', caption);
          destination.append(...label.childNodes);
          if (heading.tagName === 'H2') destination.className = 'publication-toc-chapter';
          entry.append(destination);
          entries.append(entry);
        });
        contents.append(contentsHeading, entries);
        const firstSection = Array.from(documentContent.children).find(element => /^H[2-6]$/.test(element.tagName));
        if (firstSection) firstSection.before(contents);
        else documentContent.append(contents);
      }
    }
    const finishDocument = () => {
      if (document.body.classList.contains('chat-transcript')) {
        const pageWidth = 166 * 96 / 25.4;
        for (const formula of documentContent.querySelectorAll('.math-selectable:not(.invalid)')) {
          const math = formula.querySelector('.math-render');
          if (!math) continue;
          const available = Math.min(pageWidth, formula.parentElement.clientWidth || pageWidth) - 2;
          const bounds = math.getBoundingClientRect();
          const width = Math.max(math.scrollWidth, ...Array.from(math.querySelectorAll('.base')).map(item => item.getBoundingClientRect().right - bounds.left));
          if (available > 0 && width > available) {
            formula.classList.add('display');
            math.style.fontSize = ((available / width) * parseFloat(getComputedStyle(math).fontSize) * .99) + 'px';
          }
        }
      }
      if (document.body.classList.contains('scientific-publication')) {
        for (const formula of documentContent.querySelectorAll('.math-selectable.display')) {
          const math = formula.querySelector('.math-render');
          if (!math) continue;
          const number = formula.querySelector('.equation-number');
          const available = Math.min(math.clientWidth, 169 * 96 / 25.4 - 6 * 96 / 25.4 - (number ? number.getBoundingClientRect().width : 0));
          const bounds = math.getBoundingClientRect();
          const width = Math.max(math.scrollWidth, ...Array.from(math.querySelectorAll('.base')).map(item => item.getBoundingClientRect().right - bounds.left));
          if (available > 0 && width > available) {
            if (available / width < 0.78) {
              formula.classList.add('wide-equation');
              const wideAvailable = 169 * 96 / 25.4 - 6 * 96 / 25.4 - (number ? number.getBoundingClientRect().width : 0);
              if (width > wideAvailable) math.style.fontSize = ((wideAvailable / width) * parseFloat(getComputedStyle(math).fontSize)) + 'px';
            }
            else math.style.fontSize = ((available / width) * parseFloat(getComputedStyle(math).fontSize)) + 'px';
          }
        }
      }
      document.body.dataset.missumPdfReady = 'true';
      document.body.dataset.missumKatexInvalid = String(documentContent.querySelectorAll('.math-selectable.invalid').length);
      const headings = Array.from(documentContent.querySelectorAll('h2, h3, h4, h5, h6'));
      document.body.dataset.missumKatexErrors = encodeURIComponent(JSON.stringify(
        Array.from(documentContent.querySelectorAll('.math-selectable.invalid')).slice(0, $diagnosticLimit).map(formula => {
          const preceding = headings.filter(heading => Boolean(heading.compareDocumentPosition(formula) & Node.DOCUMENT_POSITION_FOLLOWING));
          const heading = preceding.length ? preceding[preceding.length - 1] : null;
          return { section: heading ? (heading.dataset.missumOriginalHeading || heading.textContent.trim()).slice(0, $diagnosticTextLimit) : '',
            source: (formula.querySelector('.math-source-text')?.textContent || '').slice(0, $diagnosticTextLimit),
            reason: (formula.dataset.mathError || '').slice(0, $diagnosticTextLimit) };
        })));
      document.body.dataset.missumKatexRendered = String(documentContent.querySelectorAll('.math-render[data-math-typeset="true"] .katex').length);
      document.body.dataset.missumFigureInvalid = String(Array.from(documentContent.querySelectorAll('.publication-figure img')).filter(image => !image.complete || image.naturalWidth === 0).length);
      if (document.body.classList.contains('scientific-publication')) {
        const issues = [];
        const title = documentContent.querySelector(':scope > h1');
        const titleText = title ? title.textContent.trim().replace(/^1\s+/, '') : '';
        const context = document.createElement('canvas').getContext('2d');
        if (context && titleText) {
          context.font = '700 9pt Arial';
          const sideWidth = context.measureText(titleText).width;
          context.font = '8pt Arial';
          const headWidth = context.measureText('1  ' + titleText).width;
          if (sideWidth > 104 * 96 / 25.4 || headWidth > 158 * 96 / 25.4)
            issues.push({ target: 'title', section: '', text: titleText,
              reason: 'Publikationstitel passt nicht vollständig in Seitenmarke oder laufende Kopfzeile; fachlich kürzen.' });
        }
        for (const heading of documentContent.querySelectorAll('h2, h3, h4, h5, h6')) {
          if (heading.closest('.publication-toc')) continue;
          const bounds = heading.getBoundingClientRect();
          const style = getComputedStyle(heading);
          const lineHeight = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
          if (heading.scrollWidth > heading.clientWidth + 1 || bounds.height > lineHeight * 2.15)
            issues.push({ target: 'section', section: heading.dataset.missumOriginalHeading || heading.textContent.trim(),
              text: heading.textContent.trim(), reason: 'Überschrift überschreitet die Satzbreite oder zwei Zeilen; fachlich kürzen.' });
        }
        document.body.dataset.missumHeadingInvalid = String(issues.length);
        document.body.dataset.missumHeadingErrors = encodeURIComponent(JSON.stringify(issues.slice(0, Math.max(12, $diagnosticLimit))));
      }
    };
    if (document.body.classList.contains('scientific-publication')) {
      const images = Array.from(documentContent.querySelectorAll('img')).map(image => image.decode ? image.decode().catch(() => {}) : image.complete ? Promise.resolve() :
        new Promise(resolve => { image.addEventListener('load', resolve, { once: true }); image.addEventListener('error', resolve, { once: true }); }));
      Promise.all([document.fonts.ready, ...images]).then(finishDocument);
    } else document.fonts.ready.then(finishDocument);
  </script>
</body>
</html>
"@

try {
    [IO.File]::WriteAllText($htmlPath, $html, [Text.UTF8Encoding]::new($false))
    $edge = Resolve-EdgeExecutable
    $commonArguments = @(
        '--headless=new',
        '--disable-gpu',
        '--disable-extensions',
        '--disable-background-networking',
        '--no-first-run',
        '--allow-file-access-from-files',
        '--run-all-compositor-stages-before-draw',
        '--virtual-time-budget=5000'
    )
    $dumpArguments = $commonArguments + @(
        ('--user-data-dir=' + $validationProfilePath),
        '--dump-dom',
        (([Uri]$htmlPath).AbsoluteUri + '?missum-validation=1')
    )

    Write-DocumentProgress 'Prüfung: Formeln, Überschriften, Schriftarten und Abbildungen.'
    $validationResult = Invoke-EdgeProcess -Executable $edge -CommandArguments $dumpArguments
    $renderedDom = $validationResult.Output
    if ($validationResult.ExitCode -ne 0) {
        throw "Microsoft Edge hat die KaTeX-Prüfung mit Exit-Code $($validationResult.ExitCode) beendet."
    }
    if ($renderedDom -notmatch 'data-missum-pdf-ready="true"') {
        throw 'Der Missum-Markdown-/KaTeX-Renderer wurde vor der PDF-Erzeugung nicht vollständig initialisiert.'
    }
    Save-DocumentDiagnostics $renderedDom
    if (-not $ChatTranscript -and $renderedDom -match 'data-missum-katex-invalid="([1-9][0-9]*)"') {
        $invalidCount = $Matches[1]
        $formulaDetails = ''
        if ($renderedDom -match 'data-missum-katex-errors="([^"]*)"') {
            try {
                $formulaErrors = [Uri]::UnescapeDataString($Matches[1]) | ConvertFrom-Json
                $formulaDetails = (@($formulaErrors) | ForEach-Object {
                    $detail = 'Abschnitt "' + $_.section + '", Ausdruck: ' + $_.source
                    if ($_.reason) { $detail += ', Ursache: ' + $_.reason }
                    $detail
                }) -join '; '
            } catch { $formulaDetails = '' }
        }
        throw "Die PDF wurde nicht erzeugt, weil $invalidCount mathematische Ausdrücke nicht KaTeX-kompatibel sind. $formulaDetails"
    }
    if ($ScientificPublication -and $renderedDom -match 'data-missum-figure-invalid="([1-9][0-9]*)"') {
        throw "Die Publikation enthaelt $($Matches[1]) nicht lesbare Abbildungen. Die bisherige PDF bleibt erhalten."
    }
    if ($ScientificPublication -and $renderedDom -match 'data-missum-heading-invalid="([1-9][0-9]*)"') {
        $headingDetails = ''
        if ($renderedDom -match 'data-missum-heading-errors="([^"]*)"') {
            $headingErrors = [Uri]::UnescapeDataString($Matches[1]) | ConvertFrom-Json
            $headingDetails = (@($headingErrors) | ForEach-Object {
                if ($_.target -eq 'title') { 'Publikationstitel "' + $_.text + '": ' + $_.reason }
                else { 'Abschnitt "' + $_.section + '": ' + $_.reason }
            }) -join '; '
        }
        throw "Die PDF wurde nicht erzeugt, weil eine Überschrift nicht vollständig lesbar ist. $headingDetails Die vorherige PDF bleibt erhalten."
    }

    if ($ValidateOnly) {
        Write-DocumentProgress 'Prüfung erfolgreich. Die vorhandene PDF wurde nicht verändert.'
        return
    }
    Write-DocumentProgress 'Prüfung erfolgreich. PDF mit Inhaltsverzeichnis und Lesezeichen wird erzeugt.'
    $publicationArguments = @()
    if ($ScientificPublication) {
        $publicationArguments = @('--export-tagged-pdf', '--generate-pdf-document-outline')
    }
    $arguments = $commonArguments + $publicationArguments + @(
        ('--user-data-dir=' + $printProfilePath),
        '--no-pdf-header-footer',
        ('--print-to-pdf=' + $temporaryPdf),
        ([Uri]$htmlPath).AbsoluteUri
    )

    $printResult = Invoke-EdgeProcess -Executable $edge -CommandArguments $arguments
    if ($printResult.ExitCode -ne 0) {
        throw "Microsoft Edge hat die PDF-Erzeugung mit Exit-Code $($printResult.ExitCode) beendet."
    }
    if (-not (Test-Path -LiteralPath $temporaryPdf -PathType Leaf)) {
        throw 'Microsoft Edge hat keine PDF-Datei erzeugt.'
    }

    $pdfBytes = [IO.File]::ReadAllBytes($temporaryPdf)
    if ($pdfBytes.Length -lt 1024 -or [Text.Encoding]::ASCII.GetString($pdfBytes, 0, 5) -ne '%PDF-') {
        throw 'Die erzeugte Datei ist kein gültiges, nicht leeres PDF.'
    }

    [IO.File]::Copy($temporaryPdf, $stagedPdf, $false)
    if ([IO.File]::Exists($outputFilePath)) {
        [IO.File]::Replace($stagedPdf, $outputFilePath, $backupPdf, $true)
        [IO.File]::Delete($backupPdf)
    }
    else {
        [IO.File]::Move($stagedPdf, $outputFilePath)
    }
    Write-Output $output
}
finally {
    if (Test-Path -LiteralPath $temporaryPdf -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPdf -Force -ErrorAction SilentlyContinue
    }
    if ([IO.File]::Exists($stagedPdf)) {
        [IO.File]::Delete($stagedPdf)
    }
    if ([IO.File]::Exists($backupPdf)) {
        [IO.File]::Delete($backupPdf)
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

