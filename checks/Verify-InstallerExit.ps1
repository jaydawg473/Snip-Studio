# Signed-library protocol and dispatch checks. No application entrypoint,
# native broker/window registration, message send, shown form, recording,
# clipboard, capture, input, preferences or registry operation is performed.
param([string]$ReleaseFolder='release-0.5.5-signed',[string]$ExpectedVersion='0.5.5.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$protocol=$assembly.GetType('SnipStudio.InstallerExitProtocol',$true)
$hostType=$assembly.GetType('SnipStudio.ShortcutHost',$true)
$contextType=$assembly.GetType('SnipStudio.StudioApplicationContext',$true)
$formType=$assembly.GetType('SnipStudio.MainForm',$true)
$prefsType=$assembly.GetType('SnipStudio.Preferences',$true)
$valid=$protocol.GetMethod('IsValid',$static)
$requests=$protocol.GetMethod('RequestsExit',$static)
$accept=$hostType.GetMethod('AcceptInstallerExit',$instance)
$dispatch=$hostType.GetMethod('DispatchOne',$instance)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Exit([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Get-ExitField($Object,[string]$Name){return $Object.GetType().GetField($Name,$instance).GetValue($Object)}
function Set-ExitField($Object,[string]$Name,$Value){$Object.GetType().GetField($Name,$instance).SetValue($Object,$Value)}
function Invoke-Protocol([Reflection.MethodInfo]$Method,[long]$Magic,[long]$Operation){return [bool]$Method.Invoke($null,[object[]]@([IntPtr]::new($Magic),[IntPtr]::new($Operation)))}
function New-ConstructorFreeHost {
    # This fixture never executes ShortcutHost's native-window constructor.
    $value=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($hostType)
    Set-ExitField $value 'uiThreadId' ([Threading.Thread]::CurrentThread.ManagedThreadId)
    $pendingField=$hostType.GetField('pending',$instance)
    $pendingField.SetValue($value,[Activator]::CreateInstance($pendingField.FieldType))
    return $value
}
Confirm-Exit ($protocol.GetField('MessageName',$static).GetRawConstantValue() -ceq 'AutomationForge.SnipStudio.RequestInstallerExit.v1') 'The installer and app share the fixed registered-message name'
Confirm-Exit ([int]$protocol.GetField('Magic',$static).GetRawConstantValue() -eq 0x5355) 'The acknowledgement and protocol magic remain 0x5355'
Confirm-Exit (Invoke-Protocol $valid 0x5355 1) 'A capability probe is a valid protocol operation'
Confirm-Exit (-not (Invoke-Protocol $requests 0x5355 1)) 'A capability probe never requests application exit'
Confirm-Exit (Invoke-Protocol $valid 0x5355 0) 'An approved exit request is a valid operation'
Confirm-Exit (Invoke-Protocol $requests 0x5355 0) 'Only the explicit request operation requests exit'
foreach($pair in @(@([long]0,[long]0),@([long]0x5354,[long]0),@([long]0x5355,[long]-1),@([long]0x5355,[long]2),@([long]-1,[long]1))){
    Confirm-Exit (-not (Invoke-Protocol $valid $pair[0] $pair[1])) ('Invalid magic/operation '+$pair[0]+'/'+$pair[1]+' is rejected')
}
if([IntPtr]::Size -eq 8){Confirm-Exit (-not (Invoke-Protocol $valid ([long]4294967296+0x5355) 0)) 'High pointer bits cannot truncate into valid protocol magic'}
$fixtureHost=New-ConstructorFreeHost
Set-ExitField $fixtureHost 'dispatchPosted' $true
$script:installerExitEvents=0
$script:installerWasDispatching=$false
$script:duplicateDuringDispatchAccepted=$false
$onExit={
    $script:installerExitEvents++
    $script:installerWasDispatching=[bool](Get-ExitField $fixtureHost 'installerExitDispatching')
    $script:duplicateDuringDispatchAccepted=[bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::Zero))
}
$hostType.GetField('ExitRequested',$instance).SetValue($fixtureHost,[Action]$onExit)
$probeAccepted=[bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::new(1)))
Confirm-Exit ($probeAccepted -and -not [bool](Get-ExitField $fixtureHost 'installerExitPending') -and $script:installerExitEvents -eq 0) 'Capability probing acknowledges without pending state or an exit event'
$requestAccepted=[bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::Zero))
Confirm-Exit ($requestAccepted -and [bool](Get-ExitField $fixtureHost 'installerExitPending') -and $script:installerExitEvents -eq 0) 'An approved request queues exit without invoking application code inline'
$duplicate=[bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::Zero))
Confirm-Exit ($duplicate -and [bool](Get-ExitField $fixtureHost 'installerExitPending') -and $script:installerExitEvents -eq 0) 'Duplicate pending requests coalesce before dispatch'
$null=$dispatch.Invoke($fixtureHost,[object[]]@())
Confirm-Exit ($script:installerExitEvents -eq 1 -and $script:installerWasDispatching) 'Deferred dispatch raises exactly one exit event under its coalescing guard'
Confirm-Exit ($script:duplicateDuringDispatchAccepted -and -not [bool](Get-ExitField $fixtureHost 'installerExitPending')) 'A duplicate during a save-dialog dispatch is acknowledged without scheduling another exit'
Confirm-Exit (-not [bool](Get-ExitField $fixtureHost 'installerExitDispatching')) 'The dispatching guard is released after the event returns'
$null=$dispatch.Invoke($fixtureHost,[object[]]@())
Confirm-Exit ($script:installerExitEvents -eq 1) 'A second empty dispatch cannot repeat the exit event'
Set-ExitField $fixtureHost 'disposed' $true
Confirm-Exit (-not [bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::new(1)))) 'A disposed host refuses probes'
Confirm-Exit (-not [bool]$accept.Invoke($fixtureHost,[object[]]@([IntPtr]::new(0x5355),[IntPtr]::Zero))) 'A disposed host refuses exit requests'

