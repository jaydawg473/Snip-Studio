param([switch]$Check, [string]$Destination = 'dist', [switch]$UseExistingIcons)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$metadataFolder = Join-Path $env:WINDIR 'System32\WinMetadata'
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Net.Http.dll', '/reference:System.Web.Extensions.dll', '/reference:System.Runtime.dll', '/reference:System.Runtime.WindowsRuntime.dll', '/reference:System.Runtime.InteropServices.WindowsRuntime.dll')
foreach ($metadata in @('Windows.Foundation.winmd', 'Windows.Graphics.winmd', 'Windows.Media.winmd', 'Windows.Storage.winmd', 'Windows.Globalization.winmd', 'Windows.ApplicationModel.winmd')) {
    $metadataPath = Join-Path $metadataFolder $metadata
    if (-not (Test-Path -LiteralPath $metadataPath)) { throw "Windows text-recognition metadata was not found: $metadata" }
    $references += "/reference:$metadataPath"
}
if ($UseExistingIcons) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'assets\SnipStudio.ico'))) { throw 'The existing application icon was not found.' }
} else {
    $iconTool = Join-Path $PSScriptRoot 'tools\BuildIcons.exe'
    & $compiler /nologo /target:exe "/out:$iconTool" /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'IconArtwork.cs') (Join-Path $PSScriptRoot 'tools\BuildIcons.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Icon tool build failed.' }
    & $iconTool (Join-Path $PSScriptRoot 'assets')
    if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
}
$outputFolder = Join-Path $PSScriptRoot $Destination
[IO.Directory]::CreateDirectory($outputFolder) | Out-Null
$sources = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | ForEach-Object { $_.FullName })
$appFile = Join-Path $outputFolder 'Snip Studio.exe'
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /debug- /warn:4 /utf8output "/out:$appFile" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\assets\SnipStudio.ico" "/resource:$PSScriptRoot\assets\SnipStudio.ico,SnipStudio.AppIcon" $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($appFile + '.config') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination (Join-Path $outputFolder 'README.txt') -Force
if ($Check) {
    $checksFolder = Join-Path $PSScriptRoot 'checks'
    $checkSources = @(Get-ChildItem -LiteralPath $checksFolder -Filter '*.cs' | ForEach-Object { $_.FullName })
    $checkFile = Join-Path $checksFolder 'SnipStudio.Checks.exe'
    & $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output "/out:$checkFile" $references "/reference:$appFile" $checkSources
    if ($LASTEXITCODE -ne 0) { throw 'Check build failed.' }
    Copy-Item -LiteralPath $appFile -Destination (Join-Path $checksFolder 'Snip Studio.exe') -Force
    & $checkFile
    if ($LASTEXITCODE -ne 0) { throw 'Checks failed.' }
    $ocrChecks = Join-Path $checksFolder 'ocr\OcrChecks.exe'
    & $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output "/out:$ocrChecks" $references (Join-Path $PSScriptRoot 'TextRecognition.cs') (Join-Path $checksFolder 'ocr\OcrChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Text-recognition checks could not be built.' }
    & $ocrChecks
    if ($LASTEXITCODE -ne 0) { throw 'Text-recognition checks failed.' }
    $snapChecks = Join-Path $checksFolder 'ocr\SnapChecks.exe'
    & $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output "/out:$snapChecks" $references (Join-Path $PSScriptRoot 'TextRecognition.cs') (Join-Path $PSScriptRoot 'TextSnapSelection.cs') (Join-Path $checksFolder 'ocr\SnapChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Highlight snapping checks could not be built.' }
    & $snapChecks
    if ($LASTEXITCODE -ne 0) { throw 'Highlight snapping checks failed.' }
    $hdrChecks = Join-Path $checksFolder 'hdr\HdrChecks.exe'
    & $compiler /nologo /target:exe /platform:anycpu /optimize+ /utf8output "/out:$hdrChecks" $references (Join-Path $PSScriptRoot 'HdrCaptureService.cs') (Join-Path $checksFolder 'hdr\HdrChecks.cs')
    if ($LASTEXITCODE -ne 0) { throw 'HDR conversion checks could not be built.' }
    & $hdrChecks
    if ($LASTEXITCODE -ne 0) { throw 'HDR conversion checks failed.' }
    & (Join-Path $checksFolder 'video\run-checks.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Video checks failed.' }
}
Write-Output "Built $appFile"
