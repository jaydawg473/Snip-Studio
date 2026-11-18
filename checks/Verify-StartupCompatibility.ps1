# Pure signed-library startup policy checks. No application entrypoint, broker,
# native window/process API, input, clipboard, preferences or registry access.
param([string]$ReleaseFolder='release-0.5.3-signed',[string]$ExpectedVersion='0.5.3.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
$assembly=[Reflection.Assembly]::LoadFrom($app)
$policy=$assembly.GetType('SnipStudio.ShortcutHostCompatibility',$true)
$method=$policy.GetMethod('Check',[Reflection.BindingFlags]'Static,Public,NonPublic')
if($null -eq $method -or $method.GetParameters().Count -ne 5){throw 'The pure startup compatibility policy was not found.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
$observations=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Startup([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Check-Startup([string]$LaunchingPath,[Version]$LaunchingVersion,[string]$RunningPath,[Version]$RunningVersion,[string]$VerificationError){
    $arguments=New-Object object[] 5
    $arguments[0]=$LaunchingPath;$arguments[1]=$LaunchingVersion;$arguments[2]=$RunningPath;$arguments[3]=$RunningVersion;$arguments[4]=$VerificationError
    return $method.Invoke($null,$arguments)
}
function Confirm-Blocked([string]$Message,[string]$Label){
    Confirm-Startup (-not [string]::IsNullOrWhiteSpace($Message)) ($Label+' refuses silent forwarding')
    Confirm-Startup ($Message -match '(?i)save' -and $Message -match '(?i)exit') ($Label+' explains how to preserve and exit the existing session')
    $observations.Add([ordered]@{Case=$Label;Message=$Message})
}
$installed='C:\SyntheticProfiles\Example\AppData\Local\Programs\Automation Forge\Snip Studio\Snip Studio.exe'
$portable='C:\SyntheticProfiles\Example\AppData\Local\Temp\SnipStudio-0.5.0-signed-portable.zip\Snip Studio.exe'
$current=[Version]::new(0,5,3,0)
$old=[Version]::new(0,5,0,0)

$message=Check-Startup $installed $current $installed $current $null
Confirm-Startup ($null -eq $message) 'Matching installed path and version remain eligible for normal forwarding'
$message=Check-Startup $installed $current $installed.ToUpperInvariant() $current $null
Confirm-Startup ($null -eq $message) 'Windows path casing does not block the same installed copy'
$normalized='C:\SyntheticProfiles\Example\AppData\Local\Programs\Automation Forge\Snip Studio\..\Snip Studio\Snip Studio.exe'
$message=Check-Startup $installed $current $normalized $current $null
Confirm-Startup ($null -eq $message) 'Equivalent absolute paths normalize to the same installed copy'

$message=Check-Startup $installed $current $portable $old $null
Confirm-Blocked $message 'New installed app versus old portable broker'
Confirm-Startup ($message.Contains($installed) -and $message.Contains($portable)) 'Old portable diagnostic identifies both executable locations'
Confirm-Startup ($message.Contains('0.5.3') -and $message.Contains('0.5.0')) 'Old portable diagnostic identifies the launching and broker file versions'
Confirm-Startup ($message -match '(?i)file') 'Legacy broker version is described as file metadata'

$sameVersionPortable='C:\Tools\Snip Studio\Snip Studio.exe'
$message=Check-Startup $installed $current $sameVersionPortable $current $null
Confirm-Blocked $message 'Same-version portable copy at a different location'
Confirm-Startup ($message.Contains($installed) -and $message.Contains($sameVersionPortable)) 'Same-version location conflict remains reviewable'
$message=Check-Startup $installed $current $installed $old $null
Confirm-Blocked $message 'Older file version at the same location'
$message=Check-Startup $installed $current $installed ([Version]::new(0,5,4,0)) $null
Confirm-Blocked $message 'Newer file version at the same location'

$message=Check-Startup $installed $current $null $null 'Access to broker metadata was denied.'
Confirm-Blocked $message 'Unavailable broker path and version'
$message=Check-Startup $installed $current $installed $null $null
Confirm-Blocked $message 'Missing broker version'
$message=Check-Startup $installed $current $installed $current 'Broker identity could not be verified.'
Confirm-Blocked $message 'Identity verification failure despite matching supplied metadata'
$message=Check-Startup 'Snip Studio.exe' $current 'Snip Studio.exe' $current $null
Confirm-Blocked $message 'Relative executable locations'
$message=Check-Startup $null $current $installed $current $null
Confirm-Blocked $message 'Missing launching executable path'
$message=Check-Startup $installed $null $installed $current $null
Confirm-Blocked $message 'Missing launching version'

Confirm-Startup ($assembly.GetName().Version.ToString() -eq $ExpectedVersion) 'Checks load the final assembly version rather than a stale candidate'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Observations=$observations.ToArray();Scope='Pure startup compatibility policy only; synthetic paths/version metadata; no host construction, native process/window/message calls, application entrypoint, settings or user work accessed.';Unverified='Real legacy broker discovery, process metadata access, startup dialog appearance and same-copy native forwarding.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'startup-compatibility.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' pure signed startup compatibility checks. No broker or application was launched.')


# SIG # Begin signature block
# MII2tAYJKoZIhvcNAQcCoII2pTCCNqECAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAxIHL4lQIwLRn+
# aCWdgPD+VJtW4y7YTaOhcZ7OkX0i8KCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# L60cPqfny+Fq8UiuZzGCGrcwghqzAgEBMHEwWjELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElE
# IFZlcmlmaWVkIENTIEFPQyBDQSAwNAITMwAMYZcpenhPEOpfDwAAAAxhlzANBglg
# hkgBZQMEAgEFAKCBhDAYBgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEV
# MC8GCSqGSIb3DQEJBDEiBCCmb74zwM3/j/ZHAN9R5OAwgWhkjD3itT5KIluY4v23
# GjANBgkqhkiG9w0BAQEFAASCAYBI3VxVPFijYnUtJ+hJ7DXtDPMAuGPQPuI5l3IA
# in6gENBeQXImiH0HRPt7dtrQBK4pMYyAmMYkLktL8WsjP9rYiTJAcu4gOm8UqHgz
# cAEchtfW+jOJTDuS0Ar0wNad0wdCNDoe/XkxsLqwbGBaYk7lx0A5u9rSur617yc8
# ojPM3GlUlz5ilVNm2Fbdb4gi9qlXcgEyiMt0isL0N51NrE4ldE3rWXjcBJT7PwPV
# 6CtoLAD6wMQP+AKWWOv3gqfyI7RtgqHm71yOIn0eAbtnQD9NWH3CPJLMvr+fasjU
# XIv2W58ZWiYUD67mwMyYKHy2IjKZ8Vgo3kmNimLZKGDwdx0N9drN8BbeR+9y5QZ1
# WSD2TytbhJ5MI0MSgJYCCh9oO/g5g5WlOZP9vZvbP/nsBQi2ogqwFsusdPVBQs9x
# uNVsOFlIR7Lueyka6OS9hwtDJpLzZ5rZz9AKIzWNikejT4810tr4P3MpLpDuBsb0
# WtH7hiray2uv4Pxf+Q9/gjnyojyhghgQMIIYDAYKKwYBBAGCNwMDATGCF/wwghf4
# BgkqhkiG9w0BBwKgghfpMIIX5QIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYQYLKoZI
# hvcNAQkQAQSgggFQBIIBTDCCAUgCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgZAoaSgnADGPE3NqflaABL9J3rjF/vjdKkyPFKT/OxgsCBmr86+YnJhgS
# MjAyNjExMTgyMTU0NTAuOTRaMASAAgH0oIHhpIHeMIHbMQswCQYDVQQGEwJVUzET
# MBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmlj
# YSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046NzgwMC0wNUUw
# LUQ5NDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWUgU3RhbXBp
# bmcgQXV0aG9yaXR5oIIPITCCB4IwggVqoAMCAQICEzMAAAAF5c8P/2YuyYcAAAAA
# AAUwDQYJKoZIhvcNAQEMBQAwdzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jv
# c29mdCBDb3Jwb3JhdGlvbjFIMEYGA1UEAxM/TWljcm9zb2Z0IElkZW50aXR5IFZl
# cmlmaWNhdGlvbiBSb290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDIwMB4XDTIw
# MTExOTIwMzIzMVoXDTM1MTExOTIwNDIzMVowYTELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1
# YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwggIiMA0GCSqGSIb3DQEBAQUA
# A4ICDwAwggIKAoICAQCefOdSY/3gxZ8FfWO1BiKjHB7X55cz0RMFvWVGR3eRwV1w
# b3+yq0OXDEqhUhxqoNv6iYWKjkMcLhEFxvJAeNcLAyT+XdM5i2CgGPGcb95WJLiw
# 7HzLiBKrxmDj1EQB/mG5eEiRBEp7dDGzxKCnTYocDOcRr9KxqHydajmEkzXHOeRG
# wU+7qt8Md5l4bVZrXAhK+WSk5CihNQsWbzT1nRliVDwunuLkX1hyIWXIArCfrKM3
# +RHh+Sq5RZ8aYyik2r8HxT+l2hmRllBvE2Wok6IEaAJanHr24qoqFM9WLeBUSudz
# +qL51HwDYyIDPSQ3SeHtKog0ZubDk4hELQSxnfVYXdTGncaBnB60QrEuazvcob9n
# 4yR65pUNBCF5qeA4QwYnilBkfnmeAjRN3LVuLr0g0FXkqfYdUmj1fFFhH8k8YBoz
# rEaXnsSL3kdTD01X+4LfIWOuFzTzuoslBrBILfHNj8RfOxPgjuwNvE6YzauXi4or
# p4Sm6tF245DaFOSYbWFK5ZgG6cUY2/bUq3g3bQAqZt65KcaewEJ3ZyNEobv35Nf6
# xN6FrA6jF9447+NHvCjeWLCQZ3M8lgeCcnnhTFtyQX3XgCoc6IRXvFOcPVrr3D9R
# PHCMS6Ckg8wggTrtIVnY8yjbvGOUsAdZbeXUIQAWMs0d3cRDv09SvwVRd61evQID
# AQABo4ICGzCCAhcwDgYDVR0PAQH/BAQDAgGGMBAGCSsGAQQBgjcVAQQDAgEAMB0G
# A1UdDgQWBBRraSg6NS9IY0DPe9ivSek+2T3bITBUBgNVHSAETTBLMEkGBFUdIAAw
# QTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9E
# b2NzL1JlcG9zaXRvcnkuaHRtMBMGA1UdJQQMMAoGCCsGAQUFBwMIMBkGCSsGAQQB
# gjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU
# yH7SaoUqG8oZmAQHJ89QEE9oqKIwgYQGA1UdHwR9MHsweaB3oHWGc2h0dHA6Ly93
# d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElkZW50aXR5
# JTIwVmVyaWZpY2F0aW9uJTIwUm9vdCUyMENlcnRpZmljYXRlJTIwQXV0aG9yaXR5
# JTIwMjAyMC5jcmwwgZQGCCsGAQUFBwEBBIGHMIGEMIGBBggrBgEFBQcwAoZ1aHR0
# cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQlMjBJ
# ZGVudGl0eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1
# dGhvcml0eSUyMDIwMjAuY3J0MA0GCSqGSIb3DQEBDAUAA4ICAQBfiHbHfm21WhV1
# 50x4aPpO4dhEmSUVpbixNDmv6TvuIHv1xIs174bNGO/ilWMm+Jx5boAXrJxagRhH
# QtiFprSjMktTliL4sKZyt2i+SXncM23gRezzsoOiBhv14YSd1Klnlkzvgs29XNjT
# +c8hIfPRe9rvVCMPiH7zPZcw5nNjthDQ+zD563I1nUJ6y59TbXWsuyUsqw7wXZoG
# zZwijWT5oc6GvD3HDokJY401uhnj3ubBhbkR83RbfMvmzdp3he2bvIUztSOuFzRq
# rLfEvsPkVHYnvH1wtYyrt5vShiKheGpXa2AWpsod4OJyT4/y0dggWi8g/tgbhmQl
# ZqDUf3UqUQsZaLdIu/XSjgoZqDjamzCPJtOLi2hBwL+KsCh0Nbwc21f5xvPSwym0
# Ukr4o5sCcMUcSy6TEP7uMV8RX0eH/4JLEpGyae6Ki8JYg5v4fsNGif1OXHJ2IWG+
# 7zyjTDfkmQ1snFOTgyEX8qBpefQbF0fx6URrYiarjmBprwP6ZObwtZXJ23jK3Fg/
# 9uqM3j0P01nzVygTppBabzxPAh/hHhhls6kwo3QLJ6No803jUsZcd4JQxiYHHc+Q
# /wAMcPUnYKv/q2O444LO1+n6j01z5mggCSlRwD9faBIySAcA9S8h22hIAcRQqIGE
# jolCK9F6nK9ZyX4lhthsGHumaABdWzCCB5cwggV/oAMCAQICEzMAAABeLhFuHHac
# 7wUAAAAAAF4wDQYJKoZIhvcNAQEMBQAwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoT
# FU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1Ymxp
# YyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwHhcNMjYwOTE3MTg1NzIzWhcNMjcw
# OTE0MTg1NzIzWjCB2zELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24x
# EDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
# bjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UE
# CxMeblNoaWVsZCBUU1MgRVNOOjc4MDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNy
# b3NvZnQgUHVibGljIFJTQSBUaW1lIFN0YW1waW5nIEF1dGhvcml0eTCCAiIwDQYJ
# KoZIhvcNAQEBBQADggIPADCCAgoCggIBANYLzn+RiTrerYIgiJrGpdKrEzaWWcXT
# 2dAYG6E31aRl+HIkXj9Zazqhr90el4ioB6sXJKdqapzUSev3pnyDV94ncYrnrrTm
# ETGuhnssH/l4SH7g2yzgb1or1QMCYKl6vXsY6aOD542hdEqwGirvhAn+ZC/4tIea
# TliECBwiN1yVX/N+WQoZJi77gLyEODmpQB2hWKyhfDzZqpnOC36+jGNLdQXCgrU5
# LlSJSj+lTIyfHdr7lWZyE9xWkyyUIs3AAQtTgG92UfDD0AAg2H4FDrlfuVe/egzD
# u8kqS+DlaDHVXB+5hj+S1PnGOG9DlwVJVkUUA8OhdQ7i4xSJK8IDrZ1KsC5lXj35
# nfV8la9yTQK8dmFDlYYOdcGdCiZpdX7J7HxZIZaqP4cdjQn87zv9p+yH5bonjFU5
# pQIiJ9ZhS2dEYHYMPWJJyu8z3XliL/DdBgZivHeMp9OV6KczJwH0re8V1Xo5NmrX
# F3RrVQYtCKCRza/2ieuRt0PYkloaManzMG6a0d9ahxhDtiE11aQhXrll/daZmHhB
# ieScM97eYT4e+k0K3rAV96HfU6sVtGXip5LvJ3c7+PBDMkJYfQl/y/paM5nhW9wS
# EIAT8gYdGPRqKzolYdam9Gvst8XmHXkQbl6uUbMCxwA3U2CeN9vIh5Kval+7dBZs
# Q46Ls4jhpkBlAgMBAAGjggHLMIIBxzAdBgNVHQ4EFgQUkJ+O/e1ckFZIWyr7+tqw
# FespRu4wHwYDVR0jBBgwFoAUa2koOjUvSGNAz3vYr0npPtk92yEwbAYDVR0fBGUw
# YzBhoF+gXYZbaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWlj
# cm9zb2Z0JTIwUHVibGljJTIwUlNBJTIwVGltZXN0YW1waW5nJTIwQ0ElMjAyMDIw
# LmNybDB5BggrBgEFBQcBAQRtMGswaQYIKwYBBQUHMAKGXWh0dHA6Ly93d3cubWlj
# cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwUHVibGljJTIwUlNB
# JTIwVGltZXN0YW1waW5nJTIwQ0ElMjAyMDIwLmNydDAMBgNVHRMBAf8EAjAAMBYG
# A1UdJQEB/wQMMAoGCCsGAQUFBwMIMA4GA1UdDwEB/wQEAwIHgDBmBgNVHSAEXzBd
# MFEGDCsGAQQBgjdMg30BATBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wCAYGZ4EMAQQCMA0G
# CSqGSIb3DQEBDAUAA4ICAQBFu/ZT4cvj4lM2inZbiG/mZywlkPodAKAk3ofp/uBe
# /Az6gmCAK5S3repNjtHt+cJpGGT1WDIJkrC0HhzcgTURxBWiHuwLrdArwPlIqQgH
# GNV0Ku6Y1UjsquXBMuT5fTVavpwqhDOgDdNDpxibvsN4IuY6AS2BhtJQo5y23Ois
# 6RUzzdyUbEdDql8Q8uG+KjkPHX9mBgdcszH2GCjvO5GONE+SpVFDUC/QiSj0LaPd
# 1mEPBBYSPoah6uxvcvTcBf+wY4s2bu24WyaF+Avcm3wP7CI3wkgYbpVmINM7BH0c
# 7oOTMlm/5WZXNflehbgPD4n1g6lNeTItfFEVf7j7aW/keozG7zoBvO9R3EWY6bJZ
# +dmpky3/1OdunEQsaeTHso4MNNQ0ODWBNeKHd8PEWEeihN4WxufOTlu9J5luUQ8H
# O1FmVdClL07+6aYDWZ+ROTssgzv4QNeg3J3xr1SdR7Oou3l6CCFL4AH6iTkjN1Fw
# R9q67JFfmq53iaiEWJ7Bo3XCpoTM+WaQPMjixJA4Xx1+G6u06EepTNi6ZJji3OW9
# KB2oQZdWoqaTevZRp6OoyDb2xPhQtKR9IDrttsDH6W0E0lW7n1sihsvRAIw3zyv6
# fw8D7GxmkP0VWtkSCyzXBbaR0RYXHEgUgnVYwXUwAa+YcudXxwJ+16JlrnnC7vSD
# AzGCB0Mwggc/AgEBMHgwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZXN0YW1waW5nIENBIDIwMjACEzMAAABeLhFuHHac7wUAAAAAAF4wDQYJYIZIAWUD
# BAIBBQCgggScMBEGCyqGSIb3DQEJEAIPMQIFADAaBgkqhkiG9w0BCQMxDQYLKoZI
# hvcNAQkQAQQwHAYJKoZIhvcNAQkFMQ8XDTI2MTExODIxNTQ1MFowLwYJKoZIhvcN
# AQkEMSIEIIgLnMROoMtwn/vFAeVzzOrBWw/1Q8OAveMhP+2okdhYMIG5BgsqhkiG
# 9w0BCRACLzGBqTCBpjCBozCBoAQg2fVmzDuHRgytmXR5bVL7Mwh0CGqqos+3kDbm
# XlBX7qMwfDBlpGMwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBD
# b3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZXN0
# YW1waW5nIENBIDIwMjACEzMAAABeLhFuHHac7wUAAAAAAF4wggNeBgsqhkiG9w0B
# CRACEjGCA00wggNJoYIDRTCCA0EwggIpAgEBMIIBCaGB4aSB3jCB2zELMAkGA1UE
# BhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
# BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0
# IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjc4
# MDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# IFN0YW1waW5nIEF1dGhvcml0eaIjCgEBMAcGBSsOAwIaAxUA3q4d2bA8fHDYjXY/
# C+auNvH/6AWgZzBlpGMwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZXN0YW1waW5nIENBIDIwMjAwDQYJKoZIhvcNAQELBQACBQDuqBMmMCIYDzIwMjYx
# MTE4MTIwNTU4WhgPMjAyNjExMTkxMjA1NThaMHQwOgYKKwYBBAGEWQoEATEsMCow
# CgIFAO6oEyYCAQAwBwIBAAICHOYwBwIBAAICE3kwCgIFAO6pZKYCAQAwNgYKKwYB
# BAGEWQoEAjEoMCYwDAYKKwYBBAGEWQoDAqAKMAgCAQACAwehIKEKMAgCAQACAwGG
# oDANBgkqhkiG9w0BAQsFAAOCAQEAgYo9VLJXkxwz3mPN5dQ4TnftdatI0dSaFkFe
# FIizk0l5HieeiyN/95RlA01fO/NYZX+dmWQzGAlVM5XlOs+6JDiZYK62JOBt4egt
# mpYqIkl7Ghxrmi9tt6RB/sd9kqrsMC7B6QJqV88mK2jkoedcMH6ABTlgkJ0ysPQ0
# KBUAy3+H4X6XFMymMIaMYPZRsaVCPgIKrBBTcBWkVm68pffGkvMBb4rlbQUnv90l
# ypXzzndMEipidtydTlKmKESNK+tE1cz9dgCD4cBA+H15EEGMdsWWZVbwE//JAOOl
# Rosond6SLcs0bsZEbN6gXPQ1xfJgkXYdOVZRy9ORNu1zPLxv7DANBgkqhkiG9w0B
# AQEFAASCAgDMOVLJuhdGy1Qk2NqDc0wEDgE+OYMEwz2Rvv1Xl3Gx9HVMK2vNFXno
# C5vvXiXGGfGZfp+Oei3p3DJkgOZycQ4y0LlRmi/EAKokQFcloouWvZzdDDYUHFcI
# F55zrcZp7GaxnksLQz1rw0DGNfIE393BLLW5/Samy5tdQiHb+8Q21gplIu2rPHUp
# /TyXsF6I7kNbzpBCnnnh7ZznBGM3fuOIvCnypCz+gIh2FdSikvaNYbXgookYHtML
# ehJ4b+bBhgIMPCpHqTWr0JJNSVhsqWiBKy8TO0E4Ogja6oWLUGJubUmWIfj4Htyb
# t6JiuB9b8gxyoSJ9idTCsmFk8P6+2tUOpi9CKBi6KoqsUTsSLOijAZqL2+t84Kwc
# iqMi1E9Ft3fE1tUf5+QW0hVh8JJW8D+KCi/5Jzcu8RmmE15xX5FhxaY8x93Td3D4
# 8vZ4Tvdvj054Imypg/qKC7y4VoLX+rfdaW5eVqQZUS36QuC6VQMMJW1RUW+b/Apg
# kEspV6WQDM4iBGzEoqp3QyXvlgBeM4xojP7SmW6BG7ZOLu2/yifxZhNTKoAV86ax
# 0Tu6+u2XlGPk4LvSUGZG6ABDSrBx/RxlyJILOVGxi9k0x1UVqAfo9DOgUqx/o31q
# vLArHEGuXsoZH1A4VUuyjcHHfWqpa06mTc7kK+4+WHjgDyFhawkvHA==
# SIG # End signature block
