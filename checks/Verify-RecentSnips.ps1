# Signed-library pure model checks. No app entrypoint, Windows Recent reading,
# Shell/COM calls, background worker, shown form, capture, input or preferences.
param([string]$ReleaseFolder='release-0.6.4-signed',[string]$ExpectedVersion='0.6.4.0',[Parameter(Mandatory=$true)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
if($ExpectedSha256 -notmatch '\A[0-9A-Fa-f]{64}\z'){throw 'An exact signed app SHA256 is required.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
$actualSha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
if($actualSha256 -ine $ExpectedSha256){throw 'The signed app does not match the expected SHA256.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$type=$assembly.GetType('SnipStudio.RecentSnipService',$true)
$taskbar=$assembly.GetType('SnipStudio.TaskbarJumpList',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Recent([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Call-Recent([string]$Name,[object[]]$Arguments){
    for($index=0;$index -lt $Arguments.Length;$index++){if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}}
    return ,$type.GetMethod($Name,$flags).Invoke($null,$Arguments)
}
function Property-Recent($Object,[string]$Name){return ,$Object.GetType().GetProperty($Name,$flags).GetValue($Object,$null)}
function Model-Recent([string[]]$Paths,[int]$Limit,[Threading.CancellationToken]$Token=[Threading.CancellationToken]::None){
    $arguments=New-Object object[] 3;$arguments[0]=$Paths;$arguments[1]=$Limit;$arguments[2]=$Token
    return Call-Recent 'CreateRecentResult' $arguments
}
function Link-Recent([string]$Target,[string]$Arguments,[string]$Executable){
    $values=New-Object object[] 4;$values[0]=$Target;$values[1]=$Arguments;$values[2]=$Executable
    $accepted=$type.GetMethod('TryGetOwnedLinkImagePath',$flags).Invoke($null,$values)
    return [pscustomobject]@{Accepted=[bool]$accepted;Path=$values[3]}
}
function Canceled-Recent($ErrorRecord){
    $exception=$ErrorRecord.Exception
    while($null -ne $exception){if($exception -is [OperationCanceledException]){return $true};$exception=$exception.InnerException};return $false
}
# PowerShell's property adapter can turn a disposed Width getter into null.
# A real method call exposes the GDI+ disposed-object exception reliably.
function Disposed-Recent([Drawing.Bitmap]$Bitmap){try{$null=$Bitmap.GetPixel(0,0);return $false}catch{return $true}}

$none=[Threading.CancellationToken]::None
Confirm-Recent (-not $type.GetField('workerRunning',$flags).GetValue($null) -and $type.GetField('pending',$flags).GetValue($null).Count -eq 0) 'Loading the service does not start workers or native work'
Confirm-Recent ((Call-Recent 'LimitItems' ([object[]]@([int]0))) -eq 1 -and (Call-Recent 'LimitItems' ([object[]]@([int]999))) -eq 32) 'Recent requests never ask Windows for an unbounded zero-count list'
Confirm-Recent ((Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]-1))) -eq 16 -and (Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]128))) -eq 128 -and (Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]99999))) -eq 640) 'Thumbnail allocation requests remain bounded from 16 through 640 pixels'
$empty=Model-Recent @() 32
Confirm-Recent ((Property-Recent $empty 'IsAvailable') -and (Property-Recent $empty 'Items').Count -eq 0 -and (Property-Recent $empty 'Message') -like '*Windows recent-history settings*') 'An empty list is available and explains Windows history control'
$paths=[string[]]@('C:\synthetic snips\0007.PNG','c:\SYNTHETIC SNIPS\0007.png','C:\synthetic snips\0008.jpg','\\synthetic-server\share\0009.tiff')
$result=Model-Recent $paths 32
$items=Property-Recent $result 'Items'
Confirm-Recent ($items.Count -eq 3 -and (Property-Recent $items[0] 'Path') -ceq $paths[0] -and (Property-Recent $items[1] 'Path') -ceq $paths[2]) 'Accepted paths preserve order, case and leading zeros while duplicates collapse case-insensitively'
Confirm-Recent ((Property-Recent $items[0] 'DisplayName') -ceq '0007.PNG' -and (Property-Recent $items[2] 'DisplayName') -ceq '0009.tiff') 'Display names come from accepted paths without reading files'
Confirm-Recent ($items.IsReadOnly) 'Consumers cannot mutate the service result list'
$invalid=[string[]]@('relative.png','C:\synthetic\run.exe','C:\synthetic\note.txt','C:\synthetic\image.png:stream','C:\synthetic\*.png','\\?\C:\synthetic\image.png',"C:\synthetic\bad`nname.png")
Confirm-Recent ((Property-Recent (Model-Recent $invalid 32) 'Items').Count -eq 0) 'Relative, unsupported, device, stream and injected paths are rejected'
$many=New-Object 'System.Collections.Generic.List[string]'
for($i=0;$i -lt 80;$i++){$many.Add(('C:\synthetic\{0:D4}.png' -f $i))}
Confirm-Recent ((Property-Recent (Model-Recent $many.ToArray() 999) 'Items').Count -eq 32 -and (Property-Recent (Model-Recent $many.ToArray() 3) 'Items').Count -eq 3) 'Both source scanning and returned rows obey the requested bound'
$rejected=New-Object 'System.Collections.Generic.List[string]'
for($i=0;$i -lt 32;$i++){$rejected.Add(('C:\synthetic\{0:D4}.txt' -f $i))}
$rejected.Add('C:\synthetic\after-limit.png')
Confirm-Recent ((Property-Recent (Model-Recent $rejected.ToArray() 32) 'Items').Count -eq 0) 'Rejected candidates cannot make enumeration scan beyond the first 32 source entries'
$exe='C:\synthetic app\Snip Studio.exe'
foreach($path in @('C:\synthetic snips\0007.PNG','\\synthetic-server\share\0009.tiff')){
    $quoted=$taskbar.GetMethod('QuoteArgument',$flags).Invoke($null,[object[]]@($path))
    $link=Link-Recent $exe $quoted 'c:\SYNTHETIC APP\Snip Studio.exe'
    Confirm-Recent ($link.Accepted -and $link.Path -ceq $path) 'Owned image links round-trip strict Windows quoting without resolving or launching a target'
}
foreach($arguments in @('"C:\synthetic\a.png" --capture','"C:\synthetic\a.png" "C:\synthetic\b.png"','C:\synthetic\a.png','"C:\synthetic\run.exe"','"relative.png"','"C:\synthetic\a.png" ')){
    Confirm-Recent (-not (Link-Recent $exe $arguments $exe).Accepted) 'Malformed quoting, extra actions and unsupported arguments cannot become recent image paths'
}
foreach($target in @('C:\foreign app\Snip Studio.exe','Snip Studio.exe','C:Snip Studio.exe','C:\synthetic app\not-an-app.png','\\?\C:\synthetic app\Snip Studio.exe')){
    Confirm-Recent (-not (Link-Recent $target '"C:\synthetic\a.png"' $exe).Accepted) 'Foreign, relative, device and non-executable link targets are rejected'
}

