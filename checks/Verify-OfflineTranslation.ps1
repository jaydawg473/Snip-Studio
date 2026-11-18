# Real offline-engine checks. Root runs only after assembling and verifying the
# bundled Node/Bergamot/models and signing the application. No UI, desktop,
# audio, clipboard, external translation server, or user preferences are used.
param([string]$ReleaseFolder = 'release-0.4.1-signed')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5 for the .NET Framework application.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run powershell.exe -STA -NoProfile -File checks\Verify-OfflineTranslation.ps1.' }
if ($ReleaseFolder -notmatch '\Arelease-(\d+\.\d+\.\d+)-signed\z') { throw 'Choose a versioned signed release folder.' }
$expectedVersion = $Matches[1] + '.0'
$project = Split-Path $PSScriptRoot -Parent
$release = Join-Path $project $ReleaseFolder
$app = Join-Path $release 'Snip Studio.exe'
$engineRoot = Join-Path $release 'translation'
$node = Join-Path $engineRoot 'runtime\node.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $app
if ($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'The application must have a valid, timestamped Automation Forge signature.' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $expectedVersion) { throw 'The signed application version does not match its release folder.' }
$nodeSignature = Get-AuthenticodeSignature -LiteralPath $node
if ($nodeSignature.Status -ne 'Valid' -or $null -eq $nodeSignature.TimeStamperCertificate -or $null -eq $nodeSignature.SignerCertificate -or $nodeSignature.SignerCertificate.Subject -notmatch '(?i)OpenJS|Node\.js') { throw 'Bundled Node must retain its valid timestamped original Node/OpenJS publisher signature.' }
$manifest = Get-Content -LiteralPath (Join-Path $engineRoot 'bundle.json') -Raw | ConvertFrom-Json
foreach ($relative in @('runtime/node.exe','worker.js')) {
    $pin = @($manifest.files | Where-Object { $_.path -ceq $relative })
    if ($pin.Count -ne 1 -or $pin[0].sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or
        $pin[0].sha256 -ine (Get-FileHash -LiteralPath (Join-Path $engineRoot $relative) -Algorithm SHA256).Hash) { throw ('Bundled execution file does not match its reviewed hash: ' + $relative) }
}
$assembly = [Reflection.Assembly]::LoadFrom($app)
$serviceType = $assembly.GetType('SnipStudio.OfflineTranslationService',$true)
$constructor = $serviceType.GetConstructor([type[]]@([string]))
$slotsField = $serviceType.GetField('EngineSlots',[Reflection.BindingFlags]'Static,NonPublic')
if ($null -eq $constructor -or $null -eq $slotsField) { throw 'The offline translation test contract is missing.' }
$output = Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output) | Out-Null
$checks = New-Object 'System.Collections.Generic.List[string]'
$notes = New-Object 'System.Collections.Generic.List[string]'
$translations = New-Object 'System.Collections.Generic.List[object]'
$total = [Diagnostics.Stopwatch]::StartNew()
$service = $null
$failure = $null
$cancellationExercised = $false
$canceledWorkerGone = $false

