# Data-only translation checks. Run with Windows PowerShell 5 after signing.
# Loads the signed application as a library. The only sockets are ephemeral
# loopback fixtures in this process; no real snip, public server, form, native
# capture, clipboard, user preference, or translation-service account is used.
param([string]$ReleaseFolder = 'release-0.4.1-signed')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5 for the .NET Framework application.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run with powershell.exe -STA -NoProfile -File checks\Verify-Translation.ps1.' }
$project = Split-Path $PSScriptRoot -Parent
$app = Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw 'The signed release application is missing.' }
$signature = Get-AuthenticodeSignature -LiteralPath $app
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'A valid Automation Forge LLC application signature is required.' }
if ($ReleaseFolder -notmatch '\Arelease-(\d+\.\d+\.\d+)-signed\z') { throw 'Choose a versioned signed release folder.' }
$expectedVersion = $Matches[1] + '.0'
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $expectedVersion) { throw "Expected application file version $expectedVersion." }
Add-Type -AssemblyName System.Net.Http
$assembly = [Reflection.Assembly]::LoadFrom($app)
$serviceType = $assembly.GetType('SnipStudio.LibreTranslateService', $true)
$urlMethod = $serviceType.GetMethod('ValidateBaseUri', [Reflection.BindingFlags]'Public,Static')
$constructor = $serviceType.GetConstructor([type[]]@([string]))
$timedConstructor = $serviceType.GetConstructor([Reflection.BindingFlags]'NonPublic,Instance', $null,
    [type[]]@([string],[Net.Http.HttpMessageHandler],[TimeSpan]), $null)
