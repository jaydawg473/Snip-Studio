param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$ReleaseVersion = '0.6.24',
    [string]$CompilerRoot = ''
)
$ErrorActionPreference = 'Stop'
$snipProjectFolder = Split-Path $PSScriptRoot -Parent
$snipReleaseFolder = [IO.Path]::GetFullPath((Join-Path $snipProjectFolder ('release-' + $ReleaseVersion + '-signed')))
$snipTranslationFolder = Join-Path $snipReleaseFolder 'translation'
$snipOutputFolder = Join-Path $PSScriptRoot 'output'
if ([String]::IsNullOrWhiteSpace($CompilerRoot)) { $CompilerRoot = Join-Path $snipProjectFolder 'tools\nsis-3.13' }
$snipCompilerRoot = [IO.Path]::GetFullPath($CompilerRoot).TrimEnd('\')
$snipCompiler = Join-Path $snipCompilerRoot 'Bin\makensis.exe'
$snipCompilerArchive = Join-Path (Split-Path $snipCompilerRoot -Parent) 'nsis-3.13.zip'
$snipUninstallerSigner = Join-Path $PSScriptRoot 'Sign-Uninstaller.ps1'
$snipSigningHost = (Get-Process -Id $PID).Path

function Assert-TimestampedSignature([string]$Path, [switch]$AutomationForge) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate -or
        ($AutomationForge -and $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*')) { throw ('A verified timestamped signature is required: ' + [IO.Path]::GetFileName($Path)) }
}

function Get-Sha256([byte[]]$Bytes) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','') }
    finally { $sha.Dispose() }
}

function Get-UnsignedImageHash([byte[]]$Original, [byte[]]$Current) {
    if ($Original.Length -lt 512 -or $Current.Length -lt $Original.Length -or
        $Original[0] -ne 0x4d -or $Original[1] -ne 0x5a) { throw 'Invalid original compiler PE image.' }
    $pe = [BitConverter]::ToInt32($Original,60)
    if ($pe -lt 64 -or $pe + 160 -ge $Original.Length) { throw 'Invalid compiler PE header.' }
    $optional = $pe + 24
    $magic = [BitConverter]::ToUInt16($Original,$optional)
    if ($magic -notin @(0x10b,0x20b)) { throw 'Unsupported compiler PE header.' }
    $certificateDirectory = $optional + $(if ($magic -eq 0x10b) { 128 } else { 144 })
    $certificateOffset = [BitConverter]::ToUInt32($Current,$certificateDirectory)
    $certificateBytes = [BitConverter]::ToUInt32($Current,$certificateDirectory+4)
    if ($certificateOffset -lt $Original.Length -or $certificateOffset -gt $Original.Length + 7 -or
        $certificateBytes -lt 8 -or [long]$certificateOffset + $certificateBytes -ne $Current.Length) { throw 'Unexpected changes around the compiler signature.' }
    for ($index=$Original.Length; $index -lt $certificateOffset; $index++) { if ($Current[$index] -ne 0) { throw 'Unexpected compiler signature padding.' } }
    $originalImage = [byte[]]$Original.Clone()
    $currentImage = New-Object byte[] $Original.Length
    [Array]::Copy($Current,$currentImage,$Original.Length)
    foreach ($image in @($originalImage,$currentImage)) {
        [Array]::Clear($image,$optional+64,4)
        [Array]::Clear($image,$certificateDirectory,8)
    }
    $hash = Get-Sha256 $originalImage
    if ((Get-Sha256 $currentImage) -cne $hash) { throw 'The signed compiler/plugin differs from its official original code or data.' }
    return $hash
}