function Confirm-Snip([bool]$Value,[string]$Label) {
    if (-not $Value) { throw ('FAILED: ' + $Label) }
    $checks.Add($Label)
}
function Unwrap-OfflineError([Exception]$Value) {
    while (($Value -is [Reflection.TargetInvocationException] -or $Value -is [AggregateException] -or
        $Value -is [Management.Automation.MethodInvocationException]) -and $null -ne $Value.InnerException) { $Value=$Value.InnerException }
    return $Value
}
function Wait-OfflineTask($Task,[Threading.CancellationTokenSource]$Cancellation,[int]$MaximumMilliseconds=65000) {
    $remaining = 180000 - [int]$total.ElapsedMilliseconds
    if ($remaining -le 3000) { if ($null -ne $Cancellation) { $Cancellation.Cancel() }; throw 'The three-minute verification budget was exhausted.' }
    $limit = [Math]::Min($MaximumMilliseconds,$remaining-3000)
    try {
        try { $done=$Task.Wait($limit) } catch [AggregateException] { $done=$Task.IsCompleted }
        if (-not $done) {
            if ($null -ne $Cancellation) { $Cancellation.Cancel() }
            try { $null=$Task.Wait(3000) } catch [AggregateException] { }
            throw 'An offline-engine fixture exceeded its bounded wait.'
        }
        return $Task.GetAwaiter().GetResult()
    } catch { throw (Unwrap-OfflineError $_.Exception) }
}
function Get-OfflineTaskError($Task,[Threading.CancellationTokenSource]$Cancellation,[int]$MaximumMilliseconds=5000) {
    try { $null=Wait-OfflineTask $Task $Cancellation $MaximumMilliseconds; return $null }
    catch { return (Unwrap-OfflineError $_.Exception) }
}
function Get-BundledNodeIds {
    # Read-only observation, filtered to this exact reviewed runtime path. This
    # script never stops a process or searches for other apps' command lines.
    foreach ($candidate in @(Get-Process -Name node -ErrorAction SilentlyContinue)) {
        try { if ($candidate.MainModule.FileName -ieq $node) { $candidate.Id } }
        catch { }
    }
}
function Get-StillRunningObservedIds([int[]]$Ids) {
    foreach ($observedId in $Ids) {
        $observed=$null
        try { $observed=[Diagnostics.Process]::GetProcessById($observedId); if (-not $observed.HasExited) { $observedId } }
        catch [ArgumentException] { }
        catch { $observedId } # An unreadable known process is not proof of exit.
        finally { if ($null -ne $observed) { $observed.Dispose() } }
    }
}
function Invoke-SyntheticTranslation([string]$Text,[string]$From,[string]$To) {
    $cancel=New-Object Threading.CancellationTokenSource
    $watch=[Diagnostics.Stopwatch]::StartNew()
    try {
        $task=$service.TranslateAsync($Text,$From,$To,$cancel.Token)
        $result=Wait-OfflineTask $task $cancel
        if ($null -eq $result) { throw 'The real engine returned no translation result.' }
        $translations.Add([pscustomobject]@{Source=$From;Target=$To;Input=$Text;Output=$result.Text;
            Provider=$result.ProviderName;Milliseconds=$watch.Elapsed.TotalMilliseconds})
        return $result
    } finally { $cancel.Dispose() }
}