if ($null -eq $urlMethod -or $null -eq $constructor -or $null -eq $timedConstructor) { throw 'Required translation test contract is missing.' }
$checks = New-Object 'System.Collections.Generic.List[string]'
$utf8 = New-Object Text.UTF8Encoding($false)
function Confirm-Snip([bool]$Value, [string]$Label) {
    if (-not $Value) { throw "FAILED: $Label" }
    $checks.Add($Label)
}
function Unwrap-Error([Exception]$ErrorValue) {
    while (($ErrorValue -is [Reflection.TargetInvocationException] -or $ErrorValue -is [AggregateException] -or
        $ErrorValue -is [Management.Automation.MethodInvocationException]) -and $null -ne $ErrorValue.InnerException) {
        $ErrorValue = $ErrorValue.InnerException
    }
    return $ErrorValue
}
function Get-TaskError($Task, [int]$MaximumMilliseconds = 5000) {
    try {
        try { $done = $Task.Wait($MaximumMilliseconds) } catch [AggregateException] { $done = $Task.IsCompleted }
        if (-not $done) { throw 'The fixture exceeded its bounded wait.' }
        $null = $Task.GetAwaiter().GetResult()
        return $null
    } catch { return (Unwrap-Error $_.Exception) }
}
function Rejects-Endpoint([string]$Value) {
    try { $null = $urlMethod.Invoke($null,[object[]]@($Value)); return $false } catch { return $true }
}
function Invoke-Fixture {
    param([scriptblock]$StartRequest, [string]$ResponseBody = '{}', [int]$Status = 200,
        [string]$Prefix = '', [string]$Mode = 'Respond', [int]$TimeoutMilliseconds = 5000,
        [string]$ExtraHeaders = '', [Nullable[int]]$DeclaredLength = $null)
    $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0)
    $connection = $null; $service = $null; $stream = $null
    $cancel = New-Object Threading.CancellationTokenSource
    $listener.Start()
    try {
        $port = $listener.LocalEndpoint.Port
        $base = 'http://127.0.0.1:' + $port + $Prefix
        $service = $timedConstructor.Invoke([object[]]@($base,$null,[TimeSpan]::FromMilliseconds($TimeoutMilliseconds)))
        # Wrap Task in an object at the caller so PowerShell cannot enumerate it.
        $pending = & $StartRequest $service $cancel.Token
        $work = $pending.Task
        if ($null -eq $work) { throw 'The request fixture did not return a task.' }
        $accept = $listener.AcceptTcpClientAsync()
        if (-not $accept.Wait(5000)) {
            $requestError = Get-TaskError $work 100
            if ($null -ne $requestError) { throw $requestError }
            throw 'The client did not connect to its loopback fixture.'
        }
        $connection = $accept.GetAwaiter().GetResult()
        $stream = $connection.GetStream()
        $stream.ReadTimeout = 5000; $stream.WriteTimeout = 3000
        $headerBytes = New-Object 'System.Collections.Generic.List[byte]'
        $terminator = 0
        while ($terminator -lt 4) {
            $b = $stream.ReadByte()
            if ($b -lt 0 -or $headerBytes.Count -ge 16384) { throw 'Invalid fixture request headers.' }
            $headerBytes.Add([byte]$b)
            $wanted = @(13,10,13,10)[$terminator]
            if ($b -eq $wanted) { $terminator++ } elseif ($b -eq 13) { $terminator=1 } else { $terminator=0 }
        }
        $headers = [Text.Encoding]::ASCII.GetString($headerBytes.ToArray())
        if ($headers -match '(?im)^Expect:\s*100-continue\s*$') {
            $continue = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 100 Continue`r`n`r`n")
            $stream.Write($continue,0,$continue.Length)
        }
        $length = 0
        if ($headers -match '(?im)^Content-Length:\s*(\d+)\s*$') { $length=[int]$Matches[1] }
        if ($length -gt 131072) { throw 'Unexpectedly large fixture request.' }
        $requestBytes = New-Object byte[] $length
        $read=0
        while ($read -lt $length) {
            $amount=$stream.Read($requestBytes,$read,$length-$read)
            if ($amount -le 0) { throw 'The fixture request body ended early.' }
            $read+=$amount
        }
        $requestText = $utf8.GetString($requestBytes)
        if ($Mode -eq 'Cancel') { $cancel.Cancel() }
        elseif ($Mode -ne 'Timeout') {
            $responseBytes = $utf8.GetBytes($ResponseBody)
            $contentLength = $responseBytes.Length
            if ($null -ne $DeclaredLength) { $contentLength = [int]$DeclaredLength }
            $lengthHeader="Content-Length: $contentLength`r`n"
            if ($Mode -eq 'OmitLength') { $lengthHeader='' }
            $responseHeader = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 $Status Fixture`r`nContent-Type: application/json; charset=utf-8`r`n${lengthHeader}Connection: close`r`n$ExtraHeaders`r`n")
            $stream.Write($responseHeader,0,$responseHeader.Length)
            try {
                if ($responseBytes.Length -gt 0) { $stream.Write($responseBytes,0,$responseBytes.Length) }
                $stream.Flush()
            } catch [IO.IOException] { if ($Mode -ne 'OmitLength') { throw } }
            if ($Mode -eq 'OmitLength') { $stream.Dispose(); $connection.Close() }
            if ($Mode -eq 'BodyCancel') { $cancel.CancelAfter(100) }
        }
        $result=$null; $errorValue=$null
        try {
            try { $done=$work.Wait(6000) } catch [AggregateException] { $done=$work.IsCompleted }
            if (-not $done) { throw 'The translation request exceeded its bounded fixture wait.' }
            $result=$work.GetAwaiter().GetResult()
        } catch { $errorValue=Unwrap-Error $_.Exception }
        return [pscustomobject]@{ Result=$result; Error=$errorValue; Headers=$headers; Body=$requestText }
    } finally {
        $cancel.Cancel()
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $connection) { $connection.Close() }
        $listener.Stop()
        if ($null -ne $service) { $service.Dispose() }
        $cancel.Dispose()
    }
}
function Has-Kind($Fixture, [string]$Kind) {
    return $null -ne $Fixture.Error -and [string]$Fixture.Error.Kind -eq $Kind
}

$watch=[Diagnostics.Stopwatch]::StartNew()
$local=$urlMethod.Invoke($null,[object[]]@('http://127.0.0.1:5000'))
Confirm-Snip ($local.IsLoopback -and $local.Scheme -eq 'http') 'Local HTTP translation is accepted without requiring an account.'
Confirm-Snip (-not (Rejects-Endpoint 'http://localhost:5000/prefix/')) 'Localhost with a server path prefix is accepted.'
Confirm-Snip (-not (Rejects-Endpoint 'http://[::1]:5000/')) 'IPv6 loopback HTTP is accepted.'
Confirm-Snip (-not (Rejects-Endpoint 'https://translate.example.test/prefix/')) 'Remote translation requires and accepts HTTPS.'
foreach ($bad in @('http://translate.example.test/','ftp://localhost/','/relative',
    'https://user:password@example.test/','https://example.test/?secret=value','https://example.test/#fragment')) {
    Confirm-Snip (Rejects-Endpoint $bad) 'Unsafe, ambiguous, or non-HTTP endpoint is rejected.'
}