function Assert-ReviewedCompiler {
    if (-not (Test-Path -LiteralPath $snipCompiler -PathType Leaf) -or
        -not (Test-Path -LiteralPath $snipCompilerArchive -PathType Leaf)) { throw 'The reviewed portable NSIS 3.13 compiler and original archive are required.' }
    if ((Get-FileHash -LiteralPath $snipCompilerArchive -Algorithm SHA256).Hash -cne 'BA63DFFC4410EE89193E1CB5A41989991BD77C61068DA17E3156D136B7B0B3D8') { throw 'The compiler archive differs from its published SourceForge SHA256.' }
    $signedNames = @('Bin/makensis.exe','Bin/zlib1.dll','Plugins/x86-unicode/System.dll','Plugins/x86-unicode/nsDialogs.dll')
    $actual = @{}
    foreach ($file in Get-ChildItem -LiteralPath $snipCompilerRoot -Recurse -File -Force) {
        if ($file.LinkType -in @('SymbolicLink','Junction')) { throw 'Linked compiler inputs are not allowed.' }
        $actual[$file.FullName.Substring($snipCompilerRoot.Length+1).Replace('\','/')] = $file.FullName
    }
    if (@(Get-ChildItem -LiteralPath $snipCompilerRoot -Recurse -Directory -Force | Where-Object { $_.LinkType -in @('SymbolicLink','Junction') }).Count) { throw 'Linked compiler directories are not allowed.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($snipCompilerArchive)
    try {
        $expected = @{}
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $entry.FullName.StartsWith('nsis-3.13/',[StringComparison]::Ordinal)) { throw 'Unexpected compiler archive root.' }
            $name = $entry.FullName.Substring(10)
            if ($expected.ContainsKey($name) -or -not $actual.ContainsKey($name)) { throw 'Compiler file inventory differs from the official archive.' }
            $expected[$name] = $true
            $memory = [IO.MemoryStream]::new()
            $stream = $entry.Open()
            try { $stream.CopyTo($memory); $original = $memory.ToArray() } finally { $stream.Dispose(); $memory.Dispose() }
            $current = [IO.File]::ReadAllBytes($actual[$name])
            if ($signedNames -ccontains $name) {
                Assert-TimestampedSignature $actual[$name]
                $null = Get-UnsignedImageHash $original $current
            } elseif ((Get-Sha256 $original) -cne (Get-Sha256 $current)) { throw ('An official compiler input changed: ' + $name) }
        }
        if ($expected.Count -ne $actual.Count) { throw 'Unexpected extra compiler inputs.' }
    } finally { $archive.Dispose() }
}

Assert-TimestampedSignature $snipSigningHost
Assert-TimestampedSignature $snipUninstallerSigner -AutomationForge
Assert-ReviewedCompiler
& (Join-Path $snipProjectFolder 'Prepare-PortableRelease.ps1') -Operation ValidateRuntime

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

$snipPackageNames = @('Snip Studio.exe','Snip Studio.exe.config','README.txt','VALIDATION.txt','Signature Verification.json','SHA256SUMS.txt')
$snipPackageNames += @('licenses/NOTICES.txt','licenses/FluentEmoji-LICENSE.txt','licenses/FluentEmoji-ATTRIBUTION.txt','licenses/FluentEmoji-manifest.json')
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
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($snipAppPath).FileVersion -ne ($ReleaseVersion + '.0')) { throw 'Unexpected application version.' }
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
$snipPreviousTemp = $env:TEMP
$snipPreviousTmp = $env:TMP
$snipPreviousUninstallerRelease = $env:SNIP_UNINSTALL_RELEASE_VERSION
$snipUninstallerTemp = Join-Path $snipOutputFolder 'uninstaller-signing-temp'
[IO.Directory]::CreateDirectory($snipUninstallerTemp) | Out-Null
$env:NSISDIR = $snipCompilerRoot
$env:TEMP = $snipUninstallerTemp
$env:TMP = $snipUninstallerTemp
$env:SNIP_UNINSTALL_RELEASE_VERSION = $ReleaseVersion
Push-Location $PSScriptRoot
try {
    & $snipCompiler /V3 "/DRELEASE_VERSION=$ReleaseVersion" "/DUNINSTALL_SIGN_HOST=$snipSigningHost" "/DUNINSTALL_SIGN_SCRIPT=$snipUninstallerSigner" "/DRELEASE_DIR=$snipReleaseFolder" "/DOUTPUT_DIR=$snipOutputFolder" "/DPACKAGE_KB=$snipPackageKb" "/DTRANSLATION_INSTALL_INCLUDE=$snipInstallInclude" "/DTRANSLATION_UNINSTALL_INCLUDE=$snipUninstallInclude" (Join-Path $PSScriptRoot 'SnipStudio.nsi')
    if ($LASTEXITCODE -ne 0) { throw 'NSIS installer build failed.' }
} finally {
    Pop-Location
    $env:NSISDIR = $snipPreviousNsisDir
    $env:TEMP = $snipPreviousTemp
    $env:TMP = $snipPreviousTmp
    $env:SNIP_UNINSTALL_RELEASE_VERSION = $snipPreviousUninstallerRelease
}
$snipInstallerPath = Join-Path $snipOutputFolder ('SnipStudio-' + $ReleaseVersion + '-Setup.exe')
if (-not (Test-Path -LiteralPath $snipInstallerPath -PathType Leaf)) { throw 'The installer output was not created.' }
$snipUninstallerPath = Join-Path $snipOutputFolder ('SnipStudio-' + $ReleaseVersion + '-Uninstall.exe')
Assert-TimestampedSignature $snipUninstallerPath -AutomationForge
$snipUninstallerReceipt = Get-Content -LiteralPath (Join-Path $snipOutputFolder ('Uninstaller-' + $ReleaseVersion + '-Signature.json')) -Raw | ConvertFrom-Json
if ($snipUninstallerReceipt.Version -cne $ReleaseVersion -or $snipUninstallerReceipt.SHA256 -cne (Get-FileHash -LiteralPath $snipUninstallerPath -Algorithm SHA256).Hash) { throw 'The generated signed uninstaller does not match its build receipt.' }
Write-Output ('Built ' + $snipInstallerPath + ' with ' + $snipTranslationFiles.Count + ' translation runtime/model/license files.')
Write-Output 'The signed uninstaller was verified before embedding. Neither installer nor uninstaller was run. Sign Setup before distribution.'

