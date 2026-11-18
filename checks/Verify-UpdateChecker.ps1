# Pure signed-library metadata and schedule policies. No updater HTTP request, update
# check task, application entrypoint, user preferences, registry or UI is used.
param([string]$ReleaseFolder='release-0.5.8-signed',[string]$ExpectedVersion='0.5.8.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Net.Http
$assembly=[Reflection.Assembly]::LoadFrom($app)
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$service=$assembly.GetType('SnipStudio.UpdateChecker',$true)
$resultType=$assembly.GetType('SnipStudio.UpdateCheckResult',$true)
$parse=$service.GetMethod('ParseResponse',$static)
$due=$service.GetMethod('CheckDue',$static)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Update([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Get-UpdateProperty($Result,[string]$Name){return ,$resultType.GetProperty($Name,$instance).GetValue($Result,$null)}
function Invoke-UpdateMethod([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($index=0;$index -lt $Arguments.Length;$index++){
        if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}
    }
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function Parse-Update([Version]$Current,[AllowNull()][string]$Json){return Invoke-UpdateMethod $parse $null ([object[]]@($Current,$Json))}
function Due-Update([DateTime]$Last,[DateTime]$Now,[bool]$Enabled){return [bool](Invoke-UpdateMethod $due $null ([object[]]@($Last,$Now,$Enabled)))}
function New-UpdateFixture {
    return ,[ordered]@{
        tag_name='v0.5.8';draft=$false;prerelease=$false
        html_url='https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8'
        assets=@([ordered]@{
            name='SnipStudio-0.5.8-Setup.exe';state='uploaded'
            browser_download_url='https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe'
        })
    }
}
function Fixture-UpdateJson($Fixture){return ConvertTo-Json -InputObject $Fixture -Depth 12 -Compress}
function Confirm-UpdateUnavailable([AllowNull()][string]$Json,[string]$Label,[Version]$Current=[Version]'0.5.7.0'){
    $result=Parse-Update $Current $Json
    Confirm-Update ((Get-UpdateProperty $result 'Status').ToString() -ceq 'Unavailable' -and
        $null -eq (Get-UpdateProperty $result 'NewVersion') -and
        [String]::IsNullOrEmpty((Get-UpdateProperty $result 'ReleaseUrl')) -and
        [String]::IsNullOrEmpty((Get-UpdateProperty $result 'DownloadUrl')) -and
        -not [String]::IsNullOrWhiteSpace((Get-UpdateProperty $result 'UserMessage')) -and
        (Get-UpdateProperty $result 'CheckedUtc') -eq [DateTime]::MinValue) ($Label+' returns unavailable without a trusted release or download link')
}

$current=[Version]'0.5.7.0'
Confirm-Update ($service.GetField('LatestReleaseEndpoint',$static).GetRawConstantValue() -ceq 'https://api.github.com/repos/jaydawg473/Snip-Studio/releases/latest') 'The checker targets only the intended public stable-release endpoint'
$validJson=Fixture-UpdateJson (New-UpdateFixture)
$available=Parse-Update $current $validJson
Confirm-Update ((Get-UpdateProperty $available 'Status').ToString() -ceq 'UpdateAvailable' -and
    (Get-UpdateProperty $available 'CurrentVersion').Equals($current) -and
    (Get-UpdateProperty $available 'NewVersion').Equals([Version]'0.5.8.0')) 'A verified newer stable release produces an update notification for the current version'
Confirm-Update ((Get-UpdateProperty $available 'ReleaseUrl') -ceq 'https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8' -and
    (Get-UpdateProperty $available 'DownloadUrl') -ceq 'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe') 'An available update exposes only the exact verified official release and installer URLs'
Confirm-Update ((Get-UpdateProperty $available 'CheckedUtc') -eq [DateTime]::MinValue) 'Parsing metadata alone does not claim a network-check timestamp'
foreach($running in @([Version]'0.5.8.0',[Version]'0.5.8.1',[Version]'0.6.0.0',[Version]'1.0.0.0')){
    $result=Parse-Update $running $validJson
    Confirm-Update ((Get-UpdateProperty $result 'Status').ToString() -ceq 'UpToDate' -and
        (Get-UpdateProperty $result 'CurrentVersion').Equals($running)) 'A running version at or ahead of the verified stable release is never offered a downgrade'
}
foreach($running in @([Version]'0.5',[Version]'0.5.7')){
    $result=Parse-Update $running $validJson
    Confirm-Update ((Get-UpdateProperty $result 'CurrentVersion').Revision -eq 0 -and
        (Get-UpdateProperty $result 'Status').ToString() -ceq 'UpdateAvailable') 'Two- and three-part running versions are normalized before comparison'
}
$bom=Parse-Update $current ([string][char]0xFEFF+$validJson)
Confirm-Update ((Get-UpdateProperty $bom 'Status').ToString() -ceq 'UpdateAvailable') 'A UTF-8 BOM decoded at the start of official JSON does not hide a valid update'
$fixture=New-UpdateFixture
$fixture['assets']=@([ordered]@{name='portable.zip';state='uploaded';browser_download_url='https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/portable.zip'},$fixture['assets'][0])
Confirm-Update ((Get-UpdateProperty (Parse-Update $current (Fixture-UpdateJson $fixture)) 'Status').ToString() -ceq 'UpdateAvailable') 'Unrelated release assets do not suppress the single verified installer'

foreach($property in @('draft','prerelease')){
    foreach($value in @($true,'false',0,$null)){
        $fixture=New-UpdateFixture;$fixture[$property]=$value
        Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) ('A non-false Boolean '+$property+' value')
    }
    $fixture=New-UpdateFixture;$fixture.Remove($property)
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) ('A missing '+$property+' flag')
}
foreach($tag in @($null,'','0.5.8','V0.5.8','v00.5.8','v0.05.8','v0.5.08','v0.5.8.0','v0.5.8-beta','v0.5.8+build','v0.5','v-1.5.8','v2147483647.5.8',"v0.5.8`n")){
    $fixture=New-UpdateFixture;$fixture['tag_name']=$tag
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'A missing, malformed, prerelease or noncanonical version tag'
}
$fixture=New-UpdateFixture;$fixture.Remove('tag_name')
Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'A release without a tag field'
$releaseUrls=@(
    'http://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8',
    'https://github.com.example.invalid/jaydawg473/Snip-Studio/releases/tag/v0.5.8',
    'https://github.com@public.example.invalid/jaydawg473/Snip-Studio/releases/tag/v0.5.8',
    'https://github.com:443/jaydawg473/Snip-Studio/releases/tag/v0.5.8',
    'https://github.com/jaydawg473/%53nip-Studio/releases/tag/v0.5.8',
    'https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8?download=1',
    'https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8#details',
    'https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.8/',
    'https://github.com/another-owner/Snip-Studio/releases/tag/v0.5.8',
    'https://github.com/jaydawg473/another-repository/releases/tag/v0.5.8',
    'https://github.com/jaydawg473/Snip-Studio/releases/tag/v0.5.9',
    'https://github.com/jaydawg473/Snip-Studio/releases/latest',
    '',$null)
foreach($url in $releaseUrls){
    $fixture=New-UpdateFixture;$fixture['html_url']=$url
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'An untrusted or noncanonical release URL'
}
$downloadUrls=@(
    'http://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://public.example.invalid/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://github.com@public.example.invalid/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://github.com:443/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/%53nipStudio-0.5.8-Setup.exe',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe?token=fixture',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe#fixture',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe/',
    'https://github.com/another-owner/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://github.com/jaydawg473/another-repository/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.exe',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.9/SnipStudio-0.5.8-Setup.exe',
    'https://github.com/jaydawg473/Snip-Studio/releases/download/v0.5.8/SnipStudio-0.5.8-Setup.cmd',
    '',$null)
foreach($url in $downloadUrls){
    $fixture=New-UpdateFixture;$fixture['assets'][0]['browser_download_url']=$url
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'An untrusted or noncanonical installer download URL'
}
foreach($state in @('new','uploading','starter','UPLOADED','',$null,1)){
    $fixture=New-UpdateFixture;$fixture['assets'][0]['state']=$state
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'An installer that is not exactly in uploaded state'
}
foreach($name in @('SnipStudio-0.5.7-Setup.exe','snipstudio-0.5.8-setup.exe','SnipStudio-0.5.8-Setup.exe.cmd','portable.zip','',$null)){
    $fixture=New-UpdateFixture;$fixture['assets'][0]['name']=$name
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'A release without the exact expected installer name'
}
$fixture=New-UpdateFixture;$fixture['assets']=@($fixture['assets'][0],$fixture['assets'][0])
Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'Duplicate matching installer assets'
foreach($assets in @([pscustomobject]@{Value=$null},[pscustomobject]@{Value=@()},[pscustomobject]@{Value=@('invalid asset')},[pscustomobject]@{Value='not an array'})){
    $fixture=New-UpdateFixture;$fixture['assets']=$assets.Value
    Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'Missing, empty or incorrectly typed asset metadata'
}
$fixture=New-UpdateFixture;$fixture.Remove('assets')
Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'A missing assets field' ([Version]'0.5.9.0')
$fixture=New-UpdateFixture
$largeAssets=New-Object 'System.Collections.Generic.List[object]'
$largeAssets.Add($fixture['assets'][0])
for($index=1;$index -lt 64;$index++){$largeAssets.Add([ordered]@{name=('unrelated-'+$index);state='uploaded'})}
$fixture['assets']=$largeAssets.ToArray()
Confirm-Update ((Get-UpdateProperty (Parse-Update $current (Fixture-UpdateJson $fixture)) 'Status').ToString() -ceq 'UpdateAvailable') 'The documented asset-count boundary accepts one installer among sixty-four assets'
$largeAssets.Add([ordered]@{name='unrelated-over-limit';state='uploaded'});$fixture['assets']=$largeAssets.ToArray()
Confirm-UpdateUnavailable (Fixture-UpdateJson $fixture) 'More than sixty-four assets'
foreach($json in @($null,'',' ', '{','[]','null','"release"','{ "draft": false, "tag_name": ')){
    Confirm-UpdateUnavailable $json 'Malformed, empty or non-object JSON'
}
$limit=[int]$service.GetField('MaximumResponseBytes',$static).GetRawConstantValue()
Confirm-UpdateUnavailable ($validJson+(' '*($limit+1))) 'An otherwise valid but oversized parser input'
$fixture=New-UpdateFixture;$deep='0'
for($index=0;$index -lt 16;$index++){$deep='{"nested":'+$deep+'}'}
$deepJson=$validJson.Substring(0,$validJson.Length-1)+',"extra":'+$deep+'}'
Confirm-UpdateUnavailable $deepJson 'A JSON structure beyond the bounded recursion depth'
foreach($name in @('Status','CurrentVersion','NewVersion','ReleaseUrl','DownloadUrl','UserMessage','CheckedUtc')){
    $property=$resultType.GetProperty($name,$instance)
    $setter=if($null -ne $property){$property.GetSetMethod($true)}else{$null}
    Confirm-Update ($null -ne $property -and ($null -eq $setter -or $setter.IsPrivate)) 'Update result properties cannot be mutated by consumers'
}
$checked=[DateTime]::SpecifyKind([DateTime]'2026-11-10T12:00:00',[DateTimeKind]::Utc)
$dated=Invoke-UpdateMethod ($resultType.GetMethod('At',$instance)) $available ([object[]]@($checked))
$samePayload=$true
foreach($name in @('Status','CurrentVersion','NewVersion','ReleaseUrl','DownloadUrl','UserMessage')){
    $samePayload=$samePayload -and [Object]::Equals((Get-UpdateProperty $dated $name),(Get-UpdateProperty $available $name))
}
Confirm-Update (-not [Object]::ReferenceEquals($available,$dated) -and (Get-UpdateProperty $dated 'CheckedUtc') -eq $checked -and
    (Get-UpdateProperty $available 'CheckedUtc') -eq [DateTime]::MinValue -and
    $samePayload) 'Attaching a real check timestamp creates a new result without altering the parser result or trusted metadata'

$now=$checked
Confirm-Update (-not (Due-Update ([DateTime]::MinValue) $now $false)) 'Disabled automatic checking suppresses a first-run check'
Confirm-Update (-not (Due-Update ($now.AddDays(-2)) $now $false)) 'Disabled automatic checking suppresses an overdue check'
Confirm-Update (Due-Update ([DateTime]::MinValue) $now $true) 'A newly enabled profile with no last attempt is due'
Confirm-Update (-not (Due-Update $now $now $true)) 'An attempt at the current instant is not due again'
Confirm-Update (-not (Due-Update ($now.AddHours(-24).AddTicks(1)) $now $true)) 'An attempt less than twenty-four hours ago is not due'
Confirm-Update (Due-Update ($now.AddHours(-24)) $now $true) 'The exact twenty-four-hour boundary is due'
Confirm-Update (Due-Update ($now.AddHours(-24).AddTicks(-1)) $now $true) 'An attempt older than the daily interval is due'
Confirm-Update (Due-Update ($now.AddTicks(1)) $now $true) 'A future saved attempt permits clock-rollback recovery'
Confirm-Update (Due-Update ($now.AddHours(-24).ToLocalTime()) ($now.ToLocalTime()) $true) 'Equivalent local timestamps are converted to UTC before the due calculation'
Confirm-Update (-not (Due-Update ($now.AddHours(-23).ToLocalTime()) $now $true)) 'Mixed local and UTC timestamps preserve a not-yet-due interval'
$unspecifiedPrevious=[DateTime]::SpecifyKind($now.AddHours(-24),[DateTimeKind]::Unspecified)
$unspecifiedNow=[DateTime]::SpecifyKind($now,[DateTimeKind]::Unspecified)
Confirm-Update (Due-Update $unspecifiedPrevious $unspecifiedNow $true) 'Persisted timestamps with unspecified kind are interpreted as UTC'
Confirm-Update ($null -eq $service.GetField('inFlight',$static).GetValue($null) -and $null -eq $service.GetField('cached',$static).GetValue($null)) 'Pure parser and schedule checks never start or cache an HTTP request'
$baselineChecks=$checks.Count

# Exception instances are synthetic data. None starts a request or performs DNS.
# Classification and display must use type/status, never diagnostic text that
# could contain proxy, machine or account details.
$classifier=$service.GetMethod('ClassifyTransportFailure',$static)
$describe=$service.GetMethod('DescribeTransportFailure',$static)
$failureType=$assembly.GetType('SnipStudio.UpdateTransportFailure',$true)
$diagnosticMarker='Untrusted fixture diagnostic content'
$transportCases=New-Object 'System.Collections.Generic.List[object]'
function Add-UpdateTransportCase([Exception]$FailureException,[bool]$Canceled,[string]$Expected,[string]$Label){
    $transportCases.Add([pscustomobject]@{Error=$FailureException;Canceled=$Canceled;Expected=$Expected;Label=$Label})
}
function New-UpdateWebError([Net.WebExceptionStatus]$Status){return [Net.WebException]::new($diagnosticMarker,$null,$Status,$null)}
Add-UpdateTransportCase $null $false 'Connection' 'An absent underlying failure uses the generic connection category'
Add-UpdateTransportCase $null $true 'Timeout' 'A canceled request is a timeout even without an underlying exception'
Add-UpdateTransportCase ([Exception]::new($diagnosticMarker+' DNS proxy TLS timeout')) $false 'Connection' 'Words inside an untrusted exception message do not determine its category'
Add-UpdateTransportCase ([Net.Http.HttpRequestException]::new($diagnosticMarker)) $false 'Connection' 'An opaque HTTP transport error remains a generic connection error'
Add-UpdateTransportCase ([IO.IOException]::new($diagnosticMarker)) $false 'Connection' 'An opaque stream error remains a generic connection error'
Add-UpdateTransportCase ([OperationCanceledException]::new($diagnosticMarker)) $false 'Timeout' 'A cancellation exception has the timeout category'
Add-UpdateTransportCase ([Threading.Tasks.TaskCanceledException]::new($diagnosticMarker)) $false 'Timeout' 'A canceled HTTP task has the timeout category'
Add-UpdateTransportCase ([Security.Authentication.AuthenticationException]::new($diagnosticMarker)) $false 'SecureConnection' 'A TLS authentication failure has the secure-connection category'
Add-UpdateTransportCase ([Security.SecurityException]::new($diagnosticMarker)) $false 'Blocked' 'A security-policy exception has the blocked category'
Add-UpdateTransportCase ([UnauthorizedAccessException]::new($diagnosticMarker)) $false 'Blocked' 'An access-denied exception has the blocked category'
Add-UpdateTransportCase ([ObjectDisposedException]::new('fixture',$diagnosticMarker)) $false 'Connection' 'A disposed response without cancellation is not mislabeled as a timeout'
Add-UpdateTransportCase ([ObjectDisposedException]::new('fixture',$diagnosticMarker)) $true 'Timeout' 'A disposed response during cancellation retains the timeout category'
$webCases=@(
    @{Status=[Net.WebExceptionStatus]::NameResolutionFailure;Expected='NameResolution'},
    @{Status=[Net.WebExceptionStatus]::ProxyNameResolutionFailure;Expected='Proxy'},
    @{Status=[Net.WebExceptionStatus]::RequestProhibitedByProxy;Expected='Proxy'},
    @{Status=[Net.WebExceptionStatus]::SecureChannelFailure;Expected='SecureConnection'},
    @{Status=[Net.WebExceptionStatus]::TrustFailure;Expected='SecureConnection'},
    @{Status=[Net.WebExceptionStatus]::Timeout;Expected='Timeout'},
    @{Status=[Net.WebExceptionStatus]::ConnectFailure;Expected='Connection'})
foreach($case in $webCases){Add-UpdateTransportCase (New-UpdateWebError $case.Status) $false $case.Expected ('Web status '+$case.Status+' is classified without reading diagnostic text')}
foreach($case in @(
    @{Code=[Net.Sockets.SocketError]::HostNotFound;Expected='NameResolution'},
    @{Code=[Net.Sockets.SocketError]::TryAgain;Expected='NameResolution'},
    @{Code=[Net.Sockets.SocketError]::NoData;Expected='NameResolution'},
    @{Code=[Net.Sockets.SocketError]::NoRecovery;Expected='NameResolution'},
    @{Code=[Net.Sockets.SocketError]::TimedOut;Expected='Timeout'},
    @{Code=[Net.Sockets.SocketError]::AccessDenied;Expected='Blocked'},
    @{Code=[Net.Sockets.SocketError]::ConnectionRefused;Expected='Connection'})){
    Add-UpdateTransportCase ([Net.Sockets.SocketException]::new([int]$case.Code)) $false $case.Expected ('Socket status '+$case.Code+' is classified by its numeric status')
}
$wrappedDns=[Net.Http.HttpRequestException]::new($diagnosticMarker,[IO.IOException]::new($diagnosticMarker,(New-UpdateWebError ([Net.WebExceptionStatus]::NameResolutionFailure))))
$wrappedTls=[Net.Http.HttpRequestException]::new($diagnosticMarker,[IO.IOException]::new($diagnosticMarker,[Security.Authentication.AuthenticationException]::new($diagnosticMarker)))
Add-UpdateTransportCase $wrappedDns $false 'NameResolution' 'A DNS failure survives HTTP and stream exception wrappers'
Add-UpdateTransportCase $wrappedTls $false 'SecureConnection' 'A TLS failure survives HTTP and stream exception wrappers'
Add-UpdateTransportCase $wrappedDns $true 'Timeout' 'Request cancellation takes precedence over a wrapped DNS failure'
Add-UpdateTransportCase $wrappedTls $true 'Timeout' 'Request cancellation takes precedence over a wrapped TLS failure'
$withinDepth=New-UpdateWebError ([Net.WebExceptionStatus]::NameResolutionFailure)
for($index=0;$index -lt 11;$index++){$withinDepth=[Exception]::new($diagnosticMarker,$withinDepth)}
Add-UpdateTransportCase $withinDepth $false 'NameResolution' 'The last supported inner-exception depth still preserves a recognized failure'
Add-UpdateTransportCase ([Exception]::new($diagnosticMarker,$withinDepth)) $false 'Connection' 'Unbounded exception nesting falls back after twelve inspected links'
foreach($case in $transportCases){
    $failure=Invoke-UpdateMethod $classifier $null ([object[]]@($case.Error,$case.Canceled))
    $message=[string](Invoke-UpdateMethod $describe $null ([object[]]@($failure)))
    Confirm-Update ($failure.ToString() -ceq $case.Expected -and -not [String]::IsNullOrWhiteSpace($message) -and
        -not $message.Contains($diagnosticMarker)) $case.Label
}
$messages=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach($failure in [Enum]::GetValues($failureType)){
    $message=[string](Invoke-UpdateMethod $describe $null ([object[]]@($failure)))
    Confirm-Update ($messages.Add($message) -and -not $message.Contains($diagnosticMarker)) 'Each transport category has a distinct fixed user message without raw exception details'
}
$reuse=$service.GetMethod('ShouldReuseFetch',$static)
$retryCases=@(
    @{Checked=$now.AddSeconds(-10).AddTicks(1);Retry=[DateTime]::MinValue;Force=$true;Transient=$true;Expected=$true;Label='A transient manual retry reuses the result for less than ten seconds'},
    @{Checked=$now.AddSeconds(-10);Retry=[DateTime]::MinValue;Force=$true;Transient=$true;Expected=$false;Label='A transient manual retry is permitted at exactly ten seconds'},
    @{Checked=$now.AddSeconds(-11);Retry=[DateTime]::MinValue;Force=$true;Transient=$true;Expected=$false;Label='A transient error is not held for the old one-minute cooldown'},
    @{Checked=$now.AddMinutes(-1).AddTicks(1);Retry=[DateTime]::MinValue;Force=$true;Transient=$false;Expected=$true;Label='Other manual outcomes retain the one-minute minimum interval'},
    @{Checked=$now.AddMinutes(-1);Retry=[DateTime]::MinValue;Force=$true;Transient=$false;Expected=$false;Label='Other manual outcomes may refresh at exactly one minute'},
    @{Checked=$now.AddHours(-24).AddTicks(1);Retry=[DateTime]::MinValue;Force=$false;Transient=$true;Expected=$true;Label='Automatic checks retain the daily interval after a transient failure'},
    @{Checked=$now.AddHours(-24);Retry=[DateTime]::MinValue;Force=$false;Transient=$true;Expected=$false;Label='An automatic transient outcome expires at exactly twenty-four hours'},
    @{Checked=$now.AddHours(-24);Retry=[DateTime]::MinValue;Force=$false;Transient=$false;Expected=$false;Label='An automatic ordinary outcome expires at exactly twenty-four hours'},
    @{Checked=$now.AddMinutes(-2);Retry=$now.AddMinutes(30);Force=$true;Transient=$true;Expected=$true;Label='A manual transient retry cannot bypass an active server backoff'},
    @{Checked=$now.AddDays(-2);Retry=$now.AddMinutes(30);Force=$false;Transient=$false;Expected=$true;Label='An overdue automatic check still honors an active server backoff'},
    @{Checked=$now.AddSeconds(-11);Retry=$now;Force=$true;Transient=$true;Expected=$false;Label='An expired server deadline permits a retry once its own cooldown has elapsed'},
    @{Checked=$now.AddMinutes(1);Retry=[DateTime]::MinValue;Force=$true;Transient=$true;Expected=$false;Label='A future cached check without server backoff does not strand manual checks'},
    @{Checked=$now.AddMinutes(1);Retry=$now.AddMinutes(30);Force=$true;Transient=$true;Expected=$true;Label='Clock rollback does not override an active server-issued deadline'},
    @{Checked=[DateTime]::MinValue;Retry=[DateTime]::MinValue;Force=$true;Transient=$false;Expected=$false;Label='An uninitialized cached check without a server deadline is not reused'},
    @{Checked=[DateTime]::MinValue;Retry=$now.AddMinutes(30);Force=$true;Transient=$true;Expected=$true;Label='An active server deadline is honored even without a usable check timestamp'},
    @{Checked=$now.AddSeconds(-9).ToLocalTime();Retry=[DateTime]::MinValue;Force=$true;Transient=$true;Expected=$true;Label='Equivalent local timestamps preserve the manual transport cooldown'})
foreach($case in $retryCases){
    $actual=[bool](Invoke-UpdateMethod $reuse $null ([object[]]@($case.Checked,$case.Retry,$now,$case.Force,$case.Transient)))
    Confirm-Update ($actual -eq $case.Expected) $case.Label
}

# The signed product has an explicit Framework target and an isolated early
# diagnostic path. Inspect its metadata rather than executing its entrypoint.
$targetFramework=@($assembly.GetCustomAttributes($false)|Where-Object {$_.GetType().FullName -ceq 'System.Runtime.Versioning.TargetFrameworkAttribute'})
Confirm-Update ($targetFramework.Count -eq 1 -and $targetFramework[0].FrameworkName -ceq '.NETFramework,Version=v4.8') 'The signed application declares the intended Framework 4.8 target'
$opcodes=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null);$opcodes[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-UpdateCalls([Reflection.MethodBase]$Method){
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $start=$offset;$code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $op=$opcodes[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $op){throw 'Unknown diagnostic IL opcode.'}
        $operand=$op.OperandType.ToString();$length=0
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod'){
            $target=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset))
            [pscustomobject]@{Offset=$start;Target=$target;Name=$target.Name;Type=$target.DeclaringType.FullName;Opcode=$op.Name}
        }
        $offset+=$length
    }
}
$program=$assembly.GetType('SnipStudio.Program',$true)
$diagnostic=$program.GetMethod('RunUpdateCheckDiagnostic',$static)
$entryCalls=@(Read-UpdateCalls $assembly.EntryPoint)
$diagnosticCall=@($entryCalls|Where-Object {$_.Target -eq $diagnostic})
$parseCall=@($entryCalls|Where-Object {$_.Name -ceq 'TryFromArguments' -and $_.Type -ceq 'SnipStudio.ShortcutStartupRequest'})
$forwardCall=@($entryCalls|Where-Object {$_.Name -ceq 'TryForward' -and $_.Type -ceq 'SnipStudio.ShortcutHost'})
Confirm-Update ($diagnosticCall.Count -eq 1 -and $parseCall.Count -eq 1 -and $forwardCall.Count -ge 1 -and
    $diagnosticCall[0].Offset -lt $parseCall[0].Offset -and
    @($forwardCall|Where-Object {$_.Offset -le $diagnosticCall[0].Offset}).Count -eq 0) 'The dedicated update diagnostic is selected before ordinary launch parsing or every IPC forwarding attempt'
