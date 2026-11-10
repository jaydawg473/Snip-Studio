# Pure signed-library capture selection and call-graph checks only. Do not
# construct a form/scope, acquire an HWND, hide a window, wait for DWM, or take
# a screenshot. Native capture behavior still requires separate acceptance.
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
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$formType=$assembly.GetType('SnipStudio.MainForm',$true)
$scopeType=$assembly.GetType('SnipStudio.CaptureVisibilityScope',$true)
$preferencesType=$assembly.GetType('SnipStudio.Preferences',$true)
$policy=$scopeType.GetMethod('ShouldHideWindowForCapture',$static,$null,[type[]]@([bool],[bool],[bool],[bool]),$null)
if($null -eq $policy){throw 'The pure capture-window selection policy is unavailable.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Visibility([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Should-HideCaptureWindow([bool]$Editor,[bool]$Settings,[bool]$Initiator,[bool]$IncludeOpenSnips){
    [object[]]$arguments=@($Editor,$Settings,$Initiator,$IncludeOpenSnips)
    return [bool]$policy.Invoke($null,$arguments)
}

# Model actual application roles, without creating Form objects or relying on
# visibility/Handle getters. Other editor windows must stay eligible as content
# during ordinary captures; the explicit action retains its own editor too.
$windows=@(
    [pscustomobject]@{Name='Initiating editor';Editor=$true;Settings=$false;Initiator=$true},
    [pscustomobject]@{Name='Other snip A';Editor=$true;Settings=$false;Initiator=$false},
    [pscustomobject]@{Name='Other snip B';Editor=$true;Settings=$false;Initiator=$false},
    [pscustomobject]@{Name='Settings';Editor=$false;Settings=$true;Initiator=$false},
    [pscustomobject]@{Name='Unrelated window';Editor=$false;Settings=$false;Initiator=$false})
foreach($include in @($false,$true)){
    Confirm-Visibility ((Should-HideCaptureWindow $true $false $true $include) -eq (-not $include)) ('The initiating editor is hidden only for ordinary capture; include-open-snips='+$include)
    Confirm-Visibility (-not (Should-HideCaptureWindow $true $false $false $include)) ('Another editor remains visible; include-open-snips='+$include)
    Confirm-Visibility (Should-HideCaptureWindow $false $true $false $include) ('Settings remain excluded from capture; include-open-snips='+$include)
    Confirm-Visibility (-not (Should-HideCaptureWindow $false $false $false $include)) ('An unrelated window is outside application visibility changes; include-open-snips='+$include)
    $hidden=@($windows|Where-Object {Should-HideCaptureWindow $_.Editor $_.Settings $_.Initiator $include}|ForEach-Object Name)
    $expected=if($include){'Settings'}else{'Initiating editor,Settings'}
    Confirm-Visibility (($hidden -join ',') -ceq $expected) ('A multi-editor selection hides exactly the intended windows; include-open-snips='+$include)
}

