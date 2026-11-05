param([string]$CompilerPath = '')
$ErrorActionPreference = 'Stop'
$snipProjectFolder = Split-Path $PSScriptRoot -Parent
$snipReleaseFolder = Join-Path $snipProjectFolder 'release-0.3.0-signed'
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
$snipPackageNames = @('Snip Studio.exe', 'Snip Studio.exe.config', 'README.txt', 'VALIDATION.txt', 'Signature Verification.json', 'SHA256SUMS.txt')
$snipPackageBytes = 0L
foreach ($snipPackageName in $snipPackageNames) {
    $snipPackagePath = Join-Path $snipReleaseFolder $snipPackageName
    if (-not (Test-Path -LiteralPath $snipPackagePath -PathType Leaf)) { throw ('Missing release file: ' + $snipPackageName) }
    $snipPackageBytes += (Get-Item -LiteralPath $snipPackagePath).Length
}
$snipAppPath = Join-Path $snipReleaseFolder 'Snip Studio.exe'
$snipSignature = Get-AuthenticodeSignature -LiteralPath $snipAppPath
if ($snipSignature.Status -ne 'Valid' -or $null -eq $snipSignature.TimeStamperCertificate -or $snipSignature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') {
    throw 'Only a verified, timestamped Automation Forge application can be packaged.'
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($snipAppPath).FileVersion -ne '0.3.0.0') { throw 'Unexpected application version.' }
$snipPackageKb = [Math]::Max(1, [int][Math]::Ceiling($snipPackageBytes / 1024.0))
[IO.Directory]::CreateDirectory($snipOutputFolder) | Out-Null
$snipPreviousNsisDir = $env:NSISDIR
$snipCompilerDirectory = Split-Path $snipCompiler -Parent
$env:NSISDIR = if ([IO.Path]::GetFileName($snipCompilerDirectory) -eq 'Bin') { Split-Path $snipCompilerDirectory -Parent } else { $snipCompilerDirectory }
Push-Location $PSScriptRoot
try {
    & $snipCompiler /V3 "/DRELEASE_DIR=$snipReleaseFolder" "/DOUTPUT_DIR=$snipOutputFolder" "/DPACKAGE_KB=$snipPackageKb" (Join-Path $PSScriptRoot 'SnipStudio.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'NSIS installer build failed.' }
} finally { Pop-Location; $env:NSISDIR = $snipPreviousNsisDir }
$snipInstallerPath = Join-Path $snipOutputFolder 'SnipStudio-0.3.0-Setup.exe'
if (-not (Test-Path -LiteralPath $snipInstallerPath -PathType Leaf)) { throw 'The installer output was not created.' }
Write-Output "Built $snipInstallerPath"
Write-Output 'The installer has not been run. Sign the installer before distribution.'