$diagnosticCalls=@(Read-UpdateCalls $diagnostic)
Confirm-Update (@($diagnosticCalls|Where-Object {$_.Name -ceq 'CheckAsync' -and $_.Type -ceq 'SnipStudio.UpdateChecker'}).Count -eq 1 -and
    @($diagnosticCalls|Where-Object {$_.Type -in @('SnipStudio.Preferences','SnipStudio.ShortcutHost','SnipStudio.MainForm','SnipStudio.TaskbarJumpList') -or $_.Type.StartsWith('System.Windows.Forms.',[StringComparison]::Ordinal)}).Count -eq 0) 'The isolated product diagnostic calls the real checker without direct preferences, IPC or window initialization'
Confirm-Update ($null -eq $service.GetField('inFlight',$static).GetValue($null) -and $null -eq $service.GetField('cached',$static).GetValue($null)) 'Synthetic transport and retry policies did not schedule or cache an updater request'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;OriginalMetadataAndScheduleChecks=$baselineChecks;Checks=$checks.ToArray();UpdaterHTTPRequestsSent=$false;CheckAsyncInvoked=$false;ApplicationEntrypointExecuted=$false;UserPreferencesRead=$false;ApplicationRegistryAccessed=$false;UIOpened=$false;Scope='Pure stable-release JSON validation, exact owned HTTPS link/installer identity, result/daily schedule policies, synthetic transport classification/manual retry/server backoff policies, Framework target and diagnostic call metadata. Standard Windows signature verification is outside the updater HTTP scope.';Unverified='Real signed-process connectivity, redirect/content-type/byte-stream limits, runtime timeout and server backoff behavior, HTTP request caching/coalescing, automatic preference persistence, notification appearance, browser actions and installer execution.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'update-checker.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' pure signed update-checker checks; no updater HTTP, user settings or UI were used.')