# SIG # Begin signature block
# MII2tQYJKoZIhvcNAQcCoII2pjCCNqICAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCCHbI3bI8vQAEVP
# cYDSK/4opSdW6l67nO19InAnnUqzP6CCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
# lUgTecgRwIeZMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jvc29mdCBJZGVu
# dGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAy
# MDAeFw0yMDA0MTYxODM2MTZaFw00NTA0MTYxODQ0NDBaMHcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jv
# c29mdCBJZGVudGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRo
# b3JpdHkgMjAyMDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALORKgeD
# Bmf9np3gx8C3pOZCBH8Ppttf+9Va10Wg+3cL8IDzpm1aTXlT2KCGhFdFIMeiVPvH
# or+Kx24186IVxC9O40qFlkkN/76Z2BT2vCcH7kKbK/ULkgbk/WkTZaiRcvKYhOuD
# PQ7k13ESSCHLDe32R0m3m/nJxxe2hE//uKya13NnSYXjhr03QNAlhtTetcJtYmrV
# qXi8LW9J+eVsFBT9FMfTZRY33stuvF4pjf1imxUs1gXmuYkyM6Nix9fWUmcIxC70
# ViueC4fM7Ke0pqrrBc0ZV6U6CwQnHJFnni1iLS8evtrAIMsEGcoz+4m+mOJyoHI1
# vnnhnINv5G0Xb5DzPQCGdTiO0OBJmrvb0/gwytVXiGhNctO/bX9x2P29Da6SZEi3
# W295JrXNm5UhhNHvDzI9e1eM80UHTHzgXhgONXaLbZ7LNnSrBfjgc10yVpRnlyUK
# xjU9lJfnwUSLgP3B+PR0GeUw9gb7IVc+BhyLaxWGJ0l7gpPKWeh1R+g/OPTHU3mg
# trTiXFHvvV84wRPmeAyVWi7FQFkozA8kwOy6CXcjmTimthzax7ogttc32H83rwjj
# O3HbbnMbfZlysOSGM1l0tRYAe1BtxoYT2v3EOYI9JACaYNq6lMAFUSw0rFCZE4e7
# swWAsk0wAly4JoNdtGNz764jlU9gKL431VulAgMBAAGjVDBSMA4GA1UdDwEB/wQE
# AwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTIftJqhSobyhmYBAcnz1AQ
# T2ioojAQBgkrBgEEAYI3FQEEAwIBADANBgkqhkiG9w0BAQwFAAOCAgEAr2rd5hnn
# LZRDGU7L6VCVZKUDkQKL4jaAOxWiUsIWGbZqWl10QzD0m/9gdAmxIR6QFm3FJI9c
# Zohj9E/MffISTEAQiwGf2qnIrvKVG8+dBetJPnSgaFvlVixlHIJ+U9pW2UYXeZJF
# xBA2CFIpF8svpvJ+1Gkkih6PsHMNzBxKq7Kq7aeRYwFkIqgyuH4yKLNncy2RtNwx
# AQv3Rwqm8ddK7VZgxCwIo3tAsLx0J1KH1r6I3TeKiW5niB31yV2g/rarOoDXGpc8
# FzYiQR6sTdWD5jw4vU8w6VSp07YEwzJ2YbuwGMUrGLPAgNW3lbBeUU0i/OxYqujY
# lLSlLu2S3ucYfCFX3VVj979tzR/SpncocMfiWzpbCNJbTsgAlrPhgzavhgplXHT2
# 6ux6anSg8Evu75SjrFDyh+3XOjCDyft9V77l4/hByuVkrrOj7FjshZrM77nq81YY
# uVxzmq/FdxeDWds3GhhyVKVB0rYjdaNDmuV3fJZ5t0GNv+zcgKCf0Xd1WF81E+Al
# GmcLfc4l+gcK5GEh2NQc5QfGNpn0ltDGFf5Ozdeui53bFv0ExpK91IjmqaOqu/dk
# ODtfzAzQNb50GQOmxapMomE2gj4d8yu8l13bS3g7LfU772Aj6PXsCyM2la+YZr9T
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggaxMIIEmaADAgECAhMzAAxhlyl6
# eE8Q6l8PAAAADGGXMA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBBT0MgQ0EgMDQwHhcNMjYxMTE3MDk1MDE4WhcNMjYxMTIw
# MDk1MDE4WjB0MQswCQYDVQQGEwJVUzERMA8GA1UECBMITmV3IFlvcmsxFDASBgNV
# BAcTC1NwZW5jZXJwb3J0MR0wGwYDVQQKExRBdXRvbWF0aW9uIEZvcmdlIExMQzEd
# MBsGA1UEAxMUQXV0b21hdGlvbiBGb3JnZSBMTEMwggGiMA0GCSqGSIb3DQEBAQUA
# A4IBjwAwggGKAoIBgQCOW/q1Szb+TCEemrAABKnvjy7UAtBiRDW6XLVrhkV7CBQH
# tt5bs5BjlgOYccWI9Qno1HiAAow3aZm+wjHHesRHK87LJ92KqhBpVuyHTBO+9h6Z
# zwwx9HSbfC5r4Mw90CHC7eiNEI7PO+lU2uLuLYoe8a2q9kLPJ0LkqeY361HDh3j2
# /NxS7xx08rcux2PZrKMEeFo6E0ZaSQOifK4UxIpZOSF0KDY6F/By4wVfDdVaKIWg
# ecV8Y3b3bWzwM64DC+8gOgXMSiBBprew4d/LZTxbhkPDuDc7hQy+m/AqCW72lKXQ
# hr+0szgNbmOXaSyq+nld5H5HBrKdCrBDJpsHgv7iFm06VemFxr0PdEMEiYQV9VHM
# mVeF0aCEzEViIpVCh+uxkuPCOAvfc4VPmKKuxrNe+ejDYlrQKlLqQhwlg+uUI1E7
# gPhDEansl8hwI97zN9nf+vJu4xuGSW4uXGwpuUTx6tZMQMEv5a+OyQixuE1F3wW9
# hZlqxW0PU6kMFVMJbgcCAwEAAaOCAdQwggHQMAwGA1UdEwEB/wQCMAAwDgYDVR0P
# AQH/BAQDAgeAMDsGA1UdJQQ0MDIGCisGAQQBgjdhAQAGCCsGAQUFBwMDBhorBgEE
# AYI3YYHZ++pilcS8boyU8DKCgaGqKjAdBgNVHQ4EFgQUjuK8UcF6MLnHSXikZLNX
# LxZ80o4wHwYDVR0jBBgwFoAUayVB3vtrfP0YgAotf492XapzPbgwZwYDVR0fBGAw
# XjBcoFqgWIZWaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWlj
# cm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENTJTIwQU9DJTIwQ0ElMjAwNC5jcmww
# dAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUFBzAChlhodHRwOi8vd3d3Lm1pY3Jvc29m
# dC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElEJTIwVmVyaWZpZWQlMjBD
# UyUyMEFPQyUyMENBJTIwMDQuY3J0MFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsG
# AQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVw
# b3NpdG9yeS5odG0wDQYJKoZIhvcNAQEMBQADggIBAKeN+p5uHUCbUwD371G3ZZoP
# XcJ0NPLeT/s+Qd+2ctgF20DCsjGmfO7U5Am8fK2yWwfz6xdwbSIo6AHkCPxJtI8e
# i5k+UU54lmlYtBXbrBh8EhFPFqFYq6GT1ExxG7GdCBBFkvpprSG8jxEvIMDukoON
# t7Z8ODChqrTxoVjwcwcXIyJ1BMOS5M98+MA8ARRNsHnT2cV2Erbvu222d7Gh30T6
# 8wZuSX51DUHU+g5QYbKS99mKOax5IxNOIBmXW0VSggs8yc+nKlfBcrZIVAWAZ8VC
# 7tPI5vRLO4wpIrDjYm+INL82XXSYR9obYDsB2aVya2szql+oaguxZISw/yudfkHN
# U6TLV1B7SUJwbo0kpb8I+RpSuok8tnStPLj99J4nFpJHIRH4QKhVeuUYnZkn3YLT
# CfGLbfx6jJtjV4AQePY3pLBnmJ1BHfuXfawEqoeRldNR1j4NGsK2P6t4mk5t/++y
# ICj7fZFKqLA5bZ1qgpoNT2OGYit7XaacKyOlItyc8mRMAq743t1v926Hi6weoHUx
# g2LBF9dky2R+XlMeJvmIJaw3md1j0iTpBQuJRraE7d75eGKKECLiLoy55zMcWW7a
# iWChUi/XSvJlu0iR541/hKkjbKwlP90EnfWHS5wX1IfXXM6XwVHNLnvPccU6FxwK
# f4x/MJHxK6NbIrKgz8HEMIIHKDCCBRCgAwIBAgITMwAAABYxko2SAmV7mgAAAAAA
# FjANBgkqhkiG9w0BAQwFADBjMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9z
# b2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytNaWNyb3NvZnQgSUQgVmVyaWZpZWQg
# Q29kZSBTaWduaW5nIFBDQSAyMDIxMB4XDTI2MDMyNjE4MTEyOVoXDTMxMDMyNjE4
# MTEyOVowWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3Jh
# dGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAw
# NDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAMpV+sjb6Akwz/RtDk5U
# o1284BLMttRQy9e5/5WXtga6h89pkdWjeAwcXmKdP4YxKkhx8hn2q1dTVQbryvLy
# 81tC04vg2bfSJ9emojX4HKIBYs7VhPRafMbtc866hN55aw1m/kWaPEKxF4Fm/LPL
# MLJdlu7URB8nFZMfh5tTC0CJb2uox/14OP/BiGIR8214lXdkV6JsPbO0Iev0mEV1
# 33tducIeBChMipzTZfnGVEq1QYFr7460cEGOIn+7AGfNOSq7gWOlmNB4m2uZ1r66
# vUJPaN+VYgH/Kmfu7tX229b3Alsli38fYS0nQY41bElntMS7yNY+Kd047eXM8/tS
# 3NL+ZUNX3ge5xZqW2aytrrNIbwGgQnzsgzxBJvu9+b87jUWFiRS/z1YiPkRLY7iT
# HKIxJ973kIyK8K5itE/aEq/Ht6A8ytaAMMGTEwuCspk72FE1Qyby+TfDlv1KiAc7
# IlWHHIWbxoVd8jGCoMXhLSDhuFuGfOWOZKUIuW6YxlxcUYOOkXa02gC7dUYUZi0e
# 1NI1Uq9mmAHdvjKdqgORs9/5aDjnbeO3hWd5qwGBmELWytkjY0KcIEF+CMurTimo
# QoBYSiHJbYrb0pKSQe+3lVoTVpzdi36jKI7MxLnu1fBAMksa8uD85cU7RU29zMOd
# 18h9dgTEH9pvY+4xLB3lUgE7AgMBAAGjggHcMIIB2DAOBgNVHQ8BAf8EBAMCAYYw
# EAYJKwYBBAGCNxUBBAMCAQAwHQYDVR0OBBYEFGslQd77a3z9GIAKLX+Pdl2qcz24
# MFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1p
# Y3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wGQYJKwYBBAGC
# NxQCBAweCgBTAHUAYgBDAEEwEgYDVR0TAQH/BAgwBgEB/wIBADAfBgNVHSMEGDAW
# gBTZQSmwDw9jbO9p1/XNKZ6kSGow5jBwBgNVHR8EaTBnMGWgY6Bhhl9odHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJRCUyMFZl
# cmlmaWVkJTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDIxLmNybDB9BggrBgEF
# BQcBAQRxMG8wbQYIKwYBBQUHMAKGYWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9w
# a2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENvZGUlMjBT
# aWduaW5nJTIwUENBJTIwMjAyMS5jcnQwDQYJKoZIhvcNAQEMBQADggIBAAbVUF5U
# dNVEGWOVxkPciIzHA/IyNDIs1oSdW7mY4ObaFEEl8fwawEsBOascBzwXjuOaTkKe
# lQ+IiC4JvDpn8pWPgOyiKrbbw1Iswt7AV8YyuLu7A0YshILlxyny9R0AMMQixLZ0
# T5Aj07CQOm/de5QPt36ECBwtVww6XUCx8Rm2xl6eEa9JhSub8W4urQGoQYv0Zczl
# z4ej2ryj5Wf9L4ZZA3bL6CRE7XmzSQjTmdSmr904PiuL2uBWzq2KkR3RhmoaAP4J
# k2JplasNM5Bs0+dx3YX2o6xRrbSaJiu6hPk/AkBoj5BCJMTZkl4wk6Q6nOFNSCpU
# xnBmJ0RRkoKq51p5ADTxbCeRAx8rIfNpTyPjxQtxTiFTC8yy/t9K6s570YB1FAI+
# 8XxZxIrmgMd7xVUkzi2/oooKb3UeovH0lqYGBEjpHZJ9jee7boQbOe7SsKD/vr3P
# zFabg9VwLZlovpWUpWoWnU2w3xiozJ/m35tsZVvT2egUpwkb9TDIaXFGZAGV94FR
# DHn/K2XNnOwSeGskX19MB+N7yPc51fmUSd49MVAtGW/NkUGpRech5Aq/d8dCML2b
# Z+j9nTUMgj+qnd3wuEZVRbQEIbTh9HAck0fyKqoIb+qh8y/6TKbYDEEOcfM2BGMv
# rQk4WIGCK6u8uFhA2EH3kpaiMUDqyFCeCCQxMIIHnjCCBYagAwIBAgITMwAAAAeH
# ozSje6WOHAAAAAAABzANBgkqhkiG9w0BAQwFADB3MQswCQYDVQQGEwJVUzEeMBwG
# A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9NaWNyb3NvZnQg
# SWRlbnRpdHkgVmVyaWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5
# IDIwMjAwHhcNMjEwNDAxMjAwNTIwWhcNMzYwNDAxMjAxNTIwWjBjMQswCQYDVQQG
# EwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytN
# aWNyb3NvZnQgSUQgVmVyaWZpZWQgQ29kZSBTaWduaW5nIFBDQSAyMDIxMIICIjAN
# BgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAsvDArxmIKOLdVHpMSWxpCFUJtFL/
# ekr4weslKPdnF3cpTeuV8veqtmKVgok2rO0D05BpyvUDCg1wdsoEtuxACEGcgHfj
# PF/nZsOkg7c0mV8hpMT/GvB4uhDvWXMIeQPsDgCzUGzTvoi76YDpxDOxhgf8JuXW
# JzBDoLrmtThX01CE1TCCvH2sZD/+Hz3RDwl2MsvDSdX5rJDYVuR3bjaj2QfzZFmw
# fccTKqMAHlrz4B7ac8g9zyxlTpkTuJGtFnLBGasoOnn5NyYlf0xF9/bjVRo4Gzg2
# Yc7KR7yhTVNiuTGH5h4eB9ajm1OCShIyhrKqgOkc4smz6obxO+HxKeJ9bYmPf6KL
# XVNLz8UaeARo0BatvJ82sLr2gqlFBdj1sYfqOf00Qm/3B4XGFPDK/H04kteZEZsB
# Rc3VT2d/iVd7OTLpSH9yCORV3oIZQB/Qr4nD4YT/lWkhVtw2v2s0TnRJubL/hFMI
# Qa86rcaGMhNsJrhysLNNMeBhiMezU1s5zpusf54qlYu2v5sZ5zL0KvBDLHtL8F9g
# n6jOy3v7Jm0bbBHjrW5yQW7S36ALAt03QDpwW1JG1Hxu/FUXJbBO2AwwVG4Fre+Z
# Q5Od8ouwt59FpBxVOBGfN4vN2m3fZx1gqn52GvaiBz6ozorgIEjn+PhUXILhAV5Q
# /ZgCJ0u2+ldFGjcCAwEAAaOCAjUwggIxMA4GA1UdDwEB/wQEAwIBhjAQBgkrBgEE
# AYI3FQEEAwIBADAdBgNVHQ4EFgQU2UEpsA8PY2zvadf1zSmepEhqMOYwVAYDVR0g
# BE0wSzBJBgRVHSAAMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0
# LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTAZBgkrBgEEAYI3FAIEDB4K
# AFMAdQBiAEMAQTAPBgNVHRMBAf8EBTADAQH/MB8GA1UdIwQYMBaAFMh+0mqFKhvK
# GZgEByfPUBBPaKiiMIGEBgNVHR8EfTB7MHmgd6B1hnNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJZGVudGl0eSUyMFZlcmlm
# aWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1dGhvcml0eSUyMDIwMjAu
# Y3JsMIHDBggrBgEFBQcBAQSBtjCBszCBgQYIKwYBBQUHMAKGdWh0dHA6Ly93d3cu
# bWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSWRlbnRpdHkl
# MjBWZXJpZmljYXRpb24lMjBSb290JTIwQ2VydGlmaWNhdGUlMjBBdXRob3JpdHkl
# MjAyMDIwLmNydDAtBggrBgEFBQcwAYYhaHR0cDovL29uZW9jc3AubWljcm9zb2Z0
# LmNvbS9vY3NwMA0GCSqGSIb3DQEBDAUAA4ICAQB/JSqe/tSr6t1mCttXI0y6XmyQ
# 41uGWzl9xw+WYhvOL47BV09Dgfnm/tU4ieeZ7NAR5bguorTCNr58HOcA1tcsHQqt
# 0wJsdClsu8bpQD9e/al+lUgTUJEV80Xhco7xdgRrehbyhUf4pkeAhBEjABvIUpD2
# LKPho5Z4DPCT5/0TlK02nlPwUbv9URREhVYCtsDM+31OFU3fDV8BmQXv5hT2RurV
# sJHZgP4y26dJDVF+3pcbtvh7R6NEDuYHYihfmE2HdQRq5jRvLE1Eb59PYwISFCX2
# DaLZ+zpU4bX0I16ntKq4poGOFaaKtjIA1vRElItaOKcwtc04CBrXSfyL2Op6mvNI
# xTk4OaswIkTXbFL81ZKGD+24uMCwo/pLNhn7VHLfnxlMVzHQVL+bHa9KhTyzwdG/
# L6uderJQn0cGpLQMStUuNDArxW2wF16QGZ1NtBWgKA8Kqv48M8HfFqNifN6+zt6J
# 0GwzvU8g0rYGgTZR8zDEIJfeZxwWDHpSxB5FJ1VVU1LIAtB7o9PXbjXzGifaIMYT
# zU4YKt4vMNwwBmetQDHhdAtTPplOXrnI9SI6HeTtjDD3iUN/7ygbahmYOHk7VB7f
# wT4ze+ErCbMh6gHV1UuXPiLciloNxH6K4aMfZN1oLVk6YFeIJEokuPgNPa6EnTiO
# L60cPqfny+Fq8UiuZzGCGrgwghq0AgEBMHEwWjELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElE
# IFZlcmlmaWVkIENTIEFPQyBDQSAwNAITMwAMYZcpenhPEOpfDwAAAAxhlzANBglg
# hkgBZQMEAgEFAKCBhDAYBgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEV
# MC8GCSqGSIb3DQEJBDEiBCDKJRzyssV+kBMuvJIqB+tDmuJI8YQRA3KwU2oQnvte
# kTANBgkqhkiG9w0BAQEFAASCAYAe8RBVIKjeSAbLkHcbDfxUlELMvvS+24w6tdWN
# LX52ND8/Z25aQaXQMLg+mAHMS872I6AvUO1XqRtLJ50TEt5KDcXnZDm/CN7iSEzn
# XI+UCwJ6KzZPE1AJcfvvtC8VCOINpx/GkAngT+tYYRXjXyWkdVbTnbDcXpu6P3D7
# pZjCueG6Upiyjt1DQeuk3azlr+FDxC7C2WYrELG1O2vEVJ/ZJnnbQnlpo9oGlGH0
# tSh/FVG6q1eNcfXSQcKN97+i/uwdRuAL0oWNm57s3alhHgXslAZ2uDtQIW/eNsi/
# IZNlILrMtjJRdpIYAsFfPRjcxqScgboeQ44B2IaasUmwce+vCgYE5skb2B0A/yVX
# 5/NyzYBpk49FKXS3MPh89HDxjdaN7qK9HZRKprtuiVjmXkPxsx/yb6CmmVTbvjWE
# rGYXadqXTd7janm3jdCAbE/CXJ2xWuRlMmunecpJNH/PLaGIo3waO7CERwdQPbU8
# fT5DvysDEIFA9qOOVnNcvn+NMHShghgRMIIYDQYKKwYBBAGCNwMDATGCF/0wghf5
# BgkqhkiG9w0BBwKgghfqMIIX5gIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgTp7qdqrspvAaGtrc0v/Puszi9RGIvLomnYRAlPchGZECBmr86+Y0chgT
# MjAyNjExMTgyMjExMDMuOTE2WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
# EzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoT
# FU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJp
# Y2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjc4MDAtMDVF
# MC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1lIFN0YW1w
# aW5nIEF1dGhvcml0eaCCDyEwggeCMIIFaqADAgECAhMzAAAABeXPD/9mLsmHAAAA
# AAAFMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNy
# b3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jvc29mdCBJZGVudGl0eSBW
# ZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAyMDAeFw0y
# MDExMTkyMDMyMzFaFw0zNTExMTkyMDQyMzFaMGExCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQ
# dWJsaWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMIICIjANBgkqhkiG9w0BAQEF
# AAOCAg8AMIICCgKCAgEAnnznUmP94MWfBX1jtQYioxwe1+eXM9ETBb1lRkd3kcFd
# cG9/sqtDlwxKoVIcaqDb+omFio5DHC4RBcbyQHjXCwMk/l3TOYtgoBjxnG/eViS4
# sOx8y4gSq8Zg49REAf5huXhIkQRKe3Qxs8Sgp02KHAznEa/Ssah8nWo5hJM1xznk
# RsFPu6rfDHeZeG1Wa1wISvlkpOQooTULFm809Z0ZYlQ8Lp7i5F9YciFlyAKwn6yj
# N/kR4fkquUWfGmMopNq/B8U/pdoZkZZQbxNlqJOiBGgCWpx69uKqKhTPVi3gVErn
# c/qi+dR8A2MiAz0kN0nh7SqINGbmw5OIRC0EsZ31WF3Uxp3GgZwetEKxLms73KG/
# Z+MkeuaVDQQheangOEMGJ4pQZH55ngI0Tdy1bi69INBV5Kn2HVJo9XxRYR/JPGAa
# M6xGl57Ei95HUw9NV/uC3yFjrhc087qLJQawSC3xzY/EXzsT4I7sDbxOmM2rl4uK
# K6eEpurRduOQ2hTkmG1hSuWYBunFGNv21Kt4N20AKmbeuSnGnsBCd2cjRKG79+TX
# +sTehawOoxfeOO/jR7wo3liwkGdzPJYHgnJ54UxbckF914AqHOiEV7xTnD1a69w/
# UTxwjEugpIPMIIE67SFZ2PMo27xjlLAHWW3l1CEAFjLNHd3EQ79PUr8FUXetXr0C
# AwEAAaOCAhswggIXMA4GA1UdDwEB/wQEAwIBhjAQBgkrBgEEAYI3FQEEAwIBADAd
# BgNVHQ4EFgQUa2koOjUvSGNAz3vYr0npPtk92yEwVAYDVR0gBE0wSzBJBgRVHSAA
# MEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMv
# RG9jcy9SZXBvc2l0b3J5Lmh0bTATBgNVHSUEDDAKBggrBgEFBQcDCDAZBgkrBgEE
# AYI3FAIEDB4KAFMAdQBiAEMAQTAPBgNVHRMBAf8EBTADAQH/MB8GA1UdIwQYMBaA
# FMh+0mqFKhvKGZgEByfPUBBPaKiiMIGEBgNVHR8EfTB7MHmgd6B1hnNodHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJZGVudGl0
# eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1dGhvcml0
# eSUyMDIwMjAuY3JsMIGUBggrBgEFBQcBAQSBhzCBhDCBgQYIKwYBBQUHMAKGdWh0
# dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIw
# SWRlbnRpdHklMjBWZXJpZmljYXRpb24lMjBSb290JTIwQ2VydGlmaWNhdGUlMjBB
# dXRob3JpdHklMjAyMDIwLmNydDANBgkqhkiG9w0BAQwFAAOCAgEAX4h2x35ttVoV
# dedMeGj6TuHYRJklFaW4sTQ5r+k77iB79cSLNe+GzRjv4pVjJviceW6AF6ycWoEY
# R0LYhaa0ozJLU5Yi+LCmcrdovkl53DNt4EXs87KDogYb9eGEndSpZ5ZM74LNvVzY
# 0/nPISHz0Xva71QjD4h+8z2XMOZzY7YQ0Psw+etyNZ1CesufU211rLslLKsO8F2a
# Bs2cIo1k+aHOhrw9xw6JCWONNboZ497mwYW5EfN0W3zL5s3ad4Xtm7yFM7Ujrhc0
# aqy3xL7D5FR2J7x9cLWMq7eb0oYioXhqV2tgFqbKHeDick+P8tHYIFovIP7YG4Zk
# JWag1H91KlELGWi3SLv10o4KGag42pswjybTi4toQcC/irAodDW8HNtX+cbz0sMp
# tFJK+KObAnDFHEsukxD+7jFfEV9Hh/+CSxKRsmnuiovCWIOb+H7DRon9TlxydiFh
# vu88o0w35JkNbJxTk4MhF/KgaXn0GxdH8elEa2Imq45gaa8D+mTm8LWVydt4ytxY
# P/bqjN49D9NZ81coE6aQWm88TwIf4R4YZbOpMKN0CyejaPNN41LGXHeCUMYmBx3P
# kP8ADHD1J2Cr/6tjuOOCztfp+o9Nc+ZoIAkpUcA/X2gSMkgHAPUvIdtoSAHEUKiB
# hI6JQivRepyvWcl+JYbYbBh7pmgAXVswggeXMIIFf6ADAgECAhMzAAAAXi4Rbhx2
# nO8FAAAAAABeMA0GCSqGSIb3DQEBDAUAMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJs
# aWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMB4XDTI2MDkxNzE4NTcyM1oXDTI3
# MDkxNDE4NTcyM1owgdsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9u
# MRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRp
# b24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
# BAsTHm5TaGllbGQgVFNTIEVTTjo3ODAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWlj
# cm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGluZyBBdXRob3JpdHkwggIiMA0G
# CSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDWC85/kYk63q2CIIiaxqXSqxM2llnF
# 09nQGBuhN9WkZfhyJF4/WWs6oa/dHpeIqAerFySnamqc1Enr96Z8g1feJ3GK5660
# 5hExroZ7LB/5eEh+4Nss4G9aK9UDAmCper17GOmjg+eNoXRKsBoq74QJ/mQv+LSH
# mk5YhAgcIjdclV/zflkKGSYu+4C8hDg5qUAdoVisoXw82aqZzgt+voxjS3UFwoK1
# OS5UiUo/pUyMnx3a+5VmchPcVpMslCLNwAELU4BvdlHww9AAINh+BQ65X7lXv3oM
# w7vJKkvg5Wgx1VwfuYY/ktT5xjhvQ5cFSVZFFAPDoXUO4uMUiSvCA62dSrAuZV49
# +Z31fJWvck0CvHZhQ5WGDnXBnQomaXV+yex8WSGWqj+HHY0J/O87/afsh+W6J4xV
# OaUCIifWYUtnRGB2DD1iScrvM915Yi/w3QYGYrx3jKfTleinMycB9K3vFdV6OTZq
# 1xd0a1UGLQigkc2v9onrkbdD2JJaGjGp8zBumtHfWocYQ7YhNdWkIV65Zf3WmZh4
# QYnknDPe3mE+HvpNCt6wFfeh31OrFbRl4qeS7yd3O/jwQzJCWH0Jf8v6WjOZ4Vvc
# EhCAE/IGHRj0ais6JWHWpvRr7LfF5h15EG5erlGzAscAN1NgnjfbyIeSr2pfu3QW
# bEOOi7OI4aZAZQIDAQABo4IByzCCAccwHQYDVR0OBBYEFJCfjv3tXJBWSFsq+/ra
# sBXrKUbuMB8GA1UdIwQYMBaAFGtpKDo1L0hjQM972K9J6T7ZPdshMGwGA1UdHwRl
# MGMwYaBfoF2GW2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01p
# Y3Jvc29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAy
# MC5jcmwweQYIKwYBBQUHAQEEbTBrMGkGCCsGAQUFBzAChl1odHRwOi8vd3d3Lm1p
# Y3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMFB1YmxpYyUyMFJT
# QSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAyMC5jcnQwDAYDVR0TAQH/BAIwADAW
# BgNVHSUBAf8EDDAKBggrBgEFBQcDCDAOBgNVHQ8BAf8EBAMCB4AwZgYDVR0gBF8w
# XTBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMAgGBmeBDAEEAjAN
# BgkqhkiG9w0BAQwFAAOCAgEARbv2U+HL4+JTNop2W4hv5mcsJZD6HQCgJN6H6f7g
# XvwM+oJggCuUt63qTY7R7fnCaRhk9VgyCZKwtB4c3IE1EcQVoh7sC63QK8D5SKkI
# BxjVdCrumNVI7KrlwTLk+X01Wr6cKoQzoA3TQ6cYm77DeCLmOgEtgYbSUKOcttzo
# rOkVM83clGxHQ6pfEPLhvio5Dx1/ZgYHXLMx9hgo7zuRjjRPkqVRQ1Av0Iko9C2j
# 3dZhDwQWEj6Goersb3L03AX/sGOLNm7tuFsmhfgL3Jt8D+wiN8JIGG6VZiDTOwR9
# HO6DkzJZv+VmVzX5XoW4Dw+J9YOpTXkyLXxRFX+4+2lv5HqMxu86AbzvUdxFmOmy
# WfnZqZMt/9TnbpxELGnkx7KODDTUNDg1gTXih3fDxFhHooTeFsbnzk5bvSeZblEP
# BztRZlXQpS9O/ummA1mfkTk7LIM7+EDXoNyd8a9UnUezqLt5egghS+AB+ok5IzdR
# cEfauuyRX5qud4mohFiewaN1wqaEzPlmkDzI4sSQOF8dfhurtOhHqUzYumSY4tzl
# vSgdqEGXVqKmk3r2UaejqMg29sT4ULSkfSA67bbAx+ltBNJVu59bIobL0QCMN88r
# +n8PA+xsZpD9FVrZEgss1wW2kdEWFxxIFIJ1WMF1MAGvmHLnV8cCfteiZa55wu70
# gwMxggdDMIIHPwIBATB4MGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAXi4Rbhx2nO8FAAAAAABeMA0GCWCGSAFl
# AwQCAQUAoIIEnDARBgsqhkiG9w0BCRACDzECBQAwGgYJKoZIhvcNAQkDMQ0GCyqG
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMjExMDNaMC8GCSqGSIb3
# DQEJBDEiBCBZ0fvKq27nHLmYBNEgPbQ4ccb1KxdDHxt1AO+jZr+zATCBuQYLKoZI
# hvcNAQkQAi8xgakwgaYwgaMwgaAEINn1Zsw7h0YMrZl0eW1S+zMIdAhqqqLPt5A2
# 5l5QV+6jMHwwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVz
# dGFtcGluZyBDQSAyMDIwAhMzAAAAXi4Rbhx2nO8FAAAAAABeMIIDXgYLKoZIhvcN
# AQkQAhIxggNNMIIDSaGCA0UwggNBMIICKQIBATCCAQmhgeGkgd4wgdsxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29m
# dCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3
# ODAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZSBTdGFtcGluZyBBdXRob3JpdHmiIwoBATAHBgUrDgMCGgMVAN6uHdmwPHxw2I12
# Pwvmrjbx/+gFoGcwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwMA0GCSqGSIb3DQEBCwUAAgUA7qgTJjAiGA8yMDI2
# MTExODEyMDU1OFoYDzIwMjYxMTE5MTIwNTU4WjB0MDoGCisGAQQBhFkKBAExLDAq
# MAoCBQDuqBMmAgEAMAcCAQACAhzmMAcCAQACAhN5MAoCBQDuqWSmAgEAMDYGCisG
# AQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEAAgMB
# hqAwDQYJKoZIhvcNAQELBQADggEBAIGKPVSyV5McM95jzeXUOE537XWrSNHUmhZB
# XhSIs5NJeR4nnosjf/eUZQNNXzvzWGV/nZlkMxgJVTOV5TrPuiQ4mWCutiTgbeHo
# LZqWKiJJexoca5ovbbekQf7HfZKq7DAuwekCalfPJito5KHnXDB+gAU5YJCdMrD0
# NCgVAMt/h+F+lxTMpjCGjGD2UbGlQj4CCqwQU3AVpFZuvKX3xpLzAW+K5W0FJ7/d
# JcqV8853TBIqYnbcnU5SpihEjSvrRNXM/XYAg+HAQPh9eRBBjHbFlmVW8BP/yQDj
# pUaLKJ3eki3LNG7GRGzeoFz0NcXyYJF2HTlWUcvTkTbtczy8b+wwDQYJKoZIhvcN
# AQEBBQAEggIAw2TPV6Inkttrh14z0s5KQRatF+8IUwNufCOBF/pVEiL3taDnvQi6
# wV/9+t3UrIyw51ckog7j0acbZvOXw7bC4GDBlofWDV5Pm+DmALFmCY+piBiB/nG2
# ieMewe3VqIW0ReuiiCQrjHxm8PU/vfADBQhHNF5nj4fzZ9nTgHz6djzn8N8GQ+Yn
# NtWqqIeVdSyRNcuRKX9H9gm3hdcQ0t0z65AQ6SQgQuyLdMuv6+9TgbJfuYFQTbP9
# SZp2EOc6wI2oNzol4tH+t0yrpiVhAVZ2wQ80fs48Woy/I9CvDF/h3PP8Fnx9N8Z0
# 8DVmonCGuXjDhN1B4CtWcCNOjsfcnejq065E/6Or8XlOBFA+cEkHgtDXsLJB0AFI
# oopxPU+AMuknxlvTQojcAiE8EhFhlu8eZ/4jWee3bqW8RmSxg+/SdTrsLZX1XlFF
# kMfOyBOzLKsRvX+0vRxyeCBHSC8ziMpRSSJ2SgiVctDzLkHJIfwhGqY0wz0LEsgK
# M9z+KIvaTdZ8b1NEplGVK99BEY9gqTQXonre/j7NmUdM9uTbnGBIWLHVuibgTp4T
# 8F4g5Iz9KiNTw+OPAk+mUR0lt3Sc5CBx0illDijyVjFkK2ONamPKIxL5/Uqw9XP6
# YtPApFcigvPn/TZpu5t+KgxLBY7k3OAd0JUbFk1CM3lrive3iDEuFy8=
# SIG # End signature block