$languages='[{"code":"en","name":"English","targets":["es","ar"]},{"code":"es","name":"Spanish","targets":["en"]},{"code":"ar","name":"Arabic","targets":["en"]}]'
$fixture=Invoke-Fixture -Prefix '/translator/v1' -ResponseBody $languages -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.GetLanguagesAsync($token)} }
Confirm-Snip ($null -eq $fixture.Error -and @($fixture.Result).Count -eq 3) 'The client reads the server language list.'
Confirm-Snip ($fixture.Headers.StartsWith('GET /translator/v1/languages ')) 'Language discovery retains a configured server path prefix.'
$english=@($fixture.Result | Where-Object { $_.Code -ceq 'en' })[0]
$arabic=@($fixture.Result | Where-Object { $_.Code -ceq 'ar' })[0]
Confirm-Snip ($english.Targets.Count -eq 2 -and $english.Targets.Contains('es')) 'Server-supported target language pairs are retained.'
Confirm-Snip ($arabic.IsRightToLeft) 'Arabic is exposed as right-to-left for the native result view.'

$source='Part "00123" & caf' + [char]0x00e9 + "`r`n" + [char]0x4e2d + [char]0x6587
$answer='Hola, caf' + [char]0x00e9 + "`n" + [char]0x4e2d + [char]0x6587
$response=@{translatedText=$answer;detectedLanguage=@{language='en';confidence=98.5}} | ConvertTo-Json -Compress
$fixture=Invoke-Fixture -Prefix '/translator/v1/' -ResponseBody $response -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync($source,'','es',$token)} }
$sent=$fixture.Body | ConvertFrom-Json
Confirm-Snip ($null -eq $fixture.Error -and $fixture.Result.Text -ceq $answer) 'Translated Unicode and line breaks are preserved exactly.'
Confirm-Snip ($fixture.Headers.StartsWith('POST /translator/v1/translate ')) 'Translation retains a configured server path prefix.'
Confirm-Snip ($sent.q -ceq $source -and $sent.source -ceq 'auto' -and $sent.target -ceq 'es' -and $sent.format -ceq 'text') 'The JSON request preserves the source and explicitly requests plain-text automatic-language translation.'
Confirm-Snip ($null -eq $sent.api_key) 'Free translation requests do not add an account key.'
Confirm-Snip ($fixture.Result.SourceCode -ceq 'en' -and $fixture.Result.TargetCode -ceq 'es' -and $fixture.Result.ProviderName -eq 'LibreTranslate') 'The result identifies its detected language, target, and open-source provider.'
$fixture=Invoke-Fixture -ResponseBody '{"translatedText":"Hola"}' -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
$sent=$fixture.Body | ConvertFrom-Json
Confirm-Snip ($null -eq $fixture.Error -and $sent.source -ceq 'en' -and $fixture.Result.SourceCode -ceq 'en') 'An explicitly selected source language is retained without fabricated detection.'