$cts=[Threading.CancellationTokenSource]::new()
try{
    $cts.Cancel();$caught=$false
    try{$null=Model-Recent $paths 32 $cts.Token}catch{$caught=Canceled-Recent $_}
    Confirm-Recent $caught 'Canceled model enumeration stops without producing a replacement list'
    $task=Call-Recent 'GetRecentAsync' ([object[]]@([int]32,$cts.Token))
    $thumbnailTask=Call-Recent 'LoadThumbnailAsync' ([object[]]@('C:\synthetic\never-read.png',[int]128,$cts.Token))
    Confirm-Recent ($task.IsCanceled -and $thumbnailTask.IsCanceled -and -not $type.GetField('workerRunning',$flags).GetValue($null)) 'Pre-canceled public requests finish without starting native work'
}finally{$cts.Dispose()}

# Exercise the actual request's ownership/cancellation code synchronously with
# synthetic delegates. No request is enqueued and no Shell operation exists.
$requestType=$type.GetNestedType('Request`1',[Reflection.BindingFlags]::NonPublic).MakeGenericType([Drawing.Bitmap])
$requestConstructor=$requestType.GetConstructors($flags)[0]
$execute=$requestType.GetMethod('Execute',$flags)
$owned=[Drawing.Bitmap]::new(2,2)
$operation=[Func[Drawing.Bitmap]]{$owned}
$request=$requestConstructor.Invoke([object[]]@($operation,$null,$none))
try{
    $null=$execute.Invoke($request,@());$task=Property-Recent $request 'Task'
    Confirm-Recent ($task.IsCompleted -and -not $task.IsCanceled -and [Object]::ReferenceEquals($task.Result,$owned) -and -not (Disposed-Recent $owned)) 'A successful thumbnail transfers exactly one live caller-owned bitmap'
}finally{$request.Dispose();$owned.Dispose()}
$cts=[Threading.CancellationTokenSource]::new();$owned=[Drawing.Bitmap]::new(2,2)
$operation=[Func[Drawing.Bitmap]]{$cts.Cancel();return $owned}
$request=$requestConstructor.Invoke([object[]]@($operation,$null,$cts.Token))
try{
    $null=$execute.Invoke($request,@());$task=Property-Recent $request 'Task'
    Confirm-Recent ($task.IsCanceled -and (Disposed-Recent $owned)) 'A bitmap produced after cancellation is disposed instead of leaked to an abandoned consumer'
}finally{$request.Dispose();$cts.Dispose();$owned.Dispose()}

