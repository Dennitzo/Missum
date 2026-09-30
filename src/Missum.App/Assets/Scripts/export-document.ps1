[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [Parameter(Mandatory = $true)]
    [string]$WebAssetsPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [switch]$ScientificPublication
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$stylesUri = Convert-ToFileUri (Join-Path $webAssets 'styles.css')
$markdownUri = Convert-ToFileUri (Join-Path $webAssets 'markdown.js')
$katexRoot = Join-Path $webAssets 'vendor\katex\0.16.10'
$katexCssUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.css')
$katexScriptUri = Convert-ToFileUri (Join-Path $katexRoot 'katex.min.js')

$sourceText = [IO.File]::ReadAllText($source, [Text.Encoding]::UTF8)
$localFigures = [Collections.Generic.List[object]]::new()
if ($ScientificPublication) {
    $sourceDirectory = [IO.Path]::GetDirectoryName($source).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $sourceText = [regex]::Replace($sourceText, '!\[(?<label>[^\]\r\n]*)\]\((?<path>[^\)\r\n]+)\)', {
        param($match)
        $requested = $match.Groups['path'].Value.Trim().Trim('<', '>')
        if ($requested -notmatch '^figures/(?<hash>[0-9a-f]{64})\.(?<type>png|jpg)$' -or $localFigures.Count -ge 12) { return $match.Value }
        $expectedHash = $Matches['hash']
        $imageType = $Matches['type']
        try { $figurePath = [IO.Path]::GetFullPath((Join-Path $sourceDirectory ([Uri]::UnescapeDataString($requested)))) }
        catch { return $match.Value }
        if (-not $figurePath.StartsWith($sourceDirectory, [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $figurePath -PathType Leaf)) { return $match.Value }
        $fileInfo = Get-Item -LiteralPath $figurePath
        $figureDirectory = Get-Item -LiteralPath ([IO.Path]::GetDirectoryName($figurePath))
        if (($fileInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($figureDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            $fileInfo.Length -lt 8 -or $fileInfo.Length -gt 8MB) { return $match.Value }
        $totalBytes = 0L
        foreach ($existingFigure in $localFigures) { $totalBytes += $existingFigure.length }
        if ($totalBytes + $fileInfo.Length -gt 32MB) { return $match.Value }
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
$temporaryPdf = Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp.pdf')
$backupPdf = Join-Path $outputDirectory ('.' + [IO.Path]::GetFileNameWithoutExtension($output) + '.' + [Guid]::NewGuid().ToString('N') + '.bak.pdf')

$bodyClass = 'pdf-exporting'
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
    .scientific-publication .message-content { column-count: 2; column-gap: 5mm; column-fill: auto; }
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
    }
    .scientific-publication .message-content h3 {
      margin: 3.5mm 0 2mm;
      font: 700 9.5pt/1.2 Arial, sans-serif !important;
      color: #008fbd !important;
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
      position: relative; box-sizing: border-box; width: 82mm !important; max-width: 100% !important;
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
    document.getElementById('document-title').textContent = decodeUtf8('$titleBase64');
    const documentContent = document.getElementById('document-content');
    documentContent.append(globalThis.missumMarkdown.render(decodeUtf8('$sourceBase64')));
    if (document.body.classList.contains('scientific-publication')) {
      const title = documentContent.querySelector(':scope > h1');
      const headingText = title ? title.textContent.trim() : 'Wissenschaftliche Untersuchung';
      const explicitChapter = headingText.match(/^(\d+)\s+/);
      const chapter = explicitChapter ? explicitChapter[1] : '1';
      const chapterText = explicitChapter ? headingText.replace(/^\d+\s+/, '') : headingText;
      if (title && !explicitChapter) title.prepend(document.createTextNode(chapter + '  '));
      const runningHeader = document.createElement('style');
      runningHeader.textContent = '@page { @top-center { content: ' + JSON.stringify(chapter + '  ' + chapterText.slice(0, 88)) +
        '; } @left-middle { content: ' + JSON.stringify(chapterText.slice(0, 42)) +
        '; writing-mode: vertical-rl; background: #008fbd; color: white; width: 14mm; height: 57mm; margin-right: 10mm; padding-top: 3mm; padding-bottom: 3mm; text-align: center; vertical-align: middle; font: 700 9pt Arial, sans-serif; } }';
      document.head.append(runningHeader);
      if (title && title.nextElementSibling && title.nextElementSibling.tagName === 'P') {
        title.nextElementSibling.classList.add('publication-metadata');
      }
      let sectionNumber = 0;
      for (const heading of documentContent.querySelectorAll(':scope > h2')) {
        if (/^(Zusammenfassung|Abstract|Literatur|Quellen|Erg.nzende Originalquellen)/i.test(heading.textContent.trim())) continue;
        if (!/^\d+(?:\.\d+)*[.)]?\s+/.test(heading.textContent.trim())) {
          sectionNumber += 1;
          const number = document.createElement('span');
          number.textContent = chapter + '.' + sectionNumber + '  ';
          heading.prepend(number);
        }
      }
      let equationNumber = 0;
      for (const formula of documentContent.querySelectorAll('.math-selectable.display')) {
        equationNumber += 1;
        const existingTag = formula.querySelector('.tag');
        const number = document.createElement('span');
        number.className = 'equation-number';
        number.textContent = existingTag ? existingTag.textContent.trim() : '(' + chapter + '.' + equationNumber + ')';
        if (existingTag) existingTag.remove();
        formula.append(number);
      }
      const localFigures = JSON.parse(decodeUtf8('$figureBase64'));
      let figureNumber = 0;
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
        label.textContent = 'Abbildung ' + chapter + '.' + (++figureNumber) + '  ';
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
    }
    const finishDocument = () => {
      if (document.body.classList.contains('scientific-publication')) {
        for (const formula of documentContent.querySelectorAll('.math-selectable.display')) {
          const math = formula.querySelector('.math-render');
          if (!math) continue;
          const number = formula.querySelector('.equation-number');
          const available = Math.min(math.clientWidth, 82 * 96 / 25.4 - 6 * 96 / 25.4 - (number ? number.getBoundingClientRect().width : 0));
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
      document.body.dataset.missumKatexRendered = String(documentContent.querySelectorAll('.math-render[data-math-typeset="true"] .katex').length);
      document.body.dataset.missumFigureInvalid = String(Array.from(documentContent.querySelectorAll('.publication-figure img')).filter(image => !image.complete || image.naturalWidth === 0).length);
    };
    if (document.body.classList.contains('scientific-publication')) {
      const images = Array.from(documentContent.querySelectorAll('img')).map(image => image.decode ? image.decode().catch(() => {}) : image.complete ? Promise.resolve() :
        new Promise(resolve => { image.addEventListener('load', resolve, { once: true }); image.addEventListener('error', resolve, { once: true }); }));
      Promise.all([document.fonts.ready, ...images]).then(finishDocument);
    } else finishDocument();
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
        ([Uri]$htmlPath).AbsoluteUri
    )

    $validationResult = Invoke-EdgeProcess -Executable $edge -CommandArguments $dumpArguments
    $renderedDom = $validationResult.Output
    if ($validationResult.ExitCode -ne 0) {
        throw "Microsoft Edge hat die KaTeX-Prüfung mit Exit-Code $($validationResult.ExitCode) beendet."
    }
    if ($renderedDom -notmatch 'data-missum-pdf-ready="true"') {
        throw 'Der Missum-Markdown-/KaTeX-Renderer wurde vor der PDF-Erzeugung nicht vollständig initialisiert.'
    }
    if ($renderedDom -match 'data-missum-katex-invalid="([1-9][0-9]*)"') {
        throw "Die PDF wurde nicht erzeugt, weil $($Matches[1]) mathematische Ausdrücke nicht KaTeX-kompatibel sind."
    }
    if ($ScientificPublication -and $renderedDom -match 'data-missum-figure-invalid="([1-9][0-9]*)"') {
        throw "Die Publikation enthaelt $($Matches[1]) nicht lesbare Abbildungen. Die bisherige PDF bleibt erhalten."
    }

    $arguments = $commonArguments + @(
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

    if (Test-Path -LiteralPath $output -PathType Leaf) {
        [IO.File]::Replace($temporaryPdf, $output, $backupPdf, $true)
        [IO.File]::Delete($backupPdf)
    }
    else {
        [IO.File]::Move($temporaryPdf, $output)
    }
    Write-Output $output
}
finally {
    if (Test-Path -LiteralPath $temporaryPdf -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPdf -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $backupPdf -PathType Leaf) {
        Remove-Item -LiteralPath $backupPdf -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