$echo='FIXTURE-DO-NOT-ECHO-00123'
foreach ($item in @(@{Status=403;Kind='KeyRequired'},@{Status=429;Kind='RateLimited'},@{Status=503;Kind='ServiceUnavailable'})) {
    $fixture=Invoke-Fixture -Status $item.Status -ResponseBody ('{"error":"' + $echo + '"}') -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync($echo,'auto','es',$token)} }
    Confirm-Snip (Has-Kind $fixture $item.Kind) 'Server policy, rate-limit, or availability failures have a useful error category.'
    Confirm-Snip (-not $fixture.Error.ToString().Contains($echo)) 'Server error bodies cannot echo recognized text into the application error.'
}
$fixture=Invoke-Fixture -Status 307 -ExtraHeaders "Location: http://127.0.0.1:1/should-not-be-followed`r`n" -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync($echo,'auto','es',$token)} }
Confirm-Snip ($null -ne $fixture.Error -and [string]$fixture.Error.Kind -ne 'ConnectionFailed') 'An HTTP redirect is rejected rather than followed to a different server.'
foreach ($body in @('{not-json','{"unrelated":"value"}','{"translatedText":["unexpected","array"]}')) {
    $fixture=Invoke-Fixture -ResponseBody $body -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
    Confirm-Snip (Has-Kind $fixture 'InvalidResponse') 'Malformed or structurally invalid translation output is rejected.'
}
$fixture=Invoke-Fixture -DeclaredLength 1048577 -ResponseBody '' -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip (Has-Kind $fixture 'ResponseTooLarge') 'An oversized response is rejected before downloading its declared body.'
$oversized='"' + ('x' * 1048576) + '"'
$fixture=Invoke-Fixture -Mode OmitLength -ResponseBody $oversized -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip (Has-Kind $fixture 'ResponseTooLarge') 'A response without Content-Length is still bounded while streaming.'
$fixture=Invoke-Fixture -Mode Cancel -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip ($fixture.Error -is [OperationCanceledException]) 'Cancel aborts an in-flight translation request.'
$fixture=Invoke-Fixture -Mode BodyCancel -DeclaredLength 128 -ResponseBody '' -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip ($fixture.Error -is [OperationCanceledException]) 'Cancel also aborts a response body that stops arriving.'
$fixture=Invoke-Fixture -Mode Timeout -TimeoutMilliseconds 350 -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip (Has-Kind $fixture 'TimedOut') 'A server that does not answer produces a bounded timeout.'
$fixture=Invoke-Fixture -Mode BodyTimeout -DeclaredLength 128 -ResponseBody '' -TimeoutMilliseconds 350 -StartRequest { param($client,$token) [pscustomobject]@{Task=$client.TranslateAsync('Hello','en','es',$token)} }
Confirm-Snip (Has-Kind $fixture 'TimedOut') 'A stalled response body also produces a bounded timeout.'

$service=$constructor.Invoke([object[]]@('http://127.0.0.1:1/'))
try {
    foreach ($badInput in @(@{Text='';From='auto';To='es'},@{Text=('x' * 16001);From='auto';To='es'},
        @{Text='Hello';From='en';To='../../es'},@{Text='Hello';From="en`r`nInjected:yes";To='es'})) {
        $errorValue=$null
        try { $task=$service.TranslateAsync($badInput.Text,$badInput.From,$badInput.To,[Threading.CancellationToken]::None); $errorValue=Get-TaskError $task }
        catch { $errorValue=Unwrap-Error $_.Exception }
        Confirm-Snip ($null -ne $errorValue -and ([string]$errorValue.Kind -eq 'InvalidRequest' -or $errorValue -is [ArgumentException])) 'Empty, oversized, or invalid-language input is refused before contacting a server.'
    }
} finally { $service.Dispose() }
$service=$constructor.Invoke([object[]]@('http://127.0.0.1:1/'))
$alreadyCanceled=New-Object Threading.CancellationTokenSource
try {
    $alreadyCanceled.Cancel()
    $errorValue=$null
    try { $task=$service.TranslateAsync('Hello','en','es',$alreadyCanceled.Token); $errorValue=Get-TaskError $task }
    catch { $errorValue=Unwrap-Error $_.Exception }
    Confirm-Snip ($errorValue -is [OperationCanceledException]) 'A pre-canceled request is refused before contacting a server.'
} finally { $service.Dispose(); $alreadyCanceled.Dispose() }
$watch.Stop()
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output) | Out-Null
$report=[pscustomobject]@{ ApplicationVersion=$expectedVersion; CheckedAtUtc=[DateTime]::UtcNow.ToString('o');
    CheckCount=$checks.Count; Checks=@($checks); ElapsedMilliseconds=$watch.ElapsedMilliseconds;
    Scope='Signed library, synthetic loopback HTTP fixtures only. No real translation engine, public server, app UI, snip, clipboard, or user preferences tested.' }
