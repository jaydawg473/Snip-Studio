# Pure signed-library metadata and schedule policies. No updater HTTP request, update
# check task, application entrypoint, user preferences, registry or UI is used.
param([string]$ReleaseFolder='release-0.5.7-signed',[string]$ExpectedVersion='0.5.7.0')
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
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();UpdaterHTTPRequestsSent=$false;CheckAsyncInvoked=$false;ApplicationEntrypointExecuted=$false;UserPreferencesRead=$false;ApplicationRegistryAccessed=$false;UIOpened=$false;Scope='Pure stable-release JSON validation, exact owned HTTPS link/installer identity, version/result policies and daily-check scheduling against synthetic fixtures only. Standard Windows signature verification is outside the updater HTTP scope.';Unverified='Real GitHub connectivity, redirect/content-type/byte-stream limits, timeout and rate-limit behavior, HTTP caching/coalescing, automatic preference persistence, notification appearance, browser actions and installer execution.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'update-checker.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' pure signed update-checker checks; no updater HTTP, user settings or UI were used.')
