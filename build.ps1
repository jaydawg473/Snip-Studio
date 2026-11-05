param([string]$Destination = 'dist', [switch]$UseExistingIcons)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$metadataFolder = Join-Path $env:WINDIR 'System32\WinMetadata'
$references = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll', '/reference:System.Xml.dll', '/reference:System.Runtime.dll', '/reference:System.Runtime.WindowsRuntime.dll', '/reference:System.Runtime.InteropServices.WindowsRuntime.dll')
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
Write-Output "Built $appFile"