# SIG # Begin signature block
# MII2uAYJKoZIhvcNAQcCoII2qTCCNqUCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAY4HeS+1Qhg8kM
# OXQmzxxYWOVxB7zn3yNkVW6WcJCAWaCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
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
# MC8GCSqGSIb3DQEJBDEiBCA3TC5UF1SclZXiGH8yvP+eH81z0AvaBI5QSmL5/VPN
# 8jANBgkqhkiG9w0BAQEFAASCAYBw1JBW0EhCZ/RgTRGBJKtR6CsS8JHVZSPLuUWi
# nxXPvUuGdr1ajPbWsMC/MWGD7BiUD3ABti3Xgh8fD+FqZpDsboo2v2fuduDkjtyX
# JRS/CzmF7XGZM7pfbwfoAu1VCZ+Mc0/qqEBW8dsYikzzdvzkgBOdaQZp/OvnlPTF
# LGsWq+Es8iZLx7waOk5egCRW9tTczjwsZDFeNeBvnjL6Rwh+U8hL310pIzkZZTXw
# poLvMQiBAOJ9DdB11SOnmM1/vmz+9CQP2zyCqdVd8Fk9oORi6IGuWq4BtZRE6ggZ
# JGYbBXK+r7Wf4jnVrOMcY2dpxr13fmouplNdzO+sJP+tQ9yEthxp/Vd20jGD2f2y
# KDa1918gC++uPifAnbSfyubU2BS4K90VB0op0PUWoCk1E2RU8+7t0WSCphf4EkoI
# iheE7wS+Xe6IUTsFEKZsWVomPzrpUYyzyTdxIqaEqm44bQF8R/ahLUe35v/dcJWv
# Pgwly2CxsAL5BCa8IK+5WfVbBX6hghgUMIIYEAYKKwYBBAGCNwMDATGCGAAwghf8
# BgkqhkiG9w0BBwKgghftMIIX6QIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYgYLKoZI
# hvcNAQkQAQSgggFRBIIBTTCCAUkCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgRIa0VsrV1FgZXjdyCUMGLIYqgaFUcazGEE9cpuLqdeYCBmr8aVF2yBgT
# MjAyNjExMTgyMTE3MDkuNjY4WjAEgAIB9KCB4aSB3jCB2zELMAkGA1UEBhMCVVMx
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
# SIb3DQEJEAEEMBwGCSqGSIb3DQEJBTEPFw0yNjExMTgyMTE3MDlaMC8GCSqGSIb3
# DQEJBDEiBCBMlJNK3D8OO1i1mG3lny1FbpZG2Yom/IWytuAno7K8/DCBuQYLKoZI
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
# hvcNAQEBBQAEggIAexMCCU26FPfOqZIyBtzd+MlQEBTJsAkbD12pAjMl457dKOzx
# YMlwYalZyib3UvKwdVk+HfDkspGJ51uDp7PTb9UJFzYnlO/tqq3GgC6Jyk/z7bZP
# zga1W2C7FJ2dYn97KzexsWLvngYw8Ohtv3zTM82bLtBtk4r+1i/z/tIvZKUiPkIh
# oA2pdkTpkmIKkOcSWJBMTivwr8GxWw59TaZw46dcFV92e3x2juHL6R81qq4lx1T7
# I1aAm0K1oN+fKu1b2ZezTCdLWlLXpu6Yl9TLq8iFRr9J4OMNBoDCPOZaIjiVHE3M
# 4IVc1+8XfHlCtik+/19Wd41kPKTT0FQyUAhAS7VD9Dw87YxmT8LiIjUYZO4dEVXW
# mQc1LDvEHIHVunTRZvMqgWeLyNYZlZuY1kQOn+dEgRfUR7UWSE6GKozjd9smcy7F
# aErw6/mgz4itiZoO5kD1eWzjRruZGLYm+cPwuX9+NHVVa6oO5xVcE/mGq3Rhbo1m
# /Ck1mlkJfaaX8Fqo+2bsNDSvO8+PPwCEzUgy8RvsLx8CVIfH9qc2Yv4JeiKcSifa
# gFDqXVWs/3w1mTB8m3A2ZFitahzmAGRhJzVdxzTnqF1VdcEybtX7n4FATNAzh8ZJ
# ctC3feU4ZibpGgY4L+CzRKkKCI6mJRHf1VBU8b/+LteYWujqsNtswUH3QEk=
# SIG # End signature block
