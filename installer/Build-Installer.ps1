param([string]$CompilerPath = '')
$ErrorActionPreference = 'Stop'
$snipProjectFolder = Split-Path $PSScriptRoot -Parent
$snipReleaseFolder = [IO.Path]::GetFullPath((Join-Path $snipProjectFolder 'release-0.5.7-signed'))
$snipTranslationFolder = Join-Path $snipReleaseFolder 'translation'
$snipOutputFolder = Join-Path $PSScriptRoot 'output'
$snipCompiler = $CompilerPath
if ([String]::IsNullOrWhiteSpace($snipCompiler)) {
    $snipNsisCommand = Get-Command makensis.exe -ErrorAction SilentlyContinue
    if ($null -ne $snipNsisCommand) { $snipCompiler = $snipNsisCommand.Source }
}
if ([String]::IsNullOrWhiteSpace($snipCompiler)) {
    $snipNsisCandidates = New-Object 'System.Collections.Generic.List[string]'
    if (-not [String]::IsNullOrWhiteSpace(${env:ProgramFiles(x86)})) { $snipNsisCandidates.Add((Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe')) }
    if (-not [String]::IsNullOrWhiteSpace($env:ProgramFiles)) { $snipNsisCandidates.Add((Join-Path $env:ProgramFiles 'NSIS\makensis.exe')) }
    if (-not [String]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) { $snipNsisCandidates.Add((Join-Path $env:LOCALAPPDATA 'electron-builder\Cache\nsis\nsis-3.0.4.1\Bin\makensis.exe')) }
    foreach ($snipCandidate in $snipNsisCandidates) { if (Test-Path -LiteralPath $snipCandidate -PathType Leaf) { $snipCompiler = $snipCandidate; break } }
}
if ([String]::IsNullOrWhiteSpace($snipCompiler) -or -not (Test-Path -LiteralPath $snipCompiler -PathType Leaf)) { throw 'NSIS was not found. Pass -CompilerPath with the path to makensis.exe.' }

function Get-SafeTranslationPath([string]$FullPath) {
    $prefix = $snipTranslationFolder.TrimEnd('\') + '\'
    $absolute = [IO.Path]::GetFullPath($FullPath)
    if (-not $absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Translation path is outside the release runtime.' }
    $relative = $absolute.Substring($prefix.Length).Replace('\','/')
    if ([String]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '[\x00-\x1F\x7F:]' -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) { throw 'Invalid translation payload path.' }
    return $relative
}
function ConvertTo-NsisLiteral([string]$Text) {
    if ($Text -match '[\x00-\x1F\x7F]') { throw 'Control characters cannot appear in installer paths.' }
    return $Text.Replace('$','$$').Replace('"','$\"')
}

# Packaging accepts an already verified official release, including the full
# runtime. Public source builds remain unsigned; no private signing is used.
if (-not (Test-Path -LiteralPath $snipTranslationFolder -PathType Container)) { throw 'Extract the complete official portable release into release-0.5.7-signed first.' }
$snipCursor = $snipTranslationFolder
while ($snipCursor.Length -gt $snipProjectFolder.Length) {
    if ((Get-Item -LiteralPath $snipCursor -Force).LinkType -in @('SymbolicLink','Junction')) { throw 'Release paths cannot be filesystem links.' }
    $snipCursor = [IO.Path]::GetDirectoryName($snipCursor)
}
if (@(Get-ChildItem -LiteralPath $snipTranslationFolder -Force -Recurse | Where-Object { $_.LinkType -in @('SymbolicLink','Junction') }).Count -ne 0) { throw 'Release contents cannot be filesystem links.' }
$snipReleaseBundlePath = Join-Path $snipTranslationFolder 'bundle.json'
$snipReviewedBundlePath = Join-Path $snipProjectFolder 'translation-bundle\runtime\bundle.json'
if ((Get-FileHash -LiteralPath $snipReleaseBundlePath -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $snipReviewedBundlePath -Algorithm SHA256).Hash) { throw 'The release bundle manifest differs from the reviewed source provenance.' }
$snipBundle = Get-Content -LiteralPath $snipReleaseBundlePath -Raw | ConvertFrom-Json
if ($snipBundle.version -ne 1 -or $snipBundle.licenseReview.complete -ne $true -or $snipBundle.models.Count -ne 6) { throw 'The reviewed translation manifest is required.' }
$snipRuntimeNames = New-Object 'System.Collections.Generic.List[string]'
foreach ($snipRuntimeEntry in @($snipBundle.files)) {
    $snipRuntimePath = Join-Path $snipTranslationFolder $snipRuntimeEntry.path
    $snipRuntimeRelative = Get-SafeTranslationPath $snipRuntimePath
    if ($snipRuntimeEntry.path -cne $snipRuntimeRelative -or $snipRuntimeNames -ccontains $snipRuntimeRelative -or $snipRuntimeEntry.sha256 -cnotmatch '^[0-9a-f]{64}$') { throw 'Invalid or duplicate runtime manifest entry.' }
    $snipRuntimeNames.Add($snipRuntimeRelative)
    if ((Get-FileHash -LiteralPath $snipRuntimePath -Algorithm SHA256).Hash -ine $snipRuntimeEntry.sha256) { throw 'Translation payload differs from its reviewed manifest.' }
}
$snipRuntimeActual = @(Get-ChildItem -LiteralPath $snipTranslationFolder -File -Recurse -Force | ForEach-Object { Get-SafeTranslationPath $_.FullName } | Where-Object { $_ -cne 'bundle.json' } | Sort-Object)
if (@(Compare-Object @($snipRuntimeNames | Sort-Object) $snipRuntimeActual -CaseSensitive).Count -ne 0) { throw 'The runtime manifest does not cover the complete translation payload.' }
foreach ($snipRequired in @('runtime/node.exe','worker.js')) { if ($snipRuntimeNames -cnotcontains $snipRequired) { throw 'A required translation runtime file is missing.' } }
foreach ($snipNotice in @($snipBundle.licenseReview.notices)) {
    if ($snipNotice -isnot [string] -or $snipRuntimeNames -cnotcontains $snipNotice) { throw 'A required translation license/source notice is missing.' }
}
$snipNodeSignature = Get-AuthenticodeSignature -LiteralPath (Join-Path $snipTranslationFolder 'runtime\node.exe')
if ($snipNodeSignature.Status -ne 'Valid' -or $null -eq $snipNodeSignature.SignerCertificate -or $snipNodeSignature.SignerCertificate.Subject -notmatch '(?i)OpenJS|Node\.js') { throw 'Node must retain its original valid publisher signature.' }

$snipPackageNames = @('Snip Studio.exe','Snip Studio.exe.config','README.txt','VALIDATION.txt','Signature Verification.json','SHA256SUMS.txt')
$snipTranslationFiles = @(Get-ChildItem -LiteralPath $snipTranslationFolder -File -Recurse -Force | ForEach-Object {
    [pscustomobject]@{Relative=(Get-SafeTranslationPath $_.FullName);FullName=$_.FullName;Length=$_.Length}
} | Sort-Object Relative)
$snipPackageNames += @($snipTranslationFiles | ForEach-Object { 'translation/' + $_.Relative })
$snipPackageBytes = 0L
foreach ($snipPackageName in $snipPackageNames) {
    $snipPackagePath = Join-Path $snipReleaseFolder $snipPackageName
    if (-not (Test-Path -LiteralPath $snipPackagePath -PathType Leaf)) { throw ('Missing release file: ' + $snipPackageName) }
    $snipPackageBytes += (Get-Item -LiteralPath $snipPackagePath).Length
}
# The portable manifest describes every installed payload file except itself.
$snipManifestExpected = @($snipPackageNames | Where-Object { $_ -cne 'SHA256SUMS.txt' } | Sort-Object)
$snipManifestNames = New-Object 'System.Collections.Generic.List[string]'
foreach ($snipLine in Get-Content -LiteralPath (Join-Path $snipReleaseFolder 'SHA256SUMS.txt')) {
    if ($snipLine -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw 'Invalid release hash-manifest line.' }
    $snipHash = $matches[1]
    $snipName = $matches[2]
    if ($snipManifestNames -ccontains $snipName -or $snipManifestExpected -cnotcontains $snipName) { throw 'Unexpected or duplicate release hash-manifest entry.' }
    $snipManifestNames.Add($snipName)
    if ((Get-FileHash -LiteralPath (Join-Path $snipReleaseFolder $snipName) -Algorithm SHA256).Hash -ine $snipHash) { throw ('Release file no longer matches its portable manifest: ' + $snipName) }
}
if (@(Compare-Object $snipManifestExpected @($snipManifestNames | Sort-Object) -CaseSensitive).Count -ne 0) { throw 'The release manifest does not cover the complete installer payload.' }

$snipAppPath = Join-Path $snipReleaseFolder 'Snip Studio.exe'
$snipSignature = Get-AuthenticodeSignature -LiteralPath $snipAppPath
if ($snipSignature.Status -ne 'Valid' -or $null -eq $snipSignature.TimeStamperCertificate -or $snipSignature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'Only a verified, timestamped Automation Forge application can be packaged.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($snipAppPath).FileVersion -ne '0.5.7.0') { throw 'Unexpected application version.' }
$snipPackageKb = [Math]::Max(1,[int][Math]::Ceiling($snipPackageBytes / 1024.0))
[IO.Directory]::CreateDirectory($snipOutputFolder) | Out-Null
$snipInstallInclude = Join-Path $snipOutputFolder 'translation-install.nsh'
$snipUninstallInclude = Join-Path $snipOutputFolder 'translation-uninstall.nsh'
$snipInstallLines = New-Object 'System.Collections.Generic.List[string]'
$snipUninstallLines = New-Object 'System.Collections.Generic.List[string]'
$snipDirectories = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($snipTranslationFile in $snipTranslationFiles) {
    $snipRelativeWindows = $snipTranslationFile.Relative.Replace('/','\')
    $snipRelativeDirectory = [IO.Path]::GetDirectoryName($snipRelativeWindows)
    $snipDestinationSuffix = if ([String]::IsNullOrEmpty($snipRelativeDirectory)) { '' } else { '\' + $snipRelativeDirectory }
    $snipInstallLines.Add('  SetOutPath "$INSTDIR\translation' + (ConvertTo-NsisLiteral $snipDestinationSuffix) + '"')
    $snipInstallLines.Add('  File "' + (ConvertTo-NsisLiteral $snipTranslationFile.FullName) + '"')
    $snipUninstallLines.Add('  Delete "$INSTDIR\translation\' + (ConvertTo-NsisLiteral $snipRelativeWindows) + '"')
    while (-not [String]::IsNullOrEmpty($snipRelativeDirectory)) {
        $snipDirectories.Add($snipRelativeDirectory) | Out-Null
        $snipRelativeDirectory = [IO.Path]::GetDirectoryName($snipRelativeDirectory)
    }
}
foreach ($snipDirectory in @($snipDirectories | Sort-Object @{Expression={($_ -split '\\').Count};Descending=$true},@{Expression={$_};Descending=$true})) {
    $snipUninstallLines.Add('  RMDir "$INSTDIR\translation\' + (ConvertTo-NsisLiteral $snipDirectory) + '"')
}
$snipUninstallLines.Add('  RMDir "$INSTDIR\translation"')
[IO.File]::WriteAllLines($snipInstallInclude,$snipInstallLines,[Text.UTF8Encoding]::new($true))
[IO.File]::WriteAllLines($snipUninstallInclude,$snipUninstallLines,[Text.UTF8Encoding]::new($true))

$snipPreviousNsisDir = $env:NSISDIR
$snipCompilerDirectory = Split-Path $snipCompiler -Parent
$env:NSISDIR = if ([IO.Path]::GetFileName($snipCompilerDirectory) -eq 'Bin') { Split-Path $snipCompilerDirectory -Parent } else { $snipCompilerDirectory }
Push-Location $PSScriptRoot
try {
    & $snipCompiler /V3 "/DRELEASE_DIR=$snipReleaseFolder" "/DOUTPUT_DIR=$snipOutputFolder" "/DPACKAGE_KB=$snipPackageKb" "/DTRANSLATION_INSTALL_INCLUDE=$snipInstallInclude" "/DTRANSLATION_UNINSTALL_INCLUDE=$snipUninstallInclude" (Join-Path $PSScriptRoot 'SnipStudio.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'NSIS installer build failed.' }
} finally { Pop-Location; $env:NSISDIR = $snipPreviousNsisDir }
$snipInstallerPath = Join-Path $snipOutputFolder 'SnipStudio-0.5.7-Setup.exe'
if (-not (Test-Path -LiteralPath $snipInstallerPath -PathType Leaf)) { throw 'The installer output was not created.' }
Write-Output ('Built ' + $snipInstallerPath + ' with ' + $snipTranslationFiles.Count + ' translation runtime/model/license files.')
Write-Output 'The installer has not been run. Sign the installer before distribution.'