try {
    $service=$constructor.Invoke([object[]]@($engineRoot.PSObject.BaseObject))
    Confirm-Snip $service.RequiresExplicitSourceLanguage 'Offline translation honestly requires a chosen source language.'
    $cancel=New-Object Threading.CancellationTokenSource
    try { $languages=@(Wait-OfflineTask ($service.GetLanguagesAsync($cancel.Token)) $cancel 5000) }
    finally { $cancel.Dispose() }
    $codes=@($languages | ForEach-Object Code | Sort-Object)
    Confirm-Snip ($codes.Count -eq 4 -and ($codes -join ',') -ceq 'de,en,es,fr') 'The complete bundle offers German, English, Spanish, and French.'
    foreach ($language in $languages) {
        $expected=@($codes | Where-Object { $_ -cne $language.Code })
        $targets=@($language.Targets | Sort-Object)
        Confirm-Snip ($targets.Count -eq 3 -and ($targets -join ',') -ceq ($expected -join ',')) ('Installed targets include direct or English-pivot routes from ' + $language.Code + '.')
    }

    $invalid=@(
        @{Text='Hello';From='auto';To='de';Label='Unsupported automatic source detection is rejected before work starts.'},
        @{Text='Hello';From='';To='de';Label='A missing source language is rejected.'},
        @{Text='Hello';From='ja';To='en';Label='An uninstalled source language is rejected.'},
        @{Text='Hello';From='en';To='ja';Label='An uninstalled target language is rejected.'},
        @{Text='Hello';From='en';To='en';Label='An identical source and target are rejected.'},
        @{Text=' ';From='en';To='de';Label='Blank recognized text is rejected.'},
        @{Text=('x'*16001);From='en';To='de';Label='Oversized text is rejected without truncation.'},
        @{Text=([string][char]0xD800);From='en';To='de';Label='An incomplete Unicode surrogate is rejected.'}
    )
    foreach ($case in $invalid) {
        $cancel=New-Object Threading.CancellationTokenSource
        try {
            $errorValue=Get-OfflineTaskError ($service.TranslateAsync($case.Text,$case.From,$case.To,$cancel.Token)) $cancel
            Confirm-Snip ($null -ne $errorValue -and $errorValue.GetType().FullName -ceq 'SnipStudio.TranslationException' -and [string]$errorValue.Kind -ceq 'InvalidRequest') $case.Label
        } finally { $cancel.Dispose() }
    }
    $cancel=New-Object Threading.CancellationTokenSource
    try {
        $cancel.Cancel()
        $errorValue=Get-OfflineTaskError ($service.TranslateAsync('Hello','en','de',$cancel.Token)) $cancel
        Confirm-Snip ($errorValue -is [OperationCanceledException]) 'A pre-canceled request does not start translation.'
    } finally { $cancel.Dispose() }

    # All text below is fixed test data, including the user's one-word example.
    # These checks catch a broken engine or missing/wrong model; they do not
    # establish general translation quality or exact-punctuation fidelity.
    $fixtures=@(
        @{Text='Geschichte';From='de';To='en';Pattern='history|story|tale';Label='German to English translates the sample word Geschichte.'},
        @{Text='Good morning.';From='en';To='de';Pattern='morgen';Label='English to German produces a recognizable greeting.'},
        @{Text='Hola mundo.';From='es';To='en';Pattern='(hello|hi).*world';Label='Spanish to English produces a recognizable greeting.'},
        @{Text='Good morning.';From='en';To='es';Pattern='buen';Label='English to Spanish produces a recognizable greeting.'},
        @{Text='Bonjour le monde.';From='fr';To='en';Pattern='(hello|hi).*world';Label='French to English produces a recognizable greeting.'},
        @{Text='Good morning.';From='en';To='fr';Pattern='bonjour|bon.*matin';Label='English to French produces a recognizable greeting.'},
        @{Text='Guten Morgen.';From='de';To='es';Pattern='buen';Label='German to Spanish works through the bundled English pivot.'}
    )
    foreach ($fixture in $fixtures) {
        $result=Invoke-SyntheticTranslation $fixture.Text $fixture.From $fixture.To
        Confirm-Snip (-not [String]::IsNullOrWhiteSpace($result.Text) -and $result.Text -imatch $fixture.Pattern) $fixture.Label
        Confirm-Snip ($result.SourceCode -ceq $fixture.From -and $result.TargetCode -ceq $fixture.To -and $result.ProviderName -like '*Bergamot*') ('The real result reports the offline provider and ' + $fixture.From + ' to ' + $fixture.To + ' direction.')
    }

    # A whole-snip replacement maps each output to the same OCR source line.
    # Exercise multiple request IDs and a skipped blank line in one real worker.
    $segments = New-Object 'System.Collections.Generic.List[string]'
    @('Geschichte','','Guten Morgen.','Danke.') | ForEach-Object { $segments.Add($_) }
    $cancel=New-Object Threading.CancellationTokenSource
    $batchWorkers=New-Object 'System.Collections.Generic.HashSet[int]'
    $baseline=@(Get-BundledNodeIds)
    try {
        $batchTask=$service.TranslateSegmentsAsync($segments,'de','en',$cancel.Token)
        $batchWatch=[Diagnostics.Stopwatch]::StartNew()
        while (-not $batchTask.IsCompleted -and $batchWatch.ElapsedMilliseconds -lt 15000) {
            foreach ($workerId in @(Get-BundledNodeIds | Where-Object { $baseline -notcontains $_ })) { $null=$batchWorkers.Add($workerId) }
            Start-Sleep -Milliseconds 20
        }
        $batch=@(Wait-OfflineTask $batchTask $cancel)
        Confirm-Snip ($batch.Count -eq 4) 'Batch translation preserves the exact number of source regions.'
        Confirm-Snip ($batch[0] -imatch 'history|story|tale' -and $batch[1] -ceq '' -and $batch[2] -imatch 'morning' -and $batch[3] -imatch 'thank') 'Batch translations retain source order and the blank line without shifting regions.'
        Confirm-Snip ($batchWorkers.Count -eq 1) 'One observed bundled worker handles all real batch requests.'
        for ($i=0;$i -lt $segments.Count;$i++) {
            $translations.Add([pscustomobject]@{Source='de';Target='en';Input=$segments[$i];Output=$batch[$i];Provider='Bergamot batch';Milliseconds=$batchWatch.Elapsed.TotalMilliseconds})
        }
    } finally { $cancel.Dispose() }
    $oversizedSegments=New-Object 'System.Collections.Generic.List[string]'
    1..257 | ForEach-Object { $oversizedSegments.Add('Hello') }
    $cancel=New-Object Threading.CancellationTokenSource
    try {
        $errorValue=Get-OfflineTaskError ($service.TranslateSegmentsAsync($oversizedSegments,'en','de',$cancel.Token)) $cancel
        Confirm-Snip ($null -ne $errorValue -and $errorValue.GetType().FullName -ceq 'SnipStudio.TranslationException' -and [string]$errorValue.Kind -ceq 'InvalidRequest') 'A batch with too many source regions is rejected without partial translation.'
    } finally { $cancel.Dispose() }

    # Observe only newly created processes at this exact bundled runtime path.
    # Cancellation is exercised after an owned worker appears, then the adapter
    # itself is responsible for stopping it. No test-side process kill is used.
    $baseline=@(Get-BundledNodeIds)
    $cancel=New-Object Threading.CancellationTokenSource
    try {
        $task=$service.TranslateAsync(('Guten Morgen. '*1000),'de','en',$cancel.Token)
        $watch=[Diagnostics.Stopwatch]::StartNew()
        $newWorkers=@()
        while (-not $task.IsCompleted -and $watch.ElapsedMilliseconds -lt 3000) {
            $newWorkers=@(Get-BundledNodeIds | Where-Object { $baseline -notcontains $_ })
            if ($newWorkers.Count -gt 0) { break }
            Start-Sleep -Milliseconds 20
        }
        if ($task.IsCompleted -and $newWorkers.Count -eq 0) {
            # A genuinely completed fast request is a race, not a cancellation
            # failure. Keep it explicit rather than claiming this path ran.
            $null=Wait-OfflineTask $task $cancel 1000
            $notes.Add('Large request completed before cancellation could observe a worker; in-flight cancellation was not exercised.')
        } else {
            Confirm-Snip ($newWorkers.Count -gt 0) 'A real bundled worker was observed before cancellation.'
            $completedBeforeCancel=$task.IsCompleted
            $cancel.Cancel()
            $errorValue=Get-OfflineTaskError $task $cancel 5000
            if ($null -eq $errorValue) {
                $notes.Add('The observed worker completed before cancellation took effect; in-flight cancellation was not exercised. Completed before Cancel: ' + $completedBeforeCancel)
            } else {
                Confirm-Snip ($errorValue -is [OperationCanceledException]) 'Cancel stops an in-flight real-engine request.'
                $cancellationExercised=$true
            }
            $watch.Restart()
            do {
                $remainingWorkers=@(Get-StillRunningObservedIds $newWorkers)
                if ($remainingWorkers.Count -eq 0) { break }
                Start-Sleep -Milliseconds 20
            } while ($watch.ElapsedMilliseconds -lt 3000)
            $canceledWorkerGone=$remainingWorkers.Count -eq 0
            Confirm-Snip $canceledWorkerGone 'The observed owned worker exits after the cancellation attempt without a test-side kill.'
        }
    } finally { $cancel.Dispose() }
    Confirm-Snip ($slotsField.GetValue($null).CurrentCount -eq 1) 'The single-engine gate is available after completion or cancellation.'
    $result=Invoke-SyntheticTranslation 'Hola mundo.' 'es' 'en'
    Confirm-Snip ($result.Text -imatch '(hello|hi).*world') 'A new real translation succeeds after the cancellation path.'
} catch {
    $failure=Unwrap-OfflineError $_.Exception
} finally {
    if ($null -ne $service) { $service.Dispose() }
    $total.Stop()
    $report=[ordered]@{ApplicationVersion=$expectedVersion;ApplicationSHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;
        CheckedAtUtc=[DateTime]::UtcNow.ToString('o');Passed=($null -eq $failure);CheckCount=$checks.Count;
        Milliseconds=$total.Elapsed.TotalMilliseconds;RealEngineInvoked=($translations.Count -gt 0 -or $cancellationExercised);NetworkTracePerformed=$false;
        NoVisibleWindows=$true;NoDesktopCapture=$true;NoAudio=$true;NoClipboardAccess=$true;NoUserPreferences=$true;
        InFlightCancellationExercised=$cancellationExercised;ObservedOwnedWorkerExitVerified=$canceledWorkerGone;
        NodeSigner=$nodeSignature.SignerCertificate.Subject;NodeSHA256=(Get-FileHash -LiteralPath $node -Algorithm SHA256).Hash;
        Failure=$(if ($null -ne $failure) { $failure.Message } else { $null });
        Limits='Synthetic phrases check engine/model functionality only; no broad language-quality, network-isolation, live dialog, DPI, or perceived-speed guarantee.';
        Translations=$translations.ToArray();Checks=$checks.ToArray();Notes=$notes.ToArray()}
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'offline-translation-results.json') -Encoding UTF8
}
if ($null -ne $failure) { throw $failure }
Write-Output ('PASS: {0} offline checks; {1} real synthetic translations in {2:N0} ms. In-flight cancellation exercised: {3}.' -f $checks.Count,$translations.Count,$total.Elapsed.TotalMilliseconds,$cancellationExercised)