[IO.File]::WriteAllText((Join-Path $output 'translation-checks.json'),($report | ConvertTo-Json -Depth 5),$utf8)
Write-Output ("Passed {0} translation client checks in {1} ms. Real engine and UI remain unverified." -f $checks.Count,$watch.ElapsedMilliseconds)

# SIG # Begin signature block
# MII2tQYJKoZIhvcNAQcCoII2pjCCNqICAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCBKoXKeApDBoXOH
# ud6HyUziXEBhyTyeQbBecbHrtR/iWKCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# MC8GCSqGSIb3DQEJBDEiBCBJ+WriBXSOpcg+YEmEqZdib9U645i87XgZ9MC924TL
# bTANBgkqhkiG9w0BAQEFAASCAYA7xQURq0HOBq/oNARSiApAl2nDcO5GCqOGC+xV
# s+5CRpxb2hLgoNLUtKumLlqRyKUKo5e/cyrh95Ow7jG1sFoV71TWzuNy+RSFk3Fr
# +EOBx0DbO9Q/17bTV+ojwdEgtIZs3+AY9HpdBTZeHKKy8ceP1un2iSRLtgLbWRG7
# 9YAOunZOH29Xfyu7IVXT0rn2vf+dwypEWhPVWoIKJEkg9s9pEx8WnGAHBJuHkFV3
# qi9PCmuK9U6lS7+TI1k8sZVy6nAzY9e3SjiK0Tv/den4DoeQPbOF4wn5tHzzmqLX
# ENcIXIWr8NHctQgMXTIs+CkT9kD8rmIbz0mE0Pxk+sZDZrXmBMK89AmUJP2LuQVX
# pL1TYoycHZkiEKF2lvSbjDroPS1Yq44TAPIl6PZjR1LvuHONuDCUhbtSfDZ6etTY
# bM/e0eRwor/o8xxZXvSa1bjUUJ2IaLFEQoX1jT4wpRbiy7viB9fpEsVsj/zAHGS1
# L4LFsbl7TMOT24+LTx6/yObnqMChghgRMIIYDQYKKwYBBAGCNwMDATGCF/0wghf5
# BgkqhkiG9w0BBwKgghfqMIIX5gIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgBHzSpspgQykfJQpJBk1NE9IbA7vFuk1Np1+xyGq6nlUCBmr86+YTnBgT
# MjAyNjExMTgyMTMxMDAuODUxWjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
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
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMTMxMDBaMC8GCSqGSIb3
# DQEJBDEiBCAhh75A5u8H6dJSpMZW0ZqRPUiKa99pczDJbe8lh/hSFjCBuQYLKoZI
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
# AQEBBQAEggIAxjinCnQ25aPLfx8OUpM8WWF/WRnL806OHGr0cvEvyIQReQnz4LMQ
# JKbrF8L1v7sW4xB8QOuxBHmTiaenVyfOpxypNPcGQHXdw+M4lS6tYZPYndp9QviM
# 87EW2n60/eOL1y9LW0Waq8eWcQD7L9em4MosNMQKnXu2jOFudMaJdkSDq/PeIgIf
# /Tnzt0KlKWUnMD6mTTOIsWMEVkyEVlj7QsX2TBgq4lXX0KSgNTxb/o1wxq69HYLn
# G1MI/VJofF9+0rxJs9iaZvQGrBga3pqSTuw8DSJiS4k3E5UtbEuKsN9ILdHS/+c5
# 99+e9XrwruhXwpjZBLjoZJGDhocTp5QAMvHiS+m3A/vzsSfupEIwpeZbPuwamRHs
# emfFHW8iREnjpEjghKRRtMnQvd3TiALBBOEIkq45wce06gasdz3XnXBpM8ECYvmk
# gSt+CaB12TDffhuFP0tgpMBvXUGRWHKpGap4Cxlazel/OkyxKs7/5BiDcBXF/nIN
# mCxhDDD3uzsezHfE5bQVb3bgMkhwCjepwwxXmWTrykYysBSqibRU2RTLPaS7nyV3
# 2s8kugLHnIPMofLHLz2lzLFlo68iksL89m3FD22kGsc+KNkLQeH11fglV0y/0+1k
# xNzX+XDbqTZs7HQf8Mjfv7QRwPsP/zMltJDbTGho4arHfZBmTA8wfQQ=
# SIG # End signature block
