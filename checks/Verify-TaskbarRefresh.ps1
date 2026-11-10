# Signed-library refresh policy plus private on-disk ShellLink fixtures.
# No product entrypoint, real worker, native broker, application window, Recent
# registration, file associations, actual pins or Start menu links are touched.
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
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$service=$assembly.GetType('SnipStudio.TaskbarJumpList',$true)
$resultType=$service.GetNestedType('TaskbarRefreshResult',$instance)
$resultCtor=$resultType.GetConstructor($instance,$null,[type[]]@([bool],[bool],[Collections.Generic.IList[string]]),$null)
if($null -eq $resultCtor){throw 'The refresh result policy was not found.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Refresh([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Get-RefreshProperty($Target,[string]$Name){return ,$Target.GetType().GetProperty($Name,$instance).GetValue($Target,$null)}
function Get-RefreshField([string]$Name){return ,$service.GetField($Name,$static).GetValue($null)}
function Set-RefreshField([string]$Name,$Value){$service.GetField($Name,$static).SetValue($null,$Value)}
function Invoke-RefreshMethod([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    # Reflection sees the underlying boxed structs/COM values, not PSObject.
    for($argumentIndex=0;$argumentIndex -lt $Arguments.Length;$argumentIndex++){
        if($null -ne $Arguments[$argumentIndex]){$Arguments[$argumentIndex]=$Arguments[$argumentIndex].PSObject.BaseObject}
    }
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function New-RefreshResult([bool]$Menu,[bool]$Shortcuts,[Collections.Generic.IList[string]]$Warnings){
    [object[]]$arguments=@($Menu,$Shortcuts,$Warnings)
    for($argumentIndex=0;$argumentIndex -lt $arguments.Length;$argumentIndex++){
        if($null -ne $arguments[$argumentIndex]){$arguments[$argumentIndex]=$arguments[$argumentIndex].PSObject.BaseObject}
    }
    return $resultCtor.Invoke($arguments)
}
function New-RefreshWarnings([string[]]$Messages){
    $list=New-Object 'System.Collections.Generic.List[string]'
    foreach($message in $Messages){$list.Add($message)}
    return ,$list
}

# The native worker must never be scheduled by this managed fixture. Pretend
# its identity and worker already exist, then simulate only the generation
# dequeue that Work performs under its lock; no thread or Shell work is run.
$fieldNames=@('identityReady','initializePending','workerRunning','shellReady','lastError','pendingRefresh')
$savedFields=@{}
foreach($name in $fieldNames){$savedFields[$name]=Get-RefreshField $name}
if($savedFields.identityReady -or $savedFields.workerRunning -or $null -ne $savedFields.pendingRefresh){throw 'Taskbar service was already active; use a fresh test process.'}
$pendingFileCount=(Get-RefreshField 'pendingFiles').Count
$refresh=$service.GetMethod('RefreshAsync',$static)
try{
    Set-RefreshField 'identityReady' $true
    Set-RefreshField 'workerRunning' $true
    Set-RefreshField 'initializePending' $false
    Set-RefreshField 'pendingRefresh' $null
    $first=Invoke-RefreshMethod $refresh $null ([object[]]@())
    $firstAgain=Invoke-RefreshMethod $refresh $null ([object[]]@())
    $firstCompletion=Get-RefreshField 'pendingRefresh'
    Confirm-Refresh ([Object]::ReferenceEquals($first,$firstAgain) -and -not $first.IsCompleted -and (Get-RefreshField 'initializePending')) 'Concurrent callers share the first pending generation without starting a worker'
    Confirm-Refresh (($first.CreationOptions -band [Threading.Tasks.TaskCreationOptions]::RunContinuationsAsynchronously) -ne 0) 'Refresh completions use asynchronous continuations rather than running UI callbacks inside the worker lock'
    Set-RefreshField 'pendingRefresh' $null
    Set-RefreshField 'initializePending' $false
    $second=Invoke-RefreshMethod $refresh $null ([object[]]@())
    $secondAgain=Invoke-RefreshMethod $refresh $null ([object[]]@())
    $secondCompletion=Get-RefreshField 'pendingRefresh'
    Confirm-Refresh (-not [Object]::ReferenceEquals($first,$second) -and [Object]::ReferenceEquals($second,$secondAgain) -and -not $second.IsCompleted) 'A request during an in-flight generation gets one distinct coalesced queued generation'
    Confirm-Refresh ([Object]::ReferenceEquals($secondCompletion,(Get-RefreshField 'pendingRefresh')) -and (Get-RefreshField 'initializePending')) 'The queued generation retains its own completion slot'
    $success=New-RefreshResult $true $true (New-RefreshWarnings @())
    $completed=[bool](Invoke-RefreshMethod ($firstCompletion.GetType().GetMethod('TrySetResult')) $firstCompletion ([object[]]@($success)))
    Confirm-Refresh ($completed -and $first.IsCompleted -and -not $first.IsFaulted -and -not $second.IsCompleted) 'Finishing the older generation does not prematurely finish its queued successor'
    Confirm-Refresh ([Object]::ReferenceEquals($first.Result,$success) -and [Object]::ReferenceEquals((Get-RefreshField 'pendingRefresh'),$secondCompletion)) 'The older result and newer pending slot remain independent'
    Set-RefreshField 'pendingRefresh' $null
    Set-RefreshField 'initializePending' $false
    $warningResult=New-RefreshResult $true $false (New-RefreshWarnings @('Fixture shortcut warning'))
    $completed=[bool](Invoke-RefreshMethod ($secondCompletion.GetType().GetMethod('TrySetResult')) $secondCompletion ([object[]]@($warningResult)))
    Confirm-Refresh ($completed -and $second.IsCompleted -and -not $second.IsFaulted -and [Object]::ReferenceEquals($second.Result,$warningResult)) 'The second generation completes with its own bounded warning result'
    foreach($menu in @($false,$true)){
        foreach($shortcuts in @($false,$true)){
            $result=New-RefreshResult $menu $shortcuts (New-RefreshWarnings @())
            Confirm-Refresh ((Get-RefreshProperty $result 'MenuUpdated') -eq $menu -and (Get-RefreshProperty $result 'ShortcutsUpdated') -eq $shortcuts -and
                -not (Get-RefreshProperty $result 'HasWarnings') -and -not [String]::IsNullOrWhiteSpace((Get-RefreshProperty $result 'Message'))) 'Refresh result state independently describes menu and shortcut outcomes'
        }
    }
    $warningSource=New-RefreshWarnings @('Fixture first warning','Fixture second warning')
    $copied=New-RefreshResult $true $true $warningSource
    $warningSource[0]='Changed caller-owned list'
    $returned=Get-RefreshProperty $copied 'Warnings'
    $returned[1]='Changed returned array'
    $reread=Get-RefreshProperty $copied 'Warnings'
    Confirm-Refresh ($reread.Count -eq 2 -and $reread[0] -ceq 'Fixture first warning' -and $reread[1] -ceq 'Fixture second warning' -and
        (Get-RefreshProperty $copied 'HasWarnings')) 'Results defensively copy both incoming and returned warning collections'
    $many=New-RefreshWarnings @()
    for($warningIndex=0;$warningIndex -lt 12;$warningIndex++){$many.Add('Fixture warning '+$warningIndex)}
    $bounded=New-RefreshResult $false $false $many
    Confirm-Refresh ((Get-RefreshProperty $bounded 'Warnings').Count -eq 8 -and (Get-RefreshProperty $bounded 'Warnings')[7] -ceq 'Fixture warning 7') 'A result stores at most eight warnings even when a caller supplies more'
    $deduplicated=New-RefreshWarnings @()
    $addWarning=$service.GetMethod('AddWarning',$static)
    for($warningIndex=0;$warningIndex -lt 12;$warningIndex++){
        $null=Invoke-RefreshMethod $addWarning $null ([object[]]@($deduplicated,('Fixture warning '+$warningIndex)))
        $null=Invoke-RefreshMethod $addWarning $null ([object[]]@($deduplicated,('Fixture warning '+$warningIndex)))
    }
    Confirm-Refresh ($deduplicated.Count -eq 8 -and @($deduplicated|Select-Object -Unique).Count -eq 8) 'Refresh warning aggregation deduplicates reasons and enforces the same eight-warning bound'
    $failed=Invoke-RefreshMethod ($service.GetMethod('CompletedFailure',$static)) $null ([object[]]@('Fixture optional failure'))
    Confirm-Refresh ($failed.IsCompleted -and -not $failed.IsFaulted -and -not (Get-RefreshProperty $failed.Result 'MenuUpdated') -and
        -not (Get-RefreshProperty $failed.Result 'ShortcutsUpdated') -and (Get-RefreshProperty $failed.Result 'Warnings')[0] -ceq 'Fixture optional failure') 'An optional refresh failure is a completed result with a warning, rather than a faulted task'
    Confirm-Refresh ((Get-RefreshField 'workerRunning') -and (Get-RefreshField 'pendingFiles').Count -eq $pendingFileCount) 'The generation fixture did not start a thread or enqueue a recent document'
}finally{
    foreach($name in $fieldNames){Set-RefreshField $name $savedFields[$name]}
}
Confirm-Refresh (-not (Get-RefreshField 'identityReady') -and -not (Get-RefreshField 'workerRunning') -and $null -eq (Get-RefreshField 'pendingRefresh')) 'All service generation fields were restored before native fixture checks'

# Native ShellLink tests are confined to a newly created private fixture folder.
# Creating, loading and saving these LNK files never executes their targets.
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
$fixtureFolder=Join-Path $output ('refresh-fixture-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
if($fixtureFolder.Length -gt 220){throw 'The private fixture path is too long for bounded Shell change notifications.'}
if([IO.Directory]::Exists($fixtureFolder) -or [IO.File]::Exists($fixtureFolder)){throw 'The private fixture path already exists; no shortcut was written.'}
[IO.Directory]::CreateDirectory($fixtureFolder)|Out-Null
$create=$service.GetMethod('CreateLink',$static)
$repair=$service.GetMethod('RepairOwnedShortcut',$static)
$setProperty=$service.GetMethod('SetStringProperty',$static)
$clear=$service.GetMethod('PropVariantClear',$static)
$linkType=$service.GetNestedType('IShellLinkW',$instance)
$linkComType=$service.GetNestedType('ShellLinkComObject',$instance)
$storeType=$service.GetNestedType('IPropertyStore',$instance)
$keyType=$service.GetNestedType('PropertyKey',$instance)
$variantType=$service.GetNestedType('PropVariant',$instance)
$persistType=[Runtime.InteropServices.ComTypes.IPersistFile]
$appIdKey=$service.GetProperty('ApplicationIdKey',$static).GetValue($null,$null)
function Release-RefreshCom($Value){
    if($null -ne $Value){$raw=$Value.PSObject.BaseObject;if([Runtime.InteropServices.Marshal]::IsComObject($raw)){[void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($raw)}}
}
function Invoke-RefreshLink($Link,[string]$Name,[object[]]$Arguments){
    $result=Invoke-RefreshMethod ($linkType.GetMethod($Name)) $Link $Arguments
    if($result -lt 0){[Runtime.InteropServices.Marshal]::ThrowExceptionForHR($result)}
}
function Read-RefreshStringProperty($Link,[Guid]$Format,[uint32]$Id){
    $key=[Activator]::CreateInstance($keyType)
    $keyType.GetField('Format',$instance).SetValue($key,$Format)
    $keyType.GetField('Id',$instance).SetValue($key,$Id)
    [object[]]$arguments=@($key,[Activator]::CreateInstance($variantType))
    $code=Invoke-RefreshMethod ($storeType.GetMethod('GetValue')) $Link $arguments
    $variant=$arguments[1]
    try{
        if($code -lt 0){[Runtime.InteropServices.Marshal]::ThrowExceptionForHR($code)}
        if($variantType.GetField('Kind',$instance).GetValue($variant) -ne 31){throw 'Expected a native Unicode string property in the fixture.'}
        return [Runtime.InteropServices.Marshal]::PtrToStringUni($variantType.GetField('Pointer',$instance).GetValue($variant))
    }finally{
        $code=Invoke-RefreshMethod $clear $null ([object[]]@($variant))
        if($code -lt 0){[Runtime.InteropServices.Marshal]::ThrowExceptionForHR($code)}
    }
}
function Read-RefreshShortcut([string]$Path){
    $link=$null
    try{
        # Read through a genuinely empty ShellLink, so properties from the
        # fixture creator cannot mask an incomplete persisted repair.
        $link=[Activator]::CreateInstance([type]::GetTypeFromCLSID($linkComType.GUID))
        $null=Invoke-RefreshMethod ($persistType.GetMethod('Load')) $link ([object[]]@($Path,[int]0))
        $target=New-Object Text.StringBuilder(32768)
        Invoke-RefreshLink $link 'GetPath' @($target,$target.Capacity,[IntPtr]::Zero,[uint32]4)
        $arguments=New-Object Text.StringBuilder(32768)
        Invoke-RefreshLink $link 'GetArguments' @($arguments,$arguments.Capacity)
        $description=New-Object Text.StringBuilder(32768)
        Invoke-RefreshLink $link 'GetDescription' @($description,$description.Capacity)
        $directory=New-Object Text.StringBuilder(32768)
        Invoke-RefreshLink $link 'GetWorkingDirectory' @($directory,$directory.Capacity)
        $icon=New-Object Text.StringBuilder(32768)
        [object[]]$iconArguments=@($icon,$icon.Capacity,[int]0)
        Invoke-RefreshLink $link 'GetIconLocation' $iconArguments
        return [pscustomobject]@{Target=$target.ToString();Arguments=$arguments.ToString();Description=$description.ToString();WorkingDirectory=$directory.ToString();Icon=$icon.ToString();IconIndex=$iconArguments[2];Title=(Read-RefreshStringProperty $link ([Guid]'F29F85E0-4FF9-1068-AB91-08002B27B3D9') 2);AppId=(Read-RefreshStringProperty $link ([Guid]'9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3') 5)}
    }finally{Release-RefreshCom $link}
}
function New-RefreshShortcut([string]$Path,[string]$Target,[string]$Arguments,[string]$Title,[string]$AppId){
    $link=$null
    try{
        $link=Invoke-RefreshMethod $create $null ([object[]]@($Target,$Arguments,$Title,'Private shortcut repair fixture'))
        $null=Invoke-RefreshMethod $setProperty $null ([object[]]@($link,$appIdKey,$AppId))
        $code=Invoke-RefreshMethod ($storeType.GetMethod('Commit')) $link ([object[]]@())
        if($code -lt 0){[Runtime.InteropServices.Marshal]::ThrowExceptionForHR($code)}
        $null=Invoke-RefreshMethod ($persistType.GetMethod('Save')) $link ([object[]]@($Path,$true))
    }finally{Release-RefreshCom $link}
}
$ownedPath=Join-Path $fixtureFolder 'owned.lnk'
$ownedArguments='"C:\Synthetic Snips\sample image.png"'
New-RefreshShortcut $ownedPath $app $ownedArguments 'sample image.png' 'AutomationForge.SnipStudio.LegacyFixture'
$before=Read-RefreshShortcut $ownedPath
$beforeHash=(Get-FileHash -LiteralPath $ownedPath -Algorithm SHA256).Hash
Confirm-Refresh ($before.AppId -ceq 'AutomationForge.SnipStudio.LegacyFixture' -and [String]::Equals($before.Target,$app,[StringComparison]::OrdinalIgnoreCase)) 'The on-disk owned fixture begins with a mismatched legacy AppID and the exact app target'
$null=Invoke-RefreshMethod $repair $null ([object[]]@($ownedPath,$app))
$after=Read-RefreshShortcut $ownedPath
Confirm-Refresh ($after.AppId -ceq 'AutomationForge.SnipStudio' -and (Get-FileHash -LiteralPath $ownedPath -Algorithm SHA256).Hash -cne $beforeHash) 'Repair persists the corrected AppID through a fresh read/write ShellLink property store'
foreach($name in @('Target','WorkingDirectory','Icon')){
    Confirm-Refresh ([String]::Equals($before.$name,$after.$name,[StringComparison]::OrdinalIgnoreCase)) ('Owned shortcut repair preserves '+$name)
}
foreach($name in @('Arguments','Title','Description','IconIndex')){
    Confirm-Refresh ($before.$name -ceq $after.$name) ('Owned shortcut repair preserves '+$name)
}
$repairedHash=(Get-FileHash -LiteralPath $ownedPath -Algorithm SHA256).Hash
$sentinelTime=[DateTime]::SpecifyKind([DateTime]'2001-01-01T00:00:00',[DateTimeKind]::Utc)
[IO.File]::SetLastWriteTimeUtc($ownedPath,$sentinelTime)
$null=Invoke-RefreshMethod $repair $null ([object[]]@($ownedPath,$app))
Confirm-Refresh ((Get-FileHash -LiteralPath $ownedPath -Algorithm SHA256).Hash -ceq $repairedHash -and
    [IO.File]::GetLastWriteTimeUtc($ownedPath) -eq $sentinelTime) 'Refreshing an already correct owned link preserves both bytes and last-write time'
$foreignTarget=Join-Path $fixtureFolder 'different-signed-target.exe'
[IO.File]::Copy($app,$foreignTarget,$false)
$foreignPath=Join-Path $fixtureFolder 'unrelated.lnk'
New-RefreshShortcut $foreignPath $foreignTarget '--capture --delay 5' 'Unrelated target fixture' 'AutomationForge.Unrelated.Fixture'
$foreignHash=(Get-FileHash -LiteralPath $foreignPath -Algorithm SHA256).Hash
[IO.File]::SetLastWriteTimeUtc($foreignPath,$sentinelTime)
$null=Invoke-RefreshMethod $repair $null ([object[]]@($foreignPath,$app))
$foreign=Read-RefreshShortcut $foreignPath
Confirm-Refresh ((Get-FileHash -LiteralPath $foreignPath -Algorithm SHA256).Hash -ceq $foreignHash -and
    [IO.File]::GetLastWriteTimeUtc($foreignPath) -eq $sentinelTime -and $foreign.AppId -ceq 'AutomationForge.Unrelated.Fixture' -and
    [String]::Equals($foreign.Target,$foreignTarget,[StringComparison]::OrdinalIgnoreCase)) 'A different executable target is left byte-for-byte untouched, including its existing AppID'
$missingPath=Join-Path $fixtureFolder 'missing.lnk'
$null=Invoke-RefreshMethod $repair $null ([object[]]@($missingPath,$app))
Confirm-Refresh (-not [IO.File]::Exists($missingPath)) 'Refresh does not create a missing shortcut or pin'
Confirm-Refresh (-not (Get-RefreshField 'identityReady') -and -not (Get-RefreshField 'workerRunning') -and
    -not (Get-RefreshField 'shellReady') -and $null -eq (Get-RefreshField 'pendingRefresh')) 'Native fixture repair did not initialize the global taskbar service or schedule a refresh worker'
$report=[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();FixtureDirectory=[IO.Path]::GetFileName($fixtureFolder);FixtureFiles=@('owned.lnk','unrelated.lnk','different-signed-target.exe');ApplicationEntrypointExecuted=$false;ApplicationWindowsOpened=$false;NativeBrokerCreated=$false;RealRefreshWorkerExecuted=$false;RegistryOrFileAssociationsChanged=$false;RecentFilesRegistered=$false;ActualPinnedOrStartMenuShortcutsChanged=$false;Scope='Managed refresh-generation coalescing/result policies, plus on-disk ShellLink property-store repair only inside a new private fixture directory. Shell item/directory notifications are limited to those private fixture paths. No shortcut target is executed.';Unverified='Actual installed/pinned shortcut migration, Explorer taskbar cache refresh, visible menu behavior, file associations, recent-file acceptance, real worker UI reporting and screenshot capture.'}
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'taskbar-refresh.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' taskbar refresh generation and private ShellLink fixture checks.')
