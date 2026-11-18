param([ValidateSet('Package','CopyRuntime','ValidateRuntime')][string]$Operation = 'Package')
$ErrorActionPreference = 'Stop'
$releaseFolder = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'release-0.6.24-signed'))
$sourceRuntime = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'translation-bundle\runtime'))
$releaseRuntime = Join-Path $releaseFolder 'translation'

function Assert-NoRuntimeRedirect([string]$Path, [switch]$Descendants) {
    $trusted = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    if ($candidate -cne $trusted -and -not $candidate.StartsWith($trusted + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime preparation path is outside the application project.' }
    $cursor = $candidate
    while ($cursor.Length -ge $trusted.Length) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.LinkType -in @('SymbolicLink','Junction')) { throw 'Runtime source/release paths cannot be filesystem links.' }
        }
        if ($cursor.Equals($trusted,[StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if ($Descendants -and (Test-Path -LiteralPath $candidate -PathType Container)) {
        if (@(Get-ChildItem -LiteralPath $candidate -Force -Recurse | Where-Object { $_.LinkType -in @('SymbolicLink','Junction') }).Count -gt 0) { throw 'Runtime source/release contents cannot be filesystem links.' }
    }
}

function Get-SafeRuntimeRelativePath([string]$Root, [string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'A runtime file is outside the expected translation directory.' }
    $relative = $absolute.Substring($prefix.Length).Replace('\','/')
    if ([String]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative -match '[\x00-\x1F\x7F:]' -or @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) { throw 'Invalid translation runtime relative path.' }
    return $relative
}

function Get-VerifiedRuntime {
    Assert-NoRuntimeRedirect $sourceRuntime -Descendants
    foreach ($name in @('runtime\node.exe','worker.js','bundle.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $sourceRuntime $name) -PathType Leaf)) { throw ('Translation runtime is incomplete: missing ' + $name + '. Finish the audited bundle before preparing a release.') }
    }
    $sourceItems = @(Get-ChildItem -LiteralPath $sourceRuntime -Recurse -Force)
    if (@($sourceItems | Where-Object { $_.LinkType -in @('SymbolicLink','Junction') }).Count -gt 0) { throw 'Translation runtime cannot contain filesystem links.' }
    $bundle = Get-Content -LiteralPath (Join-Path $sourceRuntime 'bundle.json') -Raw | ConvertFrom-Json
    # The bundle author sets this gate only after reviewing upstream engine,
    # model and runtime licenses. Source hashes are validated separately below.
    if ($bundle.licenseReview.complete -ne $true -or @($bundle.licenseReview.notices).Count -eq 0) { throw 'Translation bundle license review is incomplete.' }
    foreach ($notice in @($bundle.licenseReview.notices)) {
        if ($notice -isnot [string] -or [String]::IsNullOrWhiteSpace($notice) -or [IO.Path]::IsPathRooted($notice) -or $notice -match '[\x00-\x1F\x7F:]' -or @($notice.Replace('\','/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0) { throw 'Invalid bundled license notice path.' }
        $noticePath = [IO.Path]::GetFullPath((Join-Path $sourceRuntime $notice))
        Get-SafeRuntimeRelativePath $sourceRuntime $noticePath | Out-Null
        if (-not (Test-Path -LiteralPath $noticePath -PathType Leaf) -or (Get-Item -LiteralPath $noticePath).Length -lt 1) { throw ('Missing bundled license notice: ' + $notice) }
    }
    if ($bundle.version -ne 1 -or @($bundle.models).Count -ne 6) { throw 'The reviewed bundle must contain all six supported direct model directions.' }
    $requiredPairs = @('en-de','de-en','en-es','es-en','en-fr','fr-en')
    $modelPairs = New-Object 'System.Collections.Generic.List[string]'
    foreach ($model in @($bundle.models)) {
        $pair = [string]$model.source + '-' + [string]$model.target
        if ($requiredPairs -cnotcontains $pair -or $modelPairs -ccontains $pair -or $model.directory -cne ($pair + '-tiny')) { throw 'Unexpected or duplicate model language pair.' }
        $modelPairs.Add($pair)
        if ($null -eq $model.files -or @($model.files.vocabs).Count -lt 1 -or @($model.files.vocabs).Count -gt 2) { throw 'A model is missing its reviewed vocabulary files.' }
        foreach ($modelFile in @($model.files.model,$model.files.shortlist) + @($model.files.vocabs)) {
            if ($modelFile -isnot [string] -or $modelFile -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,179}\z') { throw 'Invalid model payload filename.' }
            $modelPath = Join-Path (Join-Path $sourceRuntime $model.directory) $modelFile
            if (-not (Test-Path -LiteralPath $modelPath -PathType Leaf) -or (Get-Item -LiteralPath $modelPath).Length -lt 1) { throw ('Missing model payload file: ' + $pair) }
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $sourceRuntime 'bergamot\translator.js') -PathType Leaf) -or @(Get-ChildItem -LiteralPath (Join-Path $sourceRuntime 'bergamot') -Recurse -File -Filter '*.wasm').Count -eq 0) { throw 'The bundled Bergamot module/WASM engine is missing.' }
    $nodePath = Join-Path $sourceRuntime 'runtime\node.exe'
    $nodeSignature = Get-AuthenticodeSignature -LiteralPath $nodePath
    if ($nodeSignature.Status -ne 'Valid' -or $null -eq $nodeSignature.SignerCertificate -or $nodeSignature.SignerCertificate.Subject -notmatch '(?i)OpenJS|Node\.js') { throw 'The bundled Node runtime must retain its valid original Node/OpenJS publisher signature.' }
    $files = @($sourceItems | Where-Object { -not $_.PSIsContainer } | ForEach-Object {
        [pscustomobject]@{Relative=(Get-SafeRuntimeRelativePath $sourceRuntime $_.FullName);Source=$_.FullName;SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    } | Sort-Object Relative)
    if ($files.Count -lt 4) { throw 'The bundled translation engine, models, and license files are missing.' }
    $pins = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($pin in @($bundle.files)) {
        if ($pin.path -isnot [string] -or $pin.path -ceq 'bundle.json' -or $pin.sha256 -notmatch '\A[0-9a-fA-F]{64}\z') { throw 'The bundle file-integrity manifest is incomplete or invalid.' }
        $pinPath = [IO.Path]::GetFullPath((Join-Path $sourceRuntime $pin.path))
        if ((Get-SafeRuntimeRelativePath $sourceRuntime $pinPath) -cne $pin.path -or $pins.ContainsKey($pin.path)) { throw 'Unexpected or duplicate pinned bundle filename.' }
        $pins.Add($pin.path,$pin.sha256)
    }
    $pinnedFiles = @($files | Where-Object { $_.Relative -cne 'bundle.json' })
    if ($pins.Count -ne $pinnedFiles.Count) { throw 'The reviewed bundle manifest must pin every runtime/model/license file except itself.' }
    foreach ($file in $pinnedFiles) {
        if (-not $pins.ContainsKey($file.Relative) -or $pins[$file.Relative] -ine $file.SHA256) { throw ('Runtime bytes differ from the reviewed bundle hash: ' + $file.Relative) }
    }
    return [pscustomobject]@{Files=$files;NodeSignature=$nodeSignature;Bundle=$bundle}
}

function Assert-ReleasedRuntime($Runtime) {
    Assert-NoRuntimeRedirect $releaseFolder
    Assert-NoRuntimeRedirect $releaseRuntime -Descendants
    if (-not (Test-Path -LiteralPath $releaseRuntime -PathType Container)) { throw 'The release translation folder is missing; run the Application signing preparation first.' }
    $expected = @($Runtime.Files | ForEach-Object Relative | Sort-Object)
    $actual = @(Get-ChildItem -LiteralPath $releaseRuntime -Recurse -File -Force | ForEach-Object { Get-SafeRuntimeRelativePath $releaseRuntime $_.FullName } | Sort-Object)
    if (@(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) { throw 'The release translation directory does not match the audited runtime file list.' }
    foreach ($file in $Runtime.Files) {
        if ((Get-FileHash -LiteralPath (Join-Path $releaseRuntime $file.Relative) -Algorithm SHA256).Hash -cne $file.SHA256) { throw ('Release translation bytes differ from the audited source: ' + $file.Relative) }
    }
}

function Get-VerifiedReleaseNotices {
    $emojiFolder = Join-Path $PSScriptRoot 'assets\emoji'
    Assert-NoRuntimeRedirect $emojiFolder -Descendants
    $emojiLicense = Join-Path $emojiFolder 'LICENSE.txt'
    $emojiAttribution = Join-Path $emojiFolder 'ATTRIBUTION.txt'
    $emojiManifestPath = Join-Path $emojiFolder 'manifest.json'
    $translationNotices = Join-Path $sourceRuntime 'licenses\NOTICES.txt'
    $videoFilmstripNoticeFolder = Join-Path $PSScriptRoot 'third-party\video-filmstrip'
    Assert-NoRuntimeRedirect $videoFilmstripNoticeFolder -Descendants
    $videoFilmstripNotice = Join-Path $videoFilmstripNoticeFolder 'NOTICE.txt'
    foreach ($noticeSource in @($emojiLicense,$emojiAttribution,$emojiManifestPath,$translationNotices,$videoFilmstripNotice)) {
        if (-not (Test-Path -LiteralPath $noticeSource -PathType Leaf) -or (Get-Item -LiteralPath $noticeSource).Length -lt 1) { throw 'A required third-party notice source is missing or empty.' }
    }
    $emojiManifest = [IO.File]::ReadAllText($emojiManifestPath) | ConvertFrom-Json
    if ($emojiManifest.schemaVersion -ne 1 -or $emojiManifest.license.spdx -cne 'MIT' -or
        $emojiManifest.license.localFile -cne 'LICENSE.txt' -or $emojiManifest.license.sha256 -isnot [string] -or
        $emojiManifest.license.sha256 -cnotmatch '\A[0-9a-fA-F]{64}\z' -or
        (Get-FileHash -LiteralPath $emojiLicense -Algorithm SHA256).Hash -ine $emojiManifest.license.sha256) { throw 'The Fluent Emoji MIT license does not match its source manifest.' }
    if ((Get-Item -LiteralPath $videoFilmstripNotice).Length -ne 2451 -or
        (Get-FileHash -LiteralPath $videoFilmstripNotice -Algorithm SHA256).Hash -cne '33CF5DF44BF729396A0CC94ADCA264B884645AF8238987B66C06EAABACFF1AC7') {
        throw 'The video filmstrip notice differs from the reviewed reference credit and complete pinned MIT license.'
    }
    $combined = "Snip Studio - third-party notices`r`n`r`n" + [IO.File]::ReadAllText($emojiAttribution) +
        "`r`n`r`n" + [IO.File]::ReadAllText($emojiLicense) + "`r`n`r`n" + [IO.File]::ReadAllText($translationNotices) +
        "`r`n`r`n" + [IO.File]::ReadAllText($videoFilmstripNotice)
    $notices = @(
        [pscustomobject]@{Relative='licenses/NOTICES.txt';Bytes=[Text.UTF8Encoding]::new($false).GetBytes($combined)},
        [pscustomobject]@{Relative='licenses/FluentEmoji-LICENSE.txt';Bytes=[IO.File]::ReadAllBytes($emojiLicense)},
        [pscustomobject]@{Relative='licenses/FluentEmoji-ATTRIBUTION.txt';Bytes=[IO.File]::ReadAllBytes($emojiAttribution)},
        [pscustomobject]@{Relative='licenses/FluentEmoji-manifest.json';Bytes=[IO.File]::ReadAllBytes($emojiManifestPath)}
    )
    foreach ($notice in $notices) {
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($algorithm.ComputeHash([byte[]]$notice.Bytes)).Replace('-','') }
        finally { $algorithm.Dispose() }
        Add-Member -InputObject $notice -NotePropertyName SHA256 -NotePropertyValue $hash
    }
    return $notices
}

function Assert-ReleasedNotices($Notices, [switch]$AllowMissing) {
    Assert-NoRuntimeRedirect $releaseFolder
    $noticeFolder = Join-Path $releaseFolder 'licenses'
    Assert-NoRuntimeRedirect $noticeFolder -Descendants
    $expected = @($Notices | ForEach-Object Relative | Sort-Object)
    $actual = @()
    if (Test-Path -LiteralPath $noticeFolder -PathType Container) {
        $actual = @(Get-ChildItem -LiteralPath $noticeFolder -Recurse -File -Force | ForEach-Object { Get-SafeRuntimeRelativePath $releaseFolder $_.FullName } | Sort-Object)
    }
    if ($AllowMissing) {
        if (@($actual | Where-Object { $expected -cnotcontains $_ }).Count -ne 0) { throw 'The release notice directory contains an unexpected file; no files were deleted.' }
        return
    }
    if ($expected.Count -ne 4 -or $actual.Count -ne 4 -or @(Compare-Object $expected $actual -CaseSensitive).Count -ne 0) { throw 'The release notice directory must contain the exact four third-party notice files.' }
    foreach ($notice in $Notices) {
        if ((Get-FileHash -LiteralPath (Join-Path $releaseFolder $notice.Relative) -Algorithm SHA256).Hash -cne $notice.SHA256) { throw ('Release notice bytes differ from the verified source: ' + $notice.Relative) }
    }
}

$runtime = Get-VerifiedRuntime
$releaseNotices = @(Get-VerifiedReleaseNotices)
if ($Operation -eq 'CopyRuntime') {
    Assert-NoRuntimeRedirect $releaseFolder
    Assert-NoRuntimeRedirect $releaseRuntime -Descendants
    Assert-ReleasedNotices $releaseNotices -AllowMissing
    [IO.Directory]::CreateDirectory($releaseRuntime) | Out-Null
    $sourceNames = @($runtime.Files | ForEach-Object Relative)
    foreach ($existing in Get-ChildItem -LiteralPath $releaseRuntime -Recurse -File -Force) {
        if ($sourceNames -cnotcontains (Get-SafeRuntimeRelativePath $releaseRuntime $existing.FullName)) { throw 'The release translation directory contains an unexpected file; no files were deleted.' }
    }
    foreach ($file in $runtime.Files) {
        $destination = Join-Path $releaseRuntime $file.Relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $destination -Force
    }
    Assert-ReleasedRuntime $runtime
    foreach ($notice in $releaseNotices) {
        $destination = Join-Path $releaseFolder $notice.Relative
        [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
        [IO.File]::WriteAllBytes($destination,[byte[]]$notice.Bytes)
    }
    Assert-ReleasedNotices $releaseNotices
    Write-Output ('Copied and verified ' + $runtime.Files.Count + ' audited translation files and four third-party notice files; Node retains its original publisher signature.')
    return
}
Assert-ReleasedRuntime $runtime
Assert-ReleasedNotices $releaseNotices
if ($Operation -eq 'ValidateRuntime') { Write-Output ('Validated ' + $runtime.Files.Count + ' release translation files and four third-party notice files.'); return }

$app = Join-Path $releaseFolder 'Snip Studio.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $app
if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'A valid timestamped application signature is required.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne '0.6.24.0') { throw 'Unexpected release version.' }
foreach ($name in @('README.txt','VALIDATION.txt')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $releaseFolder $name) -Force }
$files = @('Snip Studio.exe','Snip Studio.exe.config','README.txt','VALIDATION.txt','Signature Verification.json')
$files += @('licenses/NOTICES.txt','licenses/FluentEmoji-LICENSE.txt','licenses/FluentEmoji-ATTRIBUTION.txt','licenses/FluentEmoji-manifest.json')
$files += @($runtime.Files | ForEach-Object { 'translation/' + $_.Relative })
$lines = foreach ($name in $files) {
    $path = Join-Path $releaseFolder $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw ('Missing payload: ' + $name) }
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $name
}
$lines | Set-Content -LiteralPath (Join-Path $releaseFolder 'SHA256SUMS.txt') -Encoding UTF8
$files += 'SHA256SUMS.txt'
$zip = Join-Path $PSScriptRoot 'SnipStudio-0.6.24-signed-portable.zip'
$temporaryZip = Join-Path $PSScriptRoot ('.snip-portable-' + [Guid]::NewGuid().ToString('N') + '.zip')
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    $archive = [IO.Compression.ZipFile]::Open($temporaryZip,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $files) { [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $releaseFolder $name),$name,[IO.Compression.CompressionLevel]::Optimal) | Out-Null }
    } finally { $archive.Dispose() }
    $archive = [IO.Compression.ZipFile]::OpenRead($temporaryZip)
    try {
        if ($archive.Entries.Count -ne $files.Count) { throw 'Unexpected archive payload count.' }
        foreach ($name in $files) {
            $entry = @($archive.Entries | Where-Object { $_.FullName -ceq $name })
            if ($entry.Count -ne 1) { throw ('Missing or duplicate archive entry: ' + $name) }
            $stream = $entry[0].Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $hash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
            finally { $stream.Dispose(); $hasher.Dispose() }
            if ($hash -cne (Get-FileHash -LiteralPath (Join-Path $releaseFolder $name) -Algorithm SHA256).Hash) { throw ('Archive bytes differ: ' + $name) }
        }
    } finally { $archive.Dispose() }
    if ([IO.File]::Exists($zip)) {
        # Passing $null for the backup filename is not reliable through every
        # PowerShell/.NET binding. Keep a concrete backup inside this workspace.
        $zipBackup = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('SnipStudio-0.6.24-signed-portable.previous-' + [Guid]::NewGuid().ToString('N') + '.zip')))
        if (-not [IO.Path]::GetDirectoryName($zipBackup).Equals([IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase)) { throw 'Portable archive backup is outside the project directory.' }
        [IO.File]::Replace($temporaryZip,$zip,$zipBackup)
        Write-Output ('Previous same-version portable archive retained at: ' + $zipBackup)
    } else { [IO.File]::Move($temporaryZip,$zip) }
    $temporaryZip = $null
} finally { if ($null -ne $temporaryZip -and [IO.File]::Exists($temporaryZip)) { [IO.File]::Delete($temporaryZip) } }
Write-Output ('Prepared and verified ' + $files.Count + '-file portable release, including translation runtime, models and licenses: ' + $zip)


# SIG # Begin signature block
# MII2tQYJKoZIhvcNAQcCoII2pjCCNqICAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCDq772SlV15/4z2
# fnME7XJ/cu6sF/xajfQ/JPcJ/Ds8q6CCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# MC8GCSqGSIb3DQEJBDEiBCAaTLNEIPZonQAVEQ2Kq+tJh3XfXu74x0UwpZiJ9BPc
# 4zANBgkqhkiG9w0BAQEFAASCAYBI+AO8hBPSv77Ze7VBAuGvbIzVvBcuT6U9tIyA
# +Zn4oKCqQIC5/oIwSDFKMdANaRohjSupnNFSAndKHq1kiLSFCmX1c9VxpyjZUrc2
# kAG7ZQCtgmvlKZlsKrwNZvpnXRFSvGmQN+hEnDNtGC+UmHHEVvmOfpOZxvzGOvvk
# Fh8uXkaQ27HM5vtS5oSrZPTyE3yJ+x1fnUT4Ko/7H3XPzhnoNoPazJrLLLGxq/Tm
# Fqaq5h3aDLJXmEg/6ROCyEsgER9BQtl6JUvVyWOwfZIr6U4IJ8iMqHAaUFpKB2vh
# tH0Jayz/XxP5YxJzATnZ9mym88RkCe7O0S4hRRTYkDewJm+2EQBFUxFpvL2J0Ad+
# vDM6B43AA/doeACrdMylNdk9u2kKljAstOZA+GbsuhG+2mA3rTOgRvLN3fvZuhUl
# vY3efjMVJQodECj9a3lOdgo5sNMAALroJ9N91jVmg/EOtAPCGc7cmVZIL7K9jv5g
# fAqlWC4xoEhh1WnZP1nf1sJfHWmhghgRMIIYDQYKKwYBBAGCNwMDATGCF/0wghf5
# BgkqhkiG9w0BBwKgghfqMIIX5gIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgA1Xs1ruxbI7uX24gj9YZYr5YkbAQDV4R1yTyaqcimiECBmr86+Y0PxgT
# MjAyNjExMTgyMjEwNTUuODA2WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
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
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMjEwNTVaMC8GCSqGSIb3
# DQEJBDEiBCDl4sXuPMECbMcCZWhNfsAFxiOBuNkFHZtPPEFqivlaXzCBuQYLKoZI
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
# AQEBBQAEggIAdIVDNGEP4JxwN7+ESvB19a8rxgkuDec/IVAoruYswTah0xi0jkRp
# PDUn+4BvJiFvAmJIRw2Ta6wGMcpPAPki9TAL4MP3lgm9LizFHXCmCcTPe97jdflI
# jp95Q/fY10y3Ssai/soOa2jR03gihFHH8r3Oj/OVXtKY4ntMo+xXrl6WE8jhpLZn
# 4wsJVqi9W+oT/7vystL7tJM0cV0GQVhVBLbizIJNDnoA9blme//nG3LdF+BJiYFI
# qaMfmTuUUAFG75utyEgWozagi2npWe5WU9zQN9V8nOFsJjalyJy+VZxoO35vYQEA
# P3orXeUCRysp9BvtNshio1XObc55B9pv9n4MMpMEocJOlzkxzpI00FF151d208Ea
# evDTC6gsRIkcKPky9GrjOfvQxnIURCvrUmUrPkmkzEduNaA+Rhq+8kuslsQyHqv+
# HbXQZCMdiducvYIYZjVCAyI/4ucGi8iAOX8w1D/LD4WZrj+i1zzG+XjTlD2exYR5
# CnwN9mYUmRVCtufCDST8wFpSX/TzQxn6AhRWUCeJ5/eqZMIo//k2u3npTMoADZjy
# 67Iw9qKDFRL/XEhYVE7bNBQXf+Jbf+tuFqLicp0jHm91ca7uL/cPampY6vS55mXM
# AV3YtMscqunee7SO7t01YbMVSabS9s8pAW6IOI9iw+1Inu0P5zpcEL8=
# SIG # End signature block