# SIG # Begin signature block
# MII2uAYJKoZIhvcNAQcCoII2qTCCNqUCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCANAiS81UnVTYli
# wkPOQ5t2uG0RfXQi8G9aqICcOJfkyKCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# MC8GCSqGSIb3DQEJBDEiBCBLt2b2lFf9QS+bnexru8APcc+VZ+gRAYBXx3zyE4+M
# WjANBgkqhkiG9w0BAQEFAASCAYA2dV834KTWPWg3ovTLrkyT4Z8hdzrvaKFHdimG
# nu4vdJ1Kxus9AawaNzHlx0U/Z7BEg7M68zv5mibSWWqqqAbEy9cPFwP8+/6uerKO
# XSdaguIwMNx/3GDt2KaI1LjYgYByUAxQGnn+sDLrQd9a8PMXRnRXjCcZvBj/kfO4
# lK++stdhiQzIpSv/rDIYusKVtD7IXGPF+jAxaVu7IEF54Kz/7fn2gHT4AnJuAHTY
# uwPBJ8WsIvLU4cDVQnHQrlYP7mrty0oEjKbFRkoV382uDkgk6HLqOTe6tQc6yUBo
# bv5YHAxOLF4syGkom6+2uR/10mDcce2Xd0Wu1p3CgMw3OnqWLEbQOxRYo2DxTc+g
# ks/O3Fi5mKUaXEZIG4+E1xga0mCV+BlwyU/JnafucgZbZHpBoHtn/94+8ydizsYK
# e5ya5MqI92NotA3HYQs8uEjB0Tc2L0sDXzojyEhiWZ2QbldXKx5mZfFmrak/giFc
# Z2wnXvkqLltyjXB/mHZdacpOt2ehghgUMIIYEAYKKwYBBAGCNwMDATGCGAAwghf8
# BgkqhkiG9w0BBwKgghftMIIX6QIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgKzXloX7B/69cu6u1RMcI+Yvtqw796iflvVfpfygGBWoCBmr8aVGG4hgT
# MjAyNjExMTgyMTMwNTYuNzA1WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
# EzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoT
# FU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJp
# Y2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOkE1MDAtMDVF
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
# hI6JQivRepyvWcl+JYbYbBh7pmgAXVswggeXMIIFf6ADAgECAhMzAAAAYHIGJIpK
# BbjZAAAAAABgMA0GCSqGSIb3DQEBDAUAMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJs
# aWMgUlNBIFRpbWVzdGFtcGluZyBDQSAyMDIwMB4XDTI2MDkxNzE4NTcyN1oXDTI3
# MDkxNDE4NTcyN1owgdsxCzAJBgNVBAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9u
# MRAwDgYDVQQHEwdSZWRtb25kMR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRp
# b24xJTAjBgNVBAsTHE1pY3Jvc29mdCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNV
# BAsTHm5TaGllbGQgVFNTIEVTTjpBNTAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWlj
# cm9zb2Z0IFB1YmxpYyBSU0EgVGltZSBTdGFtcGluZyBBdXRob3JpdHkwggIiMA0G
# CSqGSIb3DQEBAQUAA4ICDwAwggIKAoICAQCaE2NLwFPHivpDfMeASpyJR/YkExXC
# IhEJXza+XHOY1hGkA6ao2ad71PwUkWBnp707PqB7OFEH4XwXj1KLqcH76xP65J0W
# zFdOUXyufOYEF5+L9niiFkra3EJXSc1wzDuEgJO8QSo2ArhmWlyFdWvxyFj7FQsr
# JFRqs1cYjvJpuC+I9amZgH5b8ageWd9Xot3lqdg3IfhRI5NJVVAD/Uilf70bg7vS
# 52a97Sc1dMwdpmq1y25Pv6ItqWnoAPZRaSDRT+jO8vVlU8Zjdciqmg/hEg5TZIn5
# D2/wgDd5l4g20AicErzwL7sAW9/J2qu7o++A7JnCbvvwkCat8EID4WM5jh/J9qKD
# WUlzXX/2xH4vrsFsteuPGZubow2DGMn3ov0uPJVUpFbjkxsTChZ9inGRJns3lXP3
# Tz9ri2iDJdiqGmNybNR3BhB9e8pJdkwQXnFQYp5/N+FZx845ERvqd9QoxNc0ggoR
# A9RWtKnTjAp/2pA7WyToq2gQ/XhkTWEjYHyQsCmkb/8JYoPMoxQJNQ3LPxMZ5L8z
# BnyxPlq3ifwZhOPfUtO9rj1g1OKEkAsFJmJzJGfuLwTc1AbkuGVTpFOco0YJWcx0
# gr9QVfFOVZhBAXew3cXNGlJebmTUM9qOI5Cor38YMQUWc4KijuiQsKGqfpUekuS4
# i1BA8G49scji3wIDAQABo4IByzCCAccwHQYDVR0OBBYEFIhUKHRhUAQ7Ewm4gt6S
# o7de2mpiMB8GA1UdIwQYMBaAFGtpKDo1L0hjQM972K9J6T7ZPdshMGwGA1UdHwRl
# MGMwYaBfoF2GW2h0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01p
# Y3Jvc29mdCUyMFB1YmxpYyUyMFJTQSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAy
# MC5jcmwweQYIKwYBBQUHAQEEbTBrMGkGCCsGAQUFBzAChl1odHRwOi8vd3d3Lm1p
# Y3Jvc29mdC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMFB1YmxpYyUyMFJT
# QSUyMFRpbWVzdGFtcGluZyUyMENBJTIwMjAyMC5jcnQwDAYDVR0TAQH/BAIwADAW
# BgNVHSUBAf8EDDAKBggrBgEFBQcDCDAOBgNVHQ8BAf8EBAMCB4AwZgYDVR0gBF8w
# XTBRBgwrBgEEAYI3TIN9AQEwQTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNy
# b3NvZnQuY29tL3BraW9wcy9Eb2NzL1JlcG9zaXRvcnkuaHRtMAgGBmeBDAEEAjAN
# BgkqhkiG9w0BAQwFAAOCAgEAV4vmfykFNJc9GbTl2Jxub4K5oulwAz+QKHvLEpuj
# stjXFiHDnIhr96U5YuOmFVa58aCecRcGqrja4YM84DMVEUe4GXDyHbsLGW6ixMxz
# GzwSuz2uBBvs2Cccc259zOavuXOft2QhOcvBijiXXbFuSSPZXALUvR5i1uEkSOyp
# uBedo4fUCs49v+vGpe2dqsS4O9mIEzBs2XsuRBeVOYNKOHwCcXlXw4Ka8B5LPsMw
# bfMO3GYVt/NXQGx4lZPPVmInNNe84JUaHSwILSb7FUHGmf58TySy2QohU7wPgxms
# C32uhAVgf8Aqjd2dOOurzxS4DvqqlFpmGvT7N/aIXSAoAcGAZg82c9nvcgdBEkxC
# J9IKr8yhZBrfw7ex9pwNHa+DG8Nn6N+jstAVeD4IKAfaHPodl+F8PxKdN72YBgdj
# 9ExoeFH36MnSqVu8WMdoQEXeKwCpaimxMlZUMw8TLjuHA4h+QDIv1zj9xwiXNLSc
# UdErGE3ng5fU39s3BnHBtIcuhftj5VJ1YR+n/CudtSh8jkrKFqB7iBk4xle3fdzH
# FUUYGtOSqk27E2sBha/22Ex91IRC8ZV6UCAvJ50yNCRX97xQP+Vrs3nx1Z3Nw2Vc
# Vt81F78WHOx/hKtbUiWXIW5cjYaxnJdjUoeEKcAaTyni13gki4MhW2rpGgFeECXS
# ABYxggdGMIIHQgIBATB4MGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwAhMzAAAAYHIGJIpKBbjZAAAAAABgMA0GCWCGSAFl
# AwQCAQUAoIIEnzARBgsqhkiG9w0BCRACDzECBQAwGgYJKoZIhvcNAQkDMQ0GCyqG
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMTMwNTZaMC8GCSqGSIb3
# DQEJBDEiBCB19fJCsSBV47iq7BP2VvRwiuK7WXe3NlLJHS0WLY5atDCBuQYLKoZI
# hvcNAQkQAi8xgakwgaYwgaMwgaAEIDfLjLnwvS54utKqPKxmcYlH7Orna0xc019M
# pXpStgKzMHwwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3NvZnQg
# Q29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWVz
# dGFtcGluZyBDQSAyMDIwAhMzAAAAYHIGJIpKBbjZAAAAAABgMIIDYQYLKoZIhvcN
# AQkQAhIxggNQMIIDTKGCA0gwggNEMIICLAIBATCCAQmhgeGkgd4wgdsxCzAJBgNV
# BAYTAlVTMRMwEQYDVQQIEwpXYXNoaW5ndG9uMRAwDgYDVQQHEwdSZWRtb25kMR4w
# HAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xJTAjBgNVBAsTHE1pY3Jvc29m
# dCBBbWVyaWNhIE9wZXJhdGlvbnMxJzAlBgNVBAsTHm5TaGllbGQgVFNTIEVTTjpB
# NTAwLTA1RTAtRDk0NzE1MDMGA1UEAxMsTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZSBTdGFtcGluZyBBdXRob3JpdHmiIwoBATAHBgUrDgMCGgMVAJe/M8jCvUlk8+It
# Y60JUWKjdUI3oGcwZaRjMGExCzAJBgNVBAYTAlVTMR4wHAYDVQQKExVNaWNyb3Nv
# ZnQgQ29ycG9yYXRpb24xMjAwBgNVBAMTKU1pY3Jvc29mdCBQdWJsaWMgUlNBIFRp
# bWVzdGFtcGluZyBDQSAyMDIwMA0GCSqGSIb3DQEBCwUAAgUA7qg5UTAiGA8yMDI2
# MTExODE0NDg0OVoYDzIwMjYxMTE5MTQ0ODQ5WjB3MD0GCisGAQQBhFkKBAExLzAt
# MAoCBQDuqDlRAgEAMAoCAQACAgYqAgH/MAcCAQACAhJHMAoCBQDuqYrRAgEAMDYG
# CisGAQQBhFkKBAIxKDAmMAwGCisGAQQBhFkKAwKgCjAIAgEAAgMHoSChCjAIAgEA
# AgMBhqAwDQYJKoZIhvcNAQELBQADggEBACISIb9xNX8fNmpTSI1/ub5bt32kGNgI
# WbjXMqpF6GejxTqnEORfZMEPiY094rvrlQDrZrk2HStZNqnZizwq45vH2k/VaEfJ
# oqIO+rzIMtPLxvlFSnDpyTgwnwjKg9/Mj9719MJApVpvGrfRboXee0kVHeVPD7xG
# 87T/rNGOfoCwwu3awExtiZmvxdxwjJaudyCPpVzt6OlmaFGn2xQjKFXPHVZ/i6Vu
# 4MLdju3rQf5g+R+dz6tEtwb77Q7jKrAC395fzsfUB4Yi/7u5qxW5GcNFyhZVuckd
# e0bZbGdoNi2DLGZo/NmA1TkF71OJZA6zOpcqLYfBepIAaJrIRr7R1O0wDQYJKoZI
# hvcNAQEBBQAEggIAkyvdc5Srdc4EgQlKc9d05E37MesrXSDgEfmqLvczABWJDzHn
# 1QJZxEr3boPMSQrn0fL+DKWy0jOJQstedfXvoltSk2KyUMF0C7iUk6v2vR04vpoT
# rbkyNSYiwcpTPOGXNX9iPcU/lpExDI+k3aYcPUg76OLIjczgCquXophgDe+IT5p7
# c2pfbACzxOaWwlW2N+LiIEbfwwMr1JbDDM3vkI4Cc/xwWaAMRiEfAKSDZGtEhwKz
# ppoOUoXKYK2+HUXjEj3fziul4h2im5ciP/eu9qFr7Sjwqqo9wWGNUI87M9Se4EQs
# o9sNW7cFeypjh/WXejqv2Ua2oQOkW8NE+6Cn5Gr2C5M+p96q2y344Scs9CD9qV9j
# ATWT4O8kLvlFQGASORfsQHFWnTlf4z1f+5KQPers1CL4sV67iUGJQzn5L7Fk09kt
# j+4jjz2mfURzM4GBIzTWPiORpu7WEZbPH2ReOxeAsn1mz4198/xhGKdez6gAVI/1
# 6qJytT9jLemuHBmea0T8SNOV5PMLY8a53FTPEfDjD/frHsbjBNRszofsErv5FROB
# vuOvheedwa/i6utK4LnBdUbiAjo82r1RE9mx/t9Qp0umQDSD8kmk7AtSRpatt4VZ
# r9IH2eh3iyN1PlR7KE+pGsu+3xlzOKV+LDL2LnVezARrlD2jsQsOzvtSChQ=
# SIG # End signature block