# Inspect signed metadata, including async bodies, without executing any UI or
# capture method. Preserve instruction offsets and field writes for one-shot
# reset/order assertions; skip all native methods rather than calling them.
$opcodeMap=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null);$opcodeMap[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-VisibilityInstructions([Reflection.MethodBase]$Method){
    if($null -eq $Method){throw 'Expected signed capture method metadata is missing.'}
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $instructionOffset=$offset
        $code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $op=$opcodeMap[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $op){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$op.OperandType.ToString();$length=0;$target=$null;$literal=$null
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod'){$target=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset))}
        elseif($operand -eq 'InlineField'){$target=$Method.Module.ResolveField([BitConverter]::ToInt32($bytes,$offset))}
        elseif($op.Name -eq 'ldc.i4'){$literal=[BitConverter]::ToInt32($bytes,$offset)}
        elseif($op.Name -eq 'ldc.i4.s'){$literal=if($bytes[$offset] -gt 127){[int]$bytes[$offset]-256}else{[int]$bytes[$offset]}}
        elseif($op.Name -eq 'ldc.i4.m1'){$literal=-1}
        elseif($op.Name -match '\Aldc\.i4\.([0-8])\z'){$literal=[int]$Matches[1]}
        [pscustomobject]@{Offset=$instructionOffset;Opcode=$op.Name;Operand=$operand;Target=$target;Literal=$literal}
        $offset+=$length
    }
}
function Get-VisibilityCalls([object[]]$Instructions){return @($Instructions|Where-Object {$null -ne $_.Target -and $_.Target -is [Reflection.MethodBase]})}
function Async-VisibilityMethod([Reflection.MethodInfo]$Method){
    $attributes=@($Method.GetCustomAttributes($false)|Where-Object {$_.GetType().FullName -ceq 'System.Runtime.CompilerServices.AsyncStateMachineAttribute'})
    if($attributes.Count -ne 1){throw ('Expected an async state machine for '+$Method.Name)}
    return $attributes[0].StateMachineType.GetMethod('MoveNext',$instance)
}
function Find-VisibilityWrites([object[]]$Instructions,[Reflection.FieldInfo]$Field){
    for($index=0;$index -lt $Instructions.Count;$index++){
        if($Instructions[$index].Opcode -in @('stfld','stsfld') -and $Instructions[$index].Target -eq $Field){
            $prior=if($index -gt 0){$Instructions[$index-1]}else{$null}
            [pscustomobject]@{Offset=$Instructions[$index].Offset;Previous=$prior}
        }
    }
}
$normal=$scopeType.GetMethod('HideApplicationWindows',$static,$null,[type[]]@($formType),$null)
$selective=$scopeType.GetMethod('HideApplicationWindows',$static,$null,[type[]]@($formType,[bool]),$null)
$normalInstructions=@(Read-VisibilityInstructions $normal)
$normalCalls=@(Get-VisibilityCalls $normalInstructions)
$normalForward=@($normalCalls|Where-Object {$_.Target -eq $selective})
$normalFalse=$false
for($index=1;$index -lt $normalInstructions.Count;$index++){
    if($normalInstructions[$index].Target -eq $selective -and $normalInstructions[$index-1].Literal -ceq 0){$normalFalse=$true}
}
Confirm-Visibility ($normalForward.Count -eq 1 -and $normalFalse) 'The original one-argument capture API delegates with include-open-snips disabled'
$selectiveCalls=@(Get-VisibilityCalls @(Read-VisibilityInstructions $selective))
Confirm-Visibility (@($selectiveCalls|Where-Object {$_.Target -eq $policy}).Count -eq 1 -and
    @($selectiveCalls|Where-Object {$_.Target.Name -ceq 'ReferenceEquals' -and $_.Target.DeclaringType -eq [object]}).Count -ge 1) 'The actual window snapshot uses the pure selection policy and reference identity for its initiating editor'
Confirm-Visibility (@($selectiveCalls|Where-Object {$_.Target.Name -ceq 'CloseCapturePopups'}).Count -eq 1 -and
    @($selectiveCalls|Where-Object {$_.Target.Name -ceq 'ClosePopups'}).Count -eq 1) 'Capture preparation retains popup cleanup for the initiating editor and selected application windows'

$oneShot=$formType.GetField('captureOpenSnipsRequested',$instance)
Confirm-Visibility ($null -ne $oneShot -and $oneShot.FieldType -eq [bool] -and $oneShot.IsPrivate -and -not $oneShot.IsStatic -and
    $null -eq $preferencesType.GetField('captureOpenSnipsRequested',$instance)) 'Include-open-snips is private per-editor transient state rather than a saved preference'
$captureAction=$formType.GetMethod('CaptureOpenSnips',$instance)
$captureActionInstructions=@(Read-VisibilityInstructions $captureAction)
$actionCalls=@(Get-VisibilityCalls $captureActionInstructions)
$nullableInt=([Nullable[int]])
$startCapture=$formType.GetMethod('StartCapture',$instance,$null,[type[]]@($nullableInt),$null)
$actionWrites=@(Find-VisibilityWrites $captureActionInstructions $oneShot)
Confirm-Visibility (@($actionCalls|Where-Object {$_.Target.Name -ceq 'CanStartBackgroundCapture'}).Count -eq 1 -and
    @($actionCalls|Where-Object {$_.Target -eq $startCapture}).Count -eq 1) 'The explicit action retains the ordinary busy/modal capture guard and still-capture entrypoint'
Confirm-Visibility (@($actionWrites|Where-Object {$_.Previous.Literal -ceq 1}).Count -eq 1 -and
    @($actionWrites|Where-Object {$_.Previous.Literal -ceq 0}).Count -eq 1 -and
    @($captureAction.GetMethodBody().ExceptionHandlingClauses|Where-Object {$_.Flags -eq [Reflection.ExceptionHandlingClauseOptions]::Finally}).Count -ge 1) 'The explicit action enables one-shot inclusion and clears it in a finally block'
$startInstructions=@(Read-VisibilityInstructions (Async-VisibilityMethod $startCapture))
$startCalls=@(Get-VisibilityCalls $startInstructions)
$requestReads=@($startInstructions|Where-Object {$_.Opcode -ceq 'ldfld' -and $_.Target -eq $oneShot})
$requestWrites=@(Find-VisibilityWrites $startInstructions $oneShot)
$busyCalls=@($startCalls|Where-Object {$_.Target.Name -ceq 'CanStartBackgroundCapture'})
$scrollCalls=@($startCalls|Where-Object {$_.Target.Name -ceq 'StartScrollingCapture'})
Confirm-Visibility ($requestReads.Count -eq 1 -and $requestWrites.Count -eq 1 -and $requestWrites[0].Previous.Literal -ceq 0 -and
    $busyCalls.Count -eq 1 -and $scrollCalls.Count -eq 1 -and $requestReads[0].Offset -lt $requestWrites[0].Offset -and
    $requestWrites[0].Offset -lt $busyCalls[0].Offset -and $requestWrites[0].Offset -lt $scrollCalls[0].Offset) 'StartCapture consumes and clears one-shot inclusion before busy or scrolling early returns'
Confirm-Visibility (@($startInstructions|Where-Object {$_.Opcode -in @('stfld','stsfld') -and $null -ne $_.Target -and
    $_.Target.Name -in @('DelaySeconds','RememberCaptureDelay','CaptureModeIndex','sessionCaptureDelay')}).Count -eq 0) 'The one-shot capture path does not write remembered mode or timer preferences'
$hideCalls=@($startCalls|Where-Object {$_.Target -eq $selective})
$waitNoToken=$scopeType.GetMethod('WaitUntilHiddenAsync',$instance,$null,[type[]]@(),$null)
$waitCalls=@($startCalls|Where-Object {$_.Target -eq $waitNoToken})
$screenCalls=@($startCalls|Where-Object {$_.Target.Name -ceq 'Capture' -and $_.Target.DeclaringType.FullName -ceq 'SnipStudio.CaptureService'})
Confirm-Visibility ($hideCalls.Count -eq 1 -and $waitCalls.Count -eq 1 -and $screenCalls.Count -eq 1 -and
    $hideCalls[0].Offset -lt $waitCalls[0].Offset -and $waitCalls[0].Offset -lt $screenCalls[0].Offset) 'Still capture prepares its selected windows and invokes the compositor wait before reading screen pixels'
$waitWithToken=$scopeType.GetMethod('WaitUntilHiddenAsync',$instance,$null,[type[]]@([Threading.CancellationToken]),$null)
$waitBodyCalls=@(Get-VisibilityCalls @(Read-VisibilityInstructions (Async-VisibilityMethod $waitWithToken)))
Confirm-Visibility (@($waitBodyCalls|Where-Object {$_.Target.Name -ceq 'FlushCompositionAsync'}).Count -eq 2 -and
    @($waitBodyCalls|Where-Object {$_.Target.Name -ceq 'AllWindowsHidden'}).Count -ge 2 -and
    @($waitBodyCalls|Where-Object {$_.Target.Name -ceq 'CheckThreadAndLifetime'}).Count -ge 2) 'The existing hidden-window, lifetime and two-pass compositor guards remain in the awaited preparation path'
foreach($name in @('StartRecording','StartScrollingCapture')){
    $method=$formType.GetMethod($name,$instance)
    $calls=@(Get-VisibilityCalls @(Read-VisibilityInstructions (Async-VisibilityMethod $method)))
    Confirm-Visibility (@($calls|Where-Object {$_.Target -eq $normal}).Count -eq 1 -and
        @($calls|Where-Object {$_.Target -eq $selective}).Count -eq 0) ($name+' keeps the ordinary owner-only capture-visibility wrapper')
}

# Find the compiler-generated More-menu action by its signed call target. Only
# metadata is read; menu builders and delegates are never constructed/invoked.
$menuActionCount=0
$menuTypes=@($formType)+@($formType.GetNestedTypes([Reflection.BindingFlags]'Public,NonPublic'))
foreach($type in $menuTypes){
    foreach($method in $type.GetMethods($instance -bor $static -bor [Reflection.BindingFlags]::DeclaredOnly)){
        if($method.Name.IndexOf('BuildSettingsButton',[StringComparison]::Ordinal) -lt 0){continue}
        $menuActionCount+=@((Get-VisibilityCalls @(Read-VisibilityInstructions $method))|Where-Object {$_.Target -eq $captureAction}).Count
    }
}
Confirm-Visibility ($menuActionCount -eq 1) 'The More menu wires one Capture open snips action to the dedicated one-shot method'
Confirm-Visibility (-not [bool]$scopeType.GetField('captureActive',$static).GetValue($null) -and
    $null -eq $scopeType.GetField('outstandingFlush',$static).GetValue($null)) 'Pure policy checks did not enter the capture gate or schedule a compositor worker'
Confirm-Visibility ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'The checks did not construct or open application forms'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();ProductEntrypointExecuted=$false;FormsConstructed=$false;NativeWindowsHidden=$false;NativeDwmInvoked=$false;ScreenshotsTaken=$false;PreferencesOrRegistryAccessed=$false;Scope='Pure owner/other-editor/Settings window-role selection and signed one-shot, menu, recording/scrolling and compositor-wait call metadata only.';SourceReviewed='Explicit inclusion bypasses the scrolling workflow and uses Rectangle locally for a saved Scrolling choice without assigning the mode control; this branch was source-reviewed, not executed by these checks.';Unverified='Actual native hide/restore, physical compositor timing/ghosting, pixels captured with other editors, open-menu appearance, modal-dialog interaction, capture-mode fallback and countdown behavior.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'capture-visibility.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' pure signed capture-window visibility policy/call-graph checks; no native hiding or capture was used.')
