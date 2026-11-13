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

function Get-VerifiedEmojiResources([string]$ProjectFolder = $PSScriptRoot) {
    $emojiFolder = Join-Path $ProjectFolder 'assets\emoji'
    $emojiManifest = Get-Content -LiteralPath (Join-Path $emojiFolder 'manifest.json') -Raw | ConvertFrom-Json
    if ($emojiManifest.schemaVersion -ne 1 -or $emojiManifest.library -cne 'Microsoft Fluent Emoji' -or
        $emojiManifest.artworkStyle -cne '3D' -or $emojiManifest.license.spdx -cne 'MIT' -or
        $emojiManifest.license.localFile -cne 'LICENSE.txt' -or @($emojiManifest.assets).Count -ne 24) {
        throw 'The reviewed 24-image Fluent Emoji manifest is required.'
    }
    if ($emojiManifest.license.sha256 -isnot [string] -or $emojiManifest.license.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
        (Get-FileHash -LiteralPath (Join-Path $emojiFolder 'LICENSE.txt') -Algorithm SHA256).Hash -ine $emojiManifest.license.sha256) {
        throw 'The Fluent Emoji license differs from its reviewed manifest.'
    }
    $emojiEntries = New-Object 'System.Collections.Generic.Dictionary[string,object]' ([StringComparer]::Ordinal)
    foreach ($emoji in @($emojiManifest.assets)) {
        if ($emoji.key -isnot [string] -or $emoji.key -cnotmatch '\A[a-z][a-z0-9_-]{0,63}\z' -or $emojiEntries.ContainsKey($emoji.key) -or
            $emoji.filename -isnot [string] -or $emoji.filename -cne ($emoji.key + '.png') -or
            $emoji.resourceName -isnot [string] -or $emoji.resourceName -cne ('SnipStudio.Emoji.' + $emoji.key + '.png') -or
            $emoji.sha256 -isnot [string] -or $emoji.sha256 -cnotmatch '\A[0-9a-f]{64}\z') {
            throw 'Invalid or duplicate Fluent Emoji resource entry.'
        }
        $emojiPath = Join-Path $emojiFolder $emoji.filename
        if (-not (Test-Path -LiteralPath $emojiPath -PathType Leaf) -or
            (Get-Item -LiteralPath $emojiPath -Force).LinkType -in @('SymbolicLink','Junction') -or
            (Get-FileHash -LiteralPath $emojiPath -Algorithm SHA256).Hash -ine $emoji.sha256) {
            throw ('Fluent Emoji bytes differ from the reviewed manifest: ' + $emoji.key)
        }
        $emojiEntries.Add($emoji.key,$emoji)
    }
    $expectedEmojiNames = @($emojiEntries.Values | ForEach-Object { $_.filename } | Sort-Object)
    $actualEmojiNames = @(Get-ChildItem -LiteralPath $emojiFolder -File -Force -Filter '*.png' | ForEach-Object { $_.Name } | Sort-Object)
    if (@(Compare-Object $expectedEmojiNames $actualEmojiNames -CaseSensitive).Count -ne 0) {
        throw 'The Fluent Emoji folder contains missing or unexpected PNG files.'
    }
    [string[]]$emojiKeys = @($emojiEntries.Keys)
    [Array]::Sort($emojiKeys,[StringComparer]::Ordinal)
    foreach ($emojiKey in $emojiKeys) {
        $emoji = $emojiEntries[$emojiKey]
        '/resource:' + (Join-Path $emojiFolder $emoji.filename) + ',' + $emoji.resourceName
    }
}

function Copy-OpenSourceNotices([string]$Folder) {
    $emojiFolder = Join-Path $PSScriptRoot 'assets\emoji'
    $noticeFolder = Join-Path $Folder 'licenses'
    [IO.Directory]::CreateDirectory($noticeFolder) | Out-Null
    $combinedNotices = "Snip Studio - third-party notices`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $emojiFolder 'ATTRIBUTION.txt')) + "`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $emojiFolder 'LICENSE.txt')) + "`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'translation-bundle\runtime\licenses\NOTICES.txt'))
    [IO.File]::WriteAllText((Join-Path $noticeFolder 'NOTICES.txt'),$combinedNotices,[Text.UTF8Encoding]::new($false))
    [IO.File]::Copy((Join-Path $emojiFolder 'LICENSE.txt'),(Join-Path $noticeFolder 'FluentEmoji-LICENSE.txt'),$true)
    [IO.File]::Copy((Join-Path $emojiFolder 'ATTRIBUTION.txt'),(Join-Path $noticeFolder 'FluentEmoji-ATTRIBUTION.txt'),$true)
    [IO.File]::Copy((Join-Path $emojiFolder 'manifest.json'),(Join-Path $noticeFolder 'FluentEmoji-manifest.json'),$true)
}

$emojiResources = @(Get-VerifiedEmojiResources)
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
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /debug- /warn:4 /utf8output "/out:$appFile" "/win32manifest:$PSScriptRoot\app.manifest" "/win32icon:$PSScriptRoot\assets\SnipStudio.ico" "/resource:$PSScriptRoot\assets\SnipStudio.ico,SnipStudio.AppIcon" $emojiResources $references $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($appFile + '.config') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.txt') -Destination (Join-Path $outputFolder 'README.txt') -Force
Copy-OpenSourceNotices $outputFolder
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