# A constructor-free nonprimary host makes ApplicationContext lifecycle
# testable without a broker HWND, hotkey, tray icon or application message loop.
$contextHost=New-ConstructorFreeHost
$prefs=[Activator]::CreateInstance($prefsType)
$contextCtor=$contextType.GetConstructor($instance,$null,[type[]]@($hostType,$prefsType),$null)
$context=$contextCtor.Invoke([object[]]@($contextHost,$prefs))
$script:exitThreadEvents=0
$threadExit=[EventHandler]{$script:exitThreadEvents++}
$context.add_ThreadExit($threadExit)
try{
    $null=$contextType.GetMethod('RequestGracefulExit',$instance).Invoke($context,[object[]]@())
    Confirm-Exit ([bool](Get-ExitField $context 'exitRequested') -and [bool](Get-ExitField $context 'exiting') -and $script:exitThreadEvents -eq 1) 'An idle listener with no editors exits its application context normally'
    $null=$contextType.GetMethod('RequestGracefulExit',$instance).Invoke($context,[object[]]@())
    Confirm-Exit ($script:exitThreadEvents -eq 1) 'Repeated graceful exit after completion is idempotent'
}finally{$context.remove_ThreadExit($threadExit);$context.Dispose()}

# Inspect the signed call graph for work-preserving UI paths without opening
# save dialogs or starting capture/recording during these component checks.
$opcodeMap=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null);$opcodeMap[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-ExitCalls([Reflection.MethodBase]$Method){
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $op=$opcodeMap[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $op){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$op.OperandType.ToString();$length=0
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod'){$target=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset));[pscustomobject]@{Method=$target;Name=$target.Name;Type=$target.DeclaringType.FullName;Opcode=$op.Name}}
        $offset+=$length
    }
}
$acceptCalls=@(Read-ExitCalls $accept)
Confirm-Exit (@($acceptCalls|Where-Object {$_.Name -eq 'Invoke'}).Count -eq 0) 'The native request handler cannot directly invoke an exit callback or save prompt'
Confirm-Exit (@($acceptCalls|Where-Object {$_.Name -eq 'ScheduleDispatch'}).Count -eq 1) 'The native request schedules ordinary deferred UI dispatch'
$graceful=$contextType.GetMethod('RequestGracefulExit',$instance)
$gracefulCalls=@(Read-ExitCalls $graceful)
$editorEnumerable=([Collections.Generic.IEnumerable[object]]).GetGenericTypeDefinition().MakeGenericType([type[]]@($formType))
$scopedExit=$formType.GetMethod('ExitFromTray',$instance,$null,[type[]]@($editorEnumerable),$null)
Confirm-Exit ($null -ne $scopedExit -and @($gracefulCalls|Where-Object {$_.Method -eq $scopedExit}).Count -eq 1) 'Cooperative exit passes the tracked-editor snapshot to the normal tray-exit path'
Confirm-Exit (@($gracefulCalls|Where-Object {$_.Name -eq 'RequestExit'}).Count -eq 1) 'Zero-editor cooperative exit retains the ordinary context shutdown path'
$exitCalls=@(Read-ExitCalls $scopedExit)
Confirm-Exit (@($exitCalls|Where-Object {$_.Name -eq 'ModalOpenCount'}).Count -eq 1) 'Normal exit retains its modal-dialog refusal and activation path'
Confirm-Exit (@($exitCalls|Where-Object {$_.Name -eq 'get_OpenForms'}).Count -ge 2) 'Modal-dialog activation and tracked-editor supplementation both enumerate currently open windows'
Confirm-Exit (@($exitCalls|Where-Object {$_.Name -eq 'Close'}).Count -eq 1) 'Every approved editor uses normal Form.Close rather than process termination'
Confirm-Exit (@($exitCalls|Where-Object {$_.Name -eq 'CancelApplicationExit'}).Count -eq 1) 'A canceled editor close restores the application session'
$contextCtorCalls=@(Read-ExitCalls $contextCtor)
Confirm-Exit (@($contextCtorCalls|Where-Object {$_.Method -eq $graceful -and $_.Opcode -eq 'ldftn'}).Count -eq 1) 'The host ExitRequested event is wired to the graceful all-editor path'
$startupType=$assembly.GetType('SnipStudio.ShortcutStartupRequest',$true)
$parse=$startupType.GetMethod('TryFromArguments',$static)
$parseArgs=New-Object object[] 2;$parseArgs[0]=[string[]]@('--exit');$parseArgs[1]=$null
Confirm-Exit (-not [bool]$parse.Invoke($null,$parseArgs)) 'The ordinary image-opening command line still rejects --exit'
Confirm-Exit ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'No application form was opened by the checks'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Scope='Signed protocol policy, constructor-free broker queue/dispatch, idle ApplicationContext lifecycle and work-preserving call metadata. No native message registration/send, visible form, process close or device access.';Unverified='Actual Setup consent dialog, cross-process native message delivery, live unsaved-work cancellation, recording finalization and full update flow.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'installer-exit.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed cooperative-installer-exit checks. No application, native broker or process was launched or closed.')
