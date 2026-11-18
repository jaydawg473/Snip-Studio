param([switch]$Check, [string]$Destination = 'dist', [switch]$UseExistingIcons, [switch]$RebuildIcons)
$ErrorActionPreference = 'Stop'
if ($Check) { throw 'The legacy -Check path ran unsigned programs and is disabled. Build first, sign the app and reviewed check executables, verify their publisher/timestamps, then run the signed verification plan.' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework C# compiler was not found.' }
$compilerSignature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($compilerSignature.Status -ne 'Valid' -or $null -eq $compilerSignature.TimeStamperCertificate) { throw 'The compiler must have a valid timestamped publisher signature.' }
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

function Get-VerifiedVideoFilmstripNotice([string]$ProjectFolder = $PSScriptRoot) {
    $projectRoot = [IO.Path]::GetFullPath($ProjectFolder).TrimEnd('\')
    $noticeSource = Join-Path $projectRoot 'third-party\video-filmstrip\NOTICE.txt'
    if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf)) { throw 'The reviewed video filmstrip technical-reference notice is required.' }
    $cursor = $noticeSource
    while ($cursor.Length -ge $projectRoot.Length) {
        $item = Get-Item -LiteralPath $cursor -Force
        if ($item.LinkType -in @('SymbolicLink','Junction')) { throw 'The video filmstrip notice source cannot use filesystem links.' }
        if ($cursor.Equals($projectRoot,[StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if ((Get-Item -LiteralPath $noticeSource).Length -ne 2451 -or
        (Get-FileHash -LiteralPath $noticeSource -Algorithm SHA256).Hash -cne '33CF5DF44BF729396A0CC94ADCA264B884645AF8238987B66C06EAABACFF1AC7') {
        throw 'The video filmstrip notice differs from the reviewed reference credit and complete pinned MIT license.'
    }
    return [IO.File]::ReadAllText($noticeSource)
}

function Copy-OpenSourceNotices([string]$Folder) {
    $emojiFolder = Join-Path $PSScriptRoot 'assets\emoji'
    $videoFilmstripNotice = Get-VerifiedVideoFilmstripNotice
    $noticeFolder = Join-Path $Folder 'licenses'
    [IO.Directory]::CreateDirectory($noticeFolder) | Out-Null
    $combinedNotices = "Snip Studio - third-party notices`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $emojiFolder 'ATTRIBUTION.txt')) + "`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $emojiFolder 'LICENSE.txt')) + "`r`n`r`n" +
        [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'translation-bundle\runtime\licenses\NOTICES.txt')) + "`r`n`r`n" +
        $videoFilmstripNotice
    [IO.File]::WriteAllText((Join-Path $noticeFolder 'NOTICES.txt'),$combinedNotices,[Text.UTF8Encoding]::new($false))
    [IO.File]::Copy((Join-Path $emojiFolder 'LICENSE.txt'),(Join-Path $noticeFolder 'FluentEmoji-LICENSE.txt'),$true)
    [IO.File]::Copy((Join-Path $emojiFolder 'ATTRIBUTION.txt'),(Join-Path $noticeFolder 'FluentEmoji-ATTRIBUTION.txt'),$true)
    [IO.File]::Copy((Join-Path $emojiFolder 'manifest.json'),(Join-Path $noticeFolder 'FluentEmoji-manifest.json'),$true)
}

# Validate the required reference credit before compiling any application output.
$null = Get-VerifiedVideoFilmstripNotice
$emojiResources = @(Get-VerifiedEmojiResources)
if ($UseExistingIcons -or -not $RebuildIcons) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'assets\SnipStudio.ico'))) { throw 'The existing application icon was not found.' }
} else {
    $iconTool = Join-Path $PSScriptRoot 'tools\BuildIcons.exe'
    if (-not (Test-Path -LiteralPath $iconTool)) { throw 'Compile and sign the reviewed icon tool before using -RebuildIcons.' }
    $iconSignature = Get-AuthenticodeSignature -LiteralPath $iconTool
    if ($iconSignature.Status -ne 'Valid' -or $null -eq $iconSignature.TimeStamperCertificate -or $iconSignature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'The icon tool must be timestamp-signed by Automation Forge before it runs.' }
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
Write-Output "Built $appFile"
Write-Output 'Compilation only. Sign and verify this application before executing it; no checks were run.'

# SIG # Begin signature block
# MII2uAYJKoZIhvcNAQcCoII2qTCCNqUCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCD3lViytDj4Z6T2
# JF72jSbCKplyHMdSN1UMV4gzhRCKVaCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# L60cPqfny+Fq8UiuZzGCGrswghq3AgEBMHEwWjELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElE
# IFZlcmlmaWVkIENTIEFPQyBDQSAwNAITMwAMYZcpenhPEOpfDwAAAAxhlzANBglg
# hkgBZQMEAgEFAKCBhDAYBgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEV
# MC8GCSqGSIb3DQEJBDEiBCDPbIOAypARiGjlAmV45QTOZOWF1twdkKFU8abDcKHS
# 8DANBgkqhkiG9w0BAQEFAASCAYBSiZRIl1IBR+YYHa46CRywxMnlj1pQUMQvCVVL
# 17SIdyt1czuj6aHTqsH4y/5CtH7U/LCeQF2Wjl6ig93/fm5aY1SMUfWj1xzGR6Su
# J1JSJTRRRKOqX2iSQlvmeZ4lfnVHILqEJ5TlSwM0DG/F2rB+dFVoCsdtNKnqHFNJ
# hYsHchrNzNXnin3QPLU+Eg8R7TyB1uyqiOSBnXzwDkz+iELv9apnazpkuJ3IIr+F
# BPxcR6cmnv7wPE8nB6qYZ0w1ZgJPi86/U/+QrpHGSFcREDW0q+fIEvEGJnK1LA+I
# /Ng93B1HeDt24I2IySEruL8HcW/RiVcTHORYjrPbcvs1fjwsrYceX44OpeX6SVJt
# J2Jmh8Ejb8uyY2mo2INNPTymvOgFJIeTmE2NkJxMk0z1pO0bljwE7ro3GuNtj/Nf
# 4Fl/Rjzg5ZdjSTs7paETAbmZlDGh1G+D/QbWp4WKaUucx3+4zh8xyvII7RlEGcVw
# u5DKB8Fc4sx727DUKo8YU3qhI56hghgUMIIYEAYKKwYBBAGCNwMDATGCGAAwghf8
# BgkqhkiG9w0BBwKgghftMIIX6QIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgeqMsCePhWSkW7K+nv3Vg5Jfb48cnV1HkZXP3IHvmbVUCBmr835JbcRgT
# MjAyNjExMTgyMTM3NTQuNzg5WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
# EzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoT
# FU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJp
# Y2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjdEMDAtMDVF
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
# hI6JQivRepyvWcl+JYbYbBh7pmgAXVswggeXMIIFf6ADAgECAhMzAAAAXyrk+CNF
# vBpJAAAAAABfMA0GCSqGSIb3DQEBDAUAMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJs
# aWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMB4XDTI2MDkxNzE4NTcyNVoXDTI3
# MDkxNDE4NTcyNVowgdsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9u
# MRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRp
# b24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
# BAsTHm5TaGllbGQgVFNTIEVTTjo3RDAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWlj
# cm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGluZyBBdXRob3JpdHkwggIiMA0G
# CSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQDnVJfDXnS7N1UNpKD3LzU3QoXX76YD
# Bak2lFCAXysRz01aKzCEq0qBF6uH4+NdIduLAiqVm9fi7pXBEN02Y2jagFF+J4TE
# atM3DnqRosIHfXBW/IEy3kvGLiGQhzbUST6mgDNT0SFGCxSvsxglAspwEvTsYZff
# 88Z1Kss1OSDFpTP/ahm9yZLh/yqqhUE5SHbaJrtAUkc9Q5rXaajEabN7bwW734nG
# 2jyQsRn0az0rYJNisvGzopI/XRbCOi4CPZ9KCofY86hvd66KDMvCmaPpiH4p0m7Q
# 1gE1+gQFMktXuQ/HkehleRJ/9jPeH0b1dA0Vw2JtOq2mRHw+l2Ngx1Em+egRVr/y
# +t9zmxu5ismRV5n0fXodVQYOwMdY+8ufUxLExODhN3cm/pjm2cCeX/IfGJAWUTXN
# b9zZ8p9i8P5lxJ4oRi9ROanfX9tnRXymYQ4P0gveAQJvtrpwaGwxm+nODpZQ2lbr
# oLAv+bkxcsTOszSok2dhQXVGRpDwhi4ZjN0k9/TqLQK7914SyJktuGQJxkbdZWP8
# m4GNhCIqXw4rCqz/9BrCfs5zLFTVxYvcnfvHo+1roLIqvFnauuIUEyAgMkckgMni
# TA+Ke+WRQNvAueOjSGzRKaL3Td6YN0b0gf+9cbM9KIUi96H7myDd6JdhuSWR8dhk
# 4zmZE/mwToZdXQIDAQABo4IByzCCAccwHQYDVR0OBBYEFFKHTw3RLnp4xXmi9vYK
# gO/8OVrcMB8GA1UdIwQYMBaAFGtpKDo1L0hjQM972K9J6T7ZPdshMGwGA1UdHwRl
# MGMwYaBfoF2GW2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01p
# Y3Jvc29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAy
# MC5jcmwweQYIKwYBBQUHAQEEbTBrMGkGCCsGAQUFBzAChl1odHRwOi8vd3d3Lm1p
# Y3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMFB1YmxpYyUyMFJT
# QSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAyMC5jcnQwDAYDVR0TAQH/BAIwADAW
# BgNVHSUBAf8EDDAKBggrBgEFBQcDCDAOBgNVHQ8BAf8EBAMCB4AwZgYDVR0gBF8w
# XTBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMAgGBmeBDAEEAjAN
# BgkqhkiG9w0BAQwFAAOCAgEAMaJXDO66NeEKKad9MWk1RCgFCQbQDV9aXj8riioU
# KHr8kr7Fl0DjYY0aghnlBvGWzhwxMMd8QIev4DlnMhn0Bk4O8eQ+u9MBu0rfB1rK
# QO9tYsL4s/RJmncel5qpff34xKiOyWb4CiqmrBAHTFrMGtn95LYCFqgo1d3RyguE
# n0SfDOE7T8LsWAYb64tZLmoFDvvrq+mC4SNt+02K9Tmh2j1heU+49uaEdEnAFyFl
# IVss1X/0l1x7qcPN0bnX2juw4nDMAhIsNOWFc3Z7Mfg2pEikVij07aq+M1d80Oii
# moeh6DQGgM9px2i6IlOtngXYXJhLWXBBatjUJLfrbO6SEVZ6UGI6TwcLF/Jcs+eS
# sooh3Eh6ARUfP/YjNyHFBVz2OTZ36ZRl1Q9IbJphkEAZhhj9d1Xn/sbq8Ldahnh2
# 5xOgJLOEjiTenxOV+eyAxpK5Db8yvK5tfvDqO8/HT41eFi5L1E5zLZKC3zjqr7MD
# OFYzZoVqnCZ/9oa9Qi0+OIRx02QR/BOEYuGU7an40UMDx5YBfr1UI2KAYBSF7L08
# UmRtiy1ssTEZMsQkHaaZY+mVJ1A2OIo/XMTmT2XFbJ2o/1SzC+9gxx71CaEvV1mh
# TJ7tjQG4PNScBo+gomN7ckP27R1gNNR3GqLsPQVlsjhDSOXgLkv1S0bB8qouEx0V
# wyMxggdGMIIHQgIBATB4MGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAXyrk+CNFvBpJAAAAAABfMA0GCWCGSAFl
# AwQCAQUAoIIEnzARBgsqhkiG9w0BCRACDzECBQAwGgYJKoZIhvcNAQkDMQ0GCyqG
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMTM3NTRaMC8GCSqGSIb3
# DQEJBDEiBCBPpmJUHyBPOxTNJ6gz3K10GFxup54gUy0KVtyMV9dUFzCBuQYLKoZI
# hvcNAQkQAi8xgakwgaYwgaMwgaAEIGz2CX6rV/SA4NKFJPRKsLdQXmycedbifzBe
# UsdbLiXDMHwwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVz
# dGFtcGluZyBDQSAyMDIwAhMzAAAAXyrk+CNFvBpJAAAAAABfMIIDYQYLKoZIhvcN
# AQkQAhIxggNQMIIDTKGCA0gwggNEMIICLAIBATCCAQmhgeGkgd4wgdsxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29m
# dCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjo3
# RDAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZSBTdGFtcGluZyBBdXRob3JpdHmiIwoBATAHBgUrDgMCGgMVAHEJOt7wvLuYy68A
# iPJTS+6G8uxvoGcwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwMA0GCSqGSIb3DQEBCwUAAgUA7qgG0zAiGA8yMDI2
# MTExODExMTMyM1oYDzIwMjYxMTE5MTExMzIzWjB3MD0GCisGAQQBhFkKBAExLzAt
# MAoCBQDuqAbTAgEAMAoCAQACAhrwAgH/MAcCAQACAhLRMAoCBQDuqVhTAgEAMDYG
# CisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEA
# AgMBhqAwDQYJKoZIhvcNAQELBQADggEBAJCqUISd9MH0zvx3bElilBT26QA4CZuu
# 4wvnLyNSOg7xIe9crwe0SgE+uw6e+G0ky3oXNAm5xBUW6ZdmnE6wKxXQVMfZvfbh
# 0ygbj3T4Ul7srv701RC1Ad/iaZAgFV79VyN5+CTAL3lVpq61bZSfQ2251FMg1ui9
# CPBXDe2X2eI5KFdIfoJAzyadKi2xwkoT9taDbvFm9IdfikcdJc8JHt10L+2X9Q9i
# qsikN+LJxdpjJ/Ll16GDY2yd1cShIYhNFBnrJ4nd0m1rjB1CoEYOEKhYBMneEmTd
# h6n73F482pSFaZrOJwQg1GWHTqgqYtH5al/MgAZIjvsgE12rEkyfH1owDQYJKoZI
# hvcNAQEBBQAEggIAg703t7qkwkvlAkJcm2ejH3d+aGTmoV9gyuWjo0F41OtTlOg/
# 7PXRwUf24rjqNDJQJOYv8tbbqRH3b69qG05PjPe4lIyd8YtfG8O65mxgJ9qkdmkY
# maqV/Wyh4TIqCK/JVOtZ3cSPqJmcYgvfWQEx0cwTQIsrzLsRClzcFvzzwsJNV8rD
# tydR3Lz+W7smQY5vR8GB9fVI03khICYa9XQfndaYruN4OozHvVXRZXqgNzCXnw5P
# 6AW1ldqHmClhhCezknTpnMbizyp25a2eD3txpqn4LK2cdW7aSb5Kmq1r1tc31u+T
# 3HQqsdpUMIebwjM5fulbk2v24hHWrlnCJJWxkDtjL7x1Vw/pQ63/dZV6Sq2HEUtH
# AS2fpQYVQY8MdKqepB8mpknG/RFZeNuVZOwHHYDtLQMxJ05IHT96+zohjV1qrwUP
# zSJyoOPg8BuICSYCU+nZ/nRTb4FZhDP174DaQqTMebAZXvR9ilCSbnkmXYHmk8jU
# 86S1l5lzcRFihvQrIsg832rR4+uwKKq3UXWxySLq5BWdTrWbQUmgqsD/QIWzjhqS
# rFpSk695nm9WGaxIgzaag8dz7B3IngUsmHc24/Z5cU8dRXzz17U/xoPbrhZl3gDQ
# ehS23wEW2oi28C69MfT5Z2GP9uRi4WXDFyryMEuhizPkRtYKPcuwaJSIxL8=
# SIG # End signature block