$premultiplied=[byte[]]@(10,20,30,128,0,0,0,0)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]2,$premultiplied))
try{
    Confirm-Recent ($bitmap.Width -eq 1 -and $bitmap.Height -eq 2 -and $bitmap.GetPixel(0,0).A -eq 128 -and $bitmap.GetPixel(0,1).A -eq 0 -and $bitmap.HorizontalResolution -eq 96) 'Detached thumbnail pixels retain meaningful alpha, two-row orientation and explicit DPI'
    Confirm-Recent ($premultiplied[0] -eq 10 -and $premultiplied[3] -eq 128) 'Thumbnail conversion never changes caller input pixels'
}finally{$bitmap.Dispose()}
$legacy=[byte[]]@(30,20,10,0)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]1,$legacy))
try{Confirm-Recent ($bitmap.GetPixel(0,0).A -eq 255 -and $bitmap.GetPixel(0,0).R -eq 10) 'Legacy RGB thumbnails with unused alpha remain visible'}finally{$bitmap.Dispose()}
$straight=[byte[]]@(0,0,200,128)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]1,$straight))
try{Confirm-Recent ($bitmap.GetPixel(0,0).A -eq 128 -and [Math]::Abs($bitmap.GetPixel(0,0).R-200) -le 2) 'Detectable straight alpha converts to bounded premultiplied storage'}finally{$bitmap.Dispose()}
$caught=$false
try{$null=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]641,[int]1,[byte[]]@(0,0,0,255)))}catch{$caught=$true}
Confirm-Recent $caught 'Out-of-bound native bitmap dimensions cannot allocate a larger managed thumbnail'
Confirm-Recent (-not $type.GetField('workerRunning',$flags).GetValue($null) -and $type.GetField('pending',$flags).GetValue($null).Count -eq 0) 'All model checks leave the worker stopped and its queue empty'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=$actualSha256;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;WindowsRecentHistoryRead=$false;ShellOrCOMExecuted=$false;UserPreferencesReadOrWritten=$false;Scope='Signed-library pure recent-path policy, owned-link roundtrip and rejection, bounded DTOs and pixels, pre-cancellation and actual request bitmap ownership.';Unverified='Live Windows history and privacy configuration, native image-factory output and file-lock behavior, displayed Recent gallery, physical hover/open and installation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'recent-snips-model.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed recent-snips model checks.')

# SIG # Begin signature block
# MII2uAYJKoZIhvcNAQcCoII2qTCCNqUCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCCxAi6dOqtMKmlM
# IZTCXQ+fmXSE6dMNaR103uaE1j2glKCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# MC8GCSqGSIb3DQEJBDEiBCAHvFscck4Z7PxAan3UfQgTsO8f3TKCi/PjWVZQiEFm
# izANBgkqhkiG9w0BAQEFAASCAYBfq1IPZijDXMcVRzDplqpUbu56i99Q6haxa7cA
# IEdJGczHwrSnlmSR3kCB7veOIK9PEUaMKp54UmKWusWbUbjGPDNQMBRAAiUjssKc
# RfveHLoBVjOzC+pXTgp7S8zHenO+mbM/lOx5DcArpGrzTTz/t/kFVxm4vu+zbf5Q
# 4Q1V/UIQfpdVIcW9t/DqlFAPXeB+Yswc3iJdO/xfoVhY9fD2G16VhbOyNd38qmHR
# weYJnA5VbJDNn5DRI7w/WcSbHSvfWoeo/Lrr8v67a6K87vp07LEYG5VkhfwJCpW4
# TnWRwMGkrGqjeOknT/sYYaJRzQW/A81I8IBzPfdsCVUpm4sNl2grwVpEYsy9KLfw
# fDpMnin5H/LYNvVBktLFXoMujdOLFdnOA8hz4pRWwe9onmoHN2lne1IJcSpQ4h2a
# eSCdyUEAvhwsBxFtMXsGiDzCFy0nRdYKmr+GEHoFMCNQDWYRozxLLB3OMyUbkajn
# f0Rt3R6RsFq8ByDmV3KxSkHKTzChghgUMIIYEAYKKwYBBAGCNwMDATGCGAAwghf8
# BgkqhkiG9w0BBwKgghftMIIX6QIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgp8OBLelgae1qeOVq1C1dD7ftr216blqd0XitpDin0M4CBmr835JG9hgT
# MjAyNjExMTgyMTE3MTcuNDQ4WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
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
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMTE3MTdaMC8GCSqGSIb3
# DQEJBDEiBCBYx3SVIAbmSNT9gAxGiYSNLflZqezefsAV/zROJWmvLjCBuQYLKoZI
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
# hvcNAQEBBQAEggIAYj/H2xBSO9beZwca9aH/5CkB91xSK4jtiQSGr51B7aelXKCg
# CAcEyRdQqGJd5tK5G7mx8xLm6GBC4SMjN62051hyJVdUZaexQNtFXN3xHTgrzbE7
# Gji+9e8neMREP7mn9vMnOdjNZBrVSE6MisBAf4qArLxm7eLUrIzelfe3tFJK285g
# aTYh8aH29WTp2KQCO67DVQ+bGWEipnOkDYsZl4Wr12nFFPHzawa4bp5lg6Nr5Qe2
# djruawLyklRdlNnHXYCDXpdWe/atx/g7RSaD/xbDwK4sY58tvpYImbfXn2mtPT+R
# d5XXurRZkbu+gMW8GjI8OEK+/h9M4EAjt9M2KSBRkoi10RYEwIS7adhNMfk9Odyi
# FcDuLY843J2dxal6IYeJ10opkyLOXd0YWqjGvp00hYWq7bu/ESJk+ZgYX/hwPmyY
# LYnt2j5PplPCX0LGwpxL+8MJmoXbUttnekgTnjQCMILNiYAZ+wVpTzCMy6zT7SNg
# elTKdIBdAxemyrfRi04ZQ42MMx0+DA/B5NzmrhTGuNRkB/Xxeg5NYkHDKZLeThao
# sc4zaBfzZo6i7rY6z8AUEGMnTCL38SNs1tMO/X4tHsmx6LvNSUZbM7FwamCpOErM
# TFs5Q0NMXYUQGOC+uy8eHRBmICLM8Gm6UA5yn5Xas4H6E6/7KCyNEYlEiSo=
# SIG # End signature block
