# Signed-library checks. This never invokes prompt display/activation,
# an application entrypoint, native close/input, screenshots or preferences.
param(
    [ValidatePattern('\Arelease-\d+\.\d+\.\d+-signed\z')][string]$ReleaseFolder='release-0.6.1-signed',
    [ValidatePattern('\A\d+\.\d+\.\d+\.0\z')][string]$ExpectedVersion='0.6.1.0'
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed application is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -cne $ExpectedVersion){throw 'Unexpected signed application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$promptType=$assembly.GetType('SnipStudio.EditorClosePrompt',$true)
$queueType=$assembly.GetType('SnipStudio.EditorCloseQueue',$true)
$dialogType=$assembly.GetType('SnipStudio.UnsavedCloseDialog',$true)
$editorType=$assembly.GetType('SnipStudio.MainForm',$true)
$programType=$assembly.GetType('SnipStudio.Program',$true)
$classify=$promptType.GetMethod('IsCloseRequest',$static)
$fit=$promptType.GetMethod('FitToWorkingArea',$static)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Close([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-Close([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    if($null -eq $Method){throw 'A required signed method is absent.'}
    for($index=0;$index -lt $Arguments.Length;$index++){if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function Read-CloseControls([Windows.Forms.Control]$Root){
    foreach($control in $Root.Controls){$control;Read-CloseControls $control}
}

# The WM_CLOSE parameters are unused. SC_CLOSE uses the low command bits and
# must not overflow on a 64-bit process when other pointer bits are present.
foreach($case in @(
    @{Message=0x0010;Parameter=[long]0;Close=$true;Label='WM_CLOSE'},
    @{Message=0x0010;Parameter=[long]123456;Close=$true;Label='WM_CLOSE with unused parameter'},
    @{Message=0x0112;Parameter=[long]0xF060;Close=$true;Label='SC_CLOSE'},
    @{Message=0x0112;Parameter=[long]0xF061;Close=$true;Label='SC_CLOSE with reserved low bit'},
    @{Message=0x0112;Parameter=[long]0xF06F;Close=$true;Label='SC_CLOSE with all reserved low bits'},
    @{Message=0x0112;Parameter=[long]0xF020;Close=$false;Label='SC_MINIMIZE'},
    @{Message=0x0112;Parameter=[long]0xF030;Close=$false;Label='SC_MAXIMIZE'},
    @{Message=0x0112;Parameter=[long]0xF120;Close=$false;Label='SC_RESTORE'},
    @{Message=0x0112;Parameter=[long]0;Close=$false;Label='Empty system command'},
    @{Message=0x0100;Parameter=[long]0xF060;Close=$false;Label='Keyboard message'},
    @{Message=0x0201;Parameter=[long]0xF060;Close=$false;Label='Mouse message'},
    @{Message=0;Parameter=[long]0xF060;Close=$false;Label='WM_NULL'}
)){
    $actual=[bool](Invoke-Close $classify $null ([object[]]@([int]$case.Message,[IntPtr]::new($case.Parameter))))
    Confirm-Close ($actual -eq $case.Close) ($case.Label+' classification')
}
if([IntPtr]::Size -eq 8){
    foreach($command in @([long]0xF060,[long]0xF020)){
        $wide=[IntPtr]::new([long]4294967296+$command)
        $actual=[bool](Invoke-Close $classify $null ([object[]]@([int]0x0112,$wide)))
        Confirm-Close ($actual -eq ($command -eq 0xF060)) 'High pointer bits do not overflow or change the low system command'
    }
}

# Synthetic monitor work areas include taskbars, negative coordinates and
# oversized/high-DPI dialogs. No Screen, cursor or live desktop query is used.
$workAreas=@(
    [Drawing.Rectangle]::new(0,0,1920,1040),
    [Drawing.Rectangle]::new(-1920,0,1920,1040),
    [Drawing.Rectangle]::new(0,-2160,3840,2112),
    [Drawing.Rectangle]::new(-3840,-1200,3840,1160),
    [Drawing.Rectangle]::new(3840,1080,2560,1392),
    [Drawing.Rectangle]::new(0,48,1920,1032),
    [Drawing.Rectangle]::new(64,0,1856,1080),
    [Drawing.Rectangle]::new(500,-200,360,200),
    [Drawing.Rectangle]::new(-1,-1,1,1)
)
$sizes=@([Drawing.Size]::new(480,200),[Drawing.Size]::new(960,400),[Drawing.Size]::new(7680,4320),[Drawing.Size]::new(1,1))
$areaIndex=0
foreach($area in $workAreas){
    $areaIndex++
    foreach($size in $sizes){
        $bounds=[Drawing.Rectangle](Invoke-Close $fit $null ([object[]]@($area,$size)))
        $inside=$bounds.Width -ge 1 -and $bounds.Height -ge 1 -and $bounds.Left -ge $area.Left -and $bounds.Top -ge $area.Top -and $bounds.Right -le $area.Right -and $bounds.Bottom -le $area.Bottom
        $centered=[Math]::Abs((2L*$bounds.Left+$bounds.Width)-(2L*$area.Left+$area.Width)) -le 1 -and [Math]::Abs((2L*$bounds.Top+$bounds.Height)-(2L*$area.Top+$area.Height)) -le 1
        $notEnlarged=$bounds.Width -le $size.Width -and $bounds.Height -le $size.Height
        Confirm-Close ($inside -and $centered -and $notEnlarged) ('Work area '+$areaIndex+', requested '+$size.Width+'x'+$size.Height+': centered, fully contained and never enlarged')
    }
}

# The queue is exercised with blank, unshown Forms. These fixtures are never
# shown or closed; Dispose only releases these test-owned blank objects.
$queue=[Activator]::CreateInstance($queueType,$true)
$enqueue=$queueType.GetMethod('Enqueue',$instance)
$take=$queueType.GetMethod('TakeNext',$instance)
$clear=$queueType.GetMethod('Clear',$instance)
$count=$queueType.GetProperty('Count',$instance)
$first=[Windows.Forms.Form]::new()
$middle=[Windows.Forms.Form]::new()
$last=[Windows.Forms.Form]::new()
$current=[Windows.Forms.Form]::new()
try{
    Confirm-Close ([int]$count.GetValue($queue.PSObject.BaseObject,$null) -eq 0 -and $null -eq (Invoke-Close $take $queue ([object[]]@()))) 'A new close queue is empty'
    Confirm-Close (-not [bool](Invoke-Close $enqueue $queue ([object[]]@($null,$current)))) 'Null editor requests do not enter the queue'
    Confirm-Close (-not [bool](Invoke-Close $enqueue $queue ([object[]]@($current,$current)))) 'Retrying the current editor does not queue a second decision'
    foreach($editor in @($first,$middle,$last)){
        Confirm-Close ([bool](Invoke-Close $enqueue $queue ([object[]]@($editor,$current)))) 'A distinct existing editor can defer its close request'
    }
    Confirm-Close (-not [bool](Invoke-Close $enqueue $queue ([object[]]@($first,$current))) -and [int]$count.GetValue($queue.PSObject.BaseObject,$null) -eq 3) 'Duplicate close requests keep one entry per editor'
    $middle.Dispose()
    Confirm-Close (-not [bool](Invoke-Close $enqueue $queue ([object[]]@($middle,$current)))) 'An already-disposed editor is not enqueued'
    $next=Invoke-Close $take $queue ([object[]]@())
    Confirm-Close ([Object]::ReferenceEquals($next,$first)) 'Deferred close decisions preserve FIFO order'
    $next=Invoke-Close $take $queue ([object[]]@())
    Confirm-Close ([Object]::ReferenceEquals($next,$last) -and [int]$count.GetValue($queue.PSObject.BaseObject,$null) -eq 0) 'An editor disposed while waiting is skipped without losing the next editor'
    Confirm-Close ($null -eq (Invoke-Close $take $queue ([object[]]@()))) 'An exhausted close queue cannot repeat the last decision'
    for($retry=0;$retry -lt 256;$retry++){$null=Invoke-Close $enqueue $queue ([object[]]@($first,$current))}
    Confirm-Close ([int]$count.GetValue($queue.PSObject.BaseObject,$null) -eq 1) 'A close-message burst remains bounded by unique existing editors'
    $null=Invoke-Close $enqueue $queue ([object[]]@($last,$current))
    $null=Invoke-Close $clear $queue ([object[]]@())
    Confirm-Close ([int]$count.GetValue($queue.PSObject.BaseObject,$null) -eq 0 -and $null -eq (Invoke-Close $take $queue ([object[]]@()))) 'Canceling a burst removes all deferred requests'
    Confirm-Close (-not $first.IsDisposed -and -not $last.IsDisposed -and -not $current.IsDisposed) 'Clearing queued close requests does not dispose editors or their work'
    Confirm-Close (@(@($first,$middle,$last,$current)|Where-Object {$_.IsHandleCreated -or $_.Visible}).Count -eq 0) 'Queue checks never create HWNDs or show forms'
}finally{
    $null=Invoke-Close $clear $queue ([object[]]@())
    foreach($editor in @($first,$middle,$last,$current)){$editor.Dispose()}
}

# Construct and inspect only an unshown dialog. Do not call PrepareForShow,
# Present, Show, ShowDialog, Activate or AskToSave during these checks.
$constructor=$dialogType.GetConstructor($instance,$null,[type[]]@([Windows.Forms.Form]),$null)
Confirm-Close ($null -ne $constructor) 'The close dialog has an owner-aware constructor'
$dialog=$null
try{
    $dialog=$constructor.Invoke([object[]]@($null))
    $controls=@(Read-CloseControls $dialog)
    $buttons=@($controls|Where-Object {$_ -is [Windows.Forms.Button]})
    $save=@($buttons|Where-Object {$_.DialogResult -eq [Windows.Forms.DialogResult]::Yes})
    $discard=@($buttons|Where-Object {$_.DialogResult -eq [Windows.Forms.DialogResult]::No})
    $cancel=@($buttons|Where-Object {$_.DialogResult -eq [Windows.Forms.DialogResult]::Cancel})
    Confirm-Close ($buttons.Count -eq 3 -and $save.Count -eq 1 -and $discard.Count -eq 1 -and $cancel.Count -eq 1) 'Save, discard and cancel are three distinct explicit choices'
    Confirm-Close ($save[0].Text -ceq 'Save' -and $discard[0].Text -ceq "Don't save" -and $cancel[0].Text -ceq 'Cancel') 'Close choices use clear action labels'
    Confirm-Close ([Object]::ReferenceEquals($dialog.AcceptButton,$save[0])) 'Enter accepts Save rather than discarding work'
    Confirm-Close ([Object]::ReferenceEquals($dialog.CancelButton,$cancel[0])) 'Escape invokes Cancel'
    Confirm-Close ($dialog.DialogResult -eq [Windows.Forms.DialogResult]::None) 'Construction does not make an automatic save or discard decision'
    Confirm-Close ($dialog.ShowInTaskbar) 'The pending decision remains reachable from the taskbar'
    Confirm-Close ($dialog.StartPosition -eq [Windows.Forms.FormStartPosition]::Manual) 'Prompt placement is explicit rather than dependent on a stale parent position'
    Confirm-Close ($dialog.FormBorderStyle -eq [Windows.Forms.FormBorderStyle]::FixedDialog -and -not $dialog.MaximizeBox -and -not $dialog.MinimizeBox) 'The decision window has a standard bounded dialog frame'
    Confirm-Close (@($buttons|Where-Object {[String]::IsNullOrWhiteSpace($_.AccessibleName)}).Count -eq 0) 'Every decision button has an accessible action name'
    Confirm-Close (@($controls|Where-Object {$_ -is [Windows.Forms.Label] -and -not [String]::IsNullOrWhiteSpace($_.Text)}).Count -ge 2) 'The dialog explains the unsaved state and next action'
    Confirm-Close (-not $dialog.Visible -and -not $dialog.IsHandleCreated) 'Dialog inspection has neither shown a window nor created its HWND'
}finally{if($null -ne $dialog){$dialog.Dispose()}}

# Read call metadata from the signed application without invoking UI paths.
$opcodes=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null);$opcodes[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-CloseCalls([Reflection.MethodBase]$Method){
    if($null -eq $Method){throw 'A required signed method is missing.'}
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $instructionOffset=$offset;$code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $opcode=$opcodes[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $opcode){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$opcode.OperandType.ToString();$length=0
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod'){
            $called=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset))
            [pscustomobject]@{Offset=$instructionOffset;Method=$called;Name=$called.Name;Type=$called.DeclaringType.FullName;Opcode=$opcode.Name}
        }
        $offset+=$length
    }
}
$wndProcCalls=@(Read-CloseCalls ($editorType.GetMethod('WndProc',$instance)))
$pendingMessage=@($wndProcCalls|Where-Object {$_.Type -eq $promptType.FullName -and $_.Name -eq 'ActivatePendingForCloseMessage'})
$baseDispatch=@($wndProcCalls|Where-Object {$_.Name -eq 'WndProc' -and $_.Type -ne $editorType.FullName})
Confirm-Close ($pendingMessage.Count -eq 1 -and $baseDispatch.Count -ge 1 -and $pendingMessage[0].Offset -lt ($baseDispatch|Measure-Object Offset -Minimum).Minimum) 'Close-message retries check the existing pending dialog before base dispatch'
$onClosingMethod=$editorType.GetMethod('OnClosing',$instance,$null,[type[]]@([object],[Windows.Forms.FormClosingEventArgs]),$null)
$onClosingCalls=@(Read-CloseCalls $onClosingMethod)
$pendingEvent=@($onClosingCalls|Where-Object {$_.Type -eq $promptType.FullName -and $_.Name -eq 'DeferCloseIfPending'})
$deferred=@($onClosingCalls|Where-Object {$_.Name -in @('TryDelayCloseForScrollingCapture','TryDelayCloseForCaptureSave','StopRecording')})
Confirm-Close ($pendingEvent.Count -eq 1 -and $deferred.Count -ge 3 -and $pendingEvent[0].Offset -lt ($deferred|Measure-Object Offset -Minimum).Minimum) 'A reentrant close foregrounds the existing decision before capture or recording side effects'
Confirm-Close (@($onClosingCalls|Where-Object {$_.Name -eq 'ConfirmReplace'}).Count -eq 1 -and @($onClosingCalls|Where-Object {$_.Name -eq 'CancelApplicationExit'}).Count -ge 1) 'Normal close retains unsaved-work confirmation and application-exit cancellation'
Confirm-Close (@($onClosingCalls|Where-Object {$_.Type -eq $promptType.FullName -and $_.Name -eq 'CompleteHiddenClose'}).Count -eq 1) 'An opted-in tray hide can complete its place in the close queue'
Confirm-Close (@($onClosingMethod.GetMethodBody().ExceptionHandlingClauses|Where-Object {$_.Flags -eq [Reflection.ExceptionHandlingClauseOptions]::Finally}).Count -ge 1) 'The close-decision reentrancy flag is released through finally'
$replaceCalls=@(Read-CloseCalls ($editorType.GetMethod('ConfirmReplace',$instance)))
Confirm-Close (@($replaceCalls|Where-Object {$_.Type -eq $promptType.FullName -and $_.Name -eq 'AskToSave'}).Count -eq 1) 'The unsaved close branch reaches the new prompt'
Confirm-Close (@($replaceCalls|Where-Object {$_.Type -eq 'System.Windows.Forms.MessageBox' -and $_.Name -eq 'Show'}).Count -eq 1) 'Other unsaved replacement actions retain their existing confirmation'
Confirm-Close (@($replaceCalls|Where-Object {$_.Type -eq $editorType.FullName -and $_.Name -eq 'SaveImage'}).Count -eq 1) 'Saving still uses the existing SaveImage result rather than discarding or force-closing'
$askMethod=$promptType.GetMethod('AskToSave',$static)
$askCalls=@(Read-CloseCalls $askMethod)
Confirm-Close (@($askCalls|Where-Object {$_.Name -eq 'ShowDialog'}).Count -eq 1) 'A decision uses one owned modal dialog'
Confirm-Close (@($askMethod.GetMethodBody().ExceptionHandlingClauses|Where-Object {$_.Flags -eq [Reflection.ExceptionHandlingClauseOptions]::Finally}).Count -ge 1) 'Pending state cleanup is protected by finally'
$constructorCalls=@(Read-CloseCalls $constructor)
Confirm-Close (@($constructorCalls|Where-Object {$_.Name -in @('PrepareForShow','Present','SetForegroundWindow','Show','ShowDialog')}).Count -eq 0) 'Constructing the unshown fixture does not position, display or foreground a native window'
$activateCalls=@(Read-CloseCalls ($promptType.GetMethod('TryActivatePending',$static)))
Confirm-Close (@($activateCalls|Where-Object {$_.Name -eq 'Present'}).Count -eq 1 -and @($activateCalls|Where-Object {$_.Type -eq $dialogType.FullName -and $_.Name -eq '.ctor'}).Count -eq 0) 'Retry activation reuses the same dialog instead of allocating another decision'
$deferCalls=@(Read-CloseCalls ($promptType.GetMethod('DeferCloseIfPending',$static)))
Confirm-Close (@($deferCalls|Where-Object {$_.Type -eq $queueType.FullName -and $_.Name -eq 'Enqueue'}).Count -ge 1 -and @($deferCalls|Where-Object {$_.Name -eq 'Present'}).Count -eq 1) 'Close requests for other editors are retained while the current decision is foregrounded'
$trackCalls=@(Read-CloseCalls ($promptType.GetMethod('TrackOwner',$static)))
$closedCalls=@(Read-CloseCalls ($promptType.GetMethod('OnOwnerClosed',$static)))
Confirm-Close (@($trackCalls|Where-Object {$_.Name -eq 'add_FormClosed'}).Count -eq 1 -and @($closedCalls|Where-Object {$_.Name -eq 'remove_FormClosed'}).Count -eq 1 -and @($closedCalls|Where-Object {$_.Name -eq 'ResumeNext'}).Count -eq 1) 'Queue progression follows actual owner closure rather than a Save-button choice alone'
$resumeCalls=@(Read-CloseCalls ($promptType.GetMethod('ResumeNext',$static)))
Confirm-Close (@($resumeCalls|Where-Object {$_.Name -eq 'BeginInvoke'}).Count -eq 1 -and @($resumeCalls|Where-Object {$_.Name -eq 'TakeNext'}).Count -eq 1 -and @($resumeCalls|Where-Object {$_.Name -eq 'Close'}).Count -eq 0) 'The next queued editor is scheduled beyond the current close event'
$resumeCallback=@($resumeCalls|Where-Object {$_.Opcode -eq 'ldftn'})
Confirm-Close ($resumeCallback.Count -eq 1 -and @((Read-CloseCalls $resumeCallback[0].Method)|Where-Object {$_.Name -eq 'Close'}).Count -eq 1) 'Deferred progression uses normal Form.Close on the queued editor'
$cancelCalls=@(Read-CloseCalls ($promptType.GetMethod('CancelQueuedCloses',$static)))
Confirm-Close (@($cancelCalls|Where-Object {$_.Name -eq 'Clear'}).Count -eq 1 -and @($cancelCalls|Where-Object {$_.Name -eq 'remove_FormClosed'}).Count -eq 1) 'Cancel clears queued requests and releases the owner subscription'
$applicationCancelCalls=@(Read-CloseCalls ($programType.GetMethod('CancelApplicationExit',$static)))
Confirm-Close (@($applicationCancelCalls|Where-Object {$_.Type -eq $promptType.FullName -and $_.Name -eq 'CancelQueuedCloses'}).Count -eq 1) 'Save failure or cancellation clears the queued close burst through the existing application-cancel path'
$allCloseMethods=@($promptType.GetMethods($static)) + @($dialogType.GetMethods(($instance -bor $static -bor [Reflection.BindingFlags]::DeclaredOnly)))
$allCloseCalls=@(foreach($method in $allCloseMethods){Read-CloseCalls $method})
Confirm-Close (@($allCloseCalls|Where-Object {($_.Type -eq 'System.Diagnostics.Process' -and $_.Name -eq 'Kill') -or ($_.Type -eq 'System.Environment' -and $_.Name -in @('Exit','FailFast')) -or ($_.Type -eq 'System.Windows.Forms.Application' -and $_.Name -in @('Exit','ExitThread'))}).Count -eq 0) 'Prompt code contains no forced process or application termination path'

# These source-policy checks complement signed call metadata; they do not claim
# that source inspection proves native focus or actual Save/Cancel interaction.
$mainSourcePath=Join-Path $project 'MainForm.cs'
$promptSourcePath=Join-Path $project 'UnsavedCloseDialog.cs'
$source=[IO.File]::ReadAllText($mainSourcePath)
$promptSource=[IO.File]::ReadAllText($promptSourcePath)
$confirmation=[regex]::Match($source,'(?s)private bool ConfirmReplace\(string action\).*?(?=\r?\n        private void ChooseImage\()').Value
Confirm-Close (-not [String]::IsNullOrWhiteSpace($confirmation) -and $confirmation -match 'String\.Equals\(action,\s*"closing",\s*StringComparison\.Ordinal\)' -and $confirmation -match 'EditorClosePrompt\.AskToSave\(this\)') 'The custom prompt is limited to the explicit closing action'
Confirm-Close ($confirmation -match 'if\s*\(answer\s*==\s*DialogResult\.Cancel\)\s*return false;' -and $confirmation -match 'return answer\s*!=\s*DialogResult\.Yes\s*\|\|\s*SaveImage\(false\);') 'Cancel keeps the editor and a failed/canceled Save does not approve closure'
Confirm-Close ($source -match 'if\s*\(closeDecisionInProgress\)\s*\{\s*e\.Cancel\s*=\s*true;\s*return;' -and $source -match 'finally\s*\{\s*closeDecisionInProgress\s*=\s*false;\s*\}') 'A close retry during Save As cannot reenter the same editor decision'
Confirm-Close ($source -match 'catch\s*\{\s*Program\.CancelApplicationExit\(\);\s*throw;\s*\}') 'An exceptional save decision cancels queued closure before propagating the error'
Confirm-Close ($promptSource -match 'batchGeneration\+\+' -and $promptSource -match 'if\s*\(generation\s*!=\s*batchGeneration\s*\|\|') 'Cancel invalidates callbacks already posted for an earlier close burst'
Confirm-Close ($promptSource -match 'SetForegroundWindow\(Handle\)' -and $promptSource -notmatch 'AttachThreadInput|SetWindowsHookEx|AllowSetForegroundWindow|SetForegroundWindow\(editor') 'Foreground requests are limited to the pending dialog without global hooks or input attachment'
Confirm-Close ($promptSource -match '(?s)internal void Present\(\)\s*\{\s*if\s*\(IsDisposed\s*\|\|\s*Disposing\s*\|\|\s*presenting\s*\|\|\s*!Visible\s*\|\|\s*!IsHandleCreated\)\s*return;\s*PositionOnScreen\(\);') 'An unshown dialog returns before screen, cursor or foreground operations'
$presentCalls=@(Read-CloseCalls ($dialogType.GetMethod('Present',$instance)))
$visibleGuard=@($presentCalls|Where-Object {$_.Name -eq 'get_Visible'})
$handleGuard=@($presentCalls|Where-Object {$_.Name -eq 'get_IsHandleCreated'})
$positionCall=@($presentCalls|Where-Object {$_.Name -eq 'PositionOnScreen'})
Confirm-Close ($visibleGuard.Count -eq 1 -and $handleGuard.Count -eq 1 -and $positionCall.Count -eq 1 -and $visibleGuard[0].Offset -lt $positionCall[0].Offset -and $handleGuard[0].Offset -lt $positionCall[0].Offset) 'Signed presentation metadata checks visibility and handle before positioning'

# Exercise pending/canceled/restarted batches only with an unshown dialog.
# The preceding source/signed-call guards and assertions below establish that
# Present returns before its live Screen/cursor/foreground branch.
$pendingField=$promptType.GetField('pending',$static)
$ownerField=$promptType.GetField('batchOwner',$static)
$deferredField=$promptType.GetField('deferred',$static)
$generationField=$promptType.GetField('batchGeneration',$static)
$deferMethod=$promptType.GetMethod('DeferCloseIfPending',$static)
$cancelMethod=$promptType.GetMethod('CancelQueuedCloses',$static)
$pendingMethod=$promptType.GetMethod('TryActivatePending',$static)
$messageMethod=$promptType.GetMethod('ActivatePendingForCloseMessage',$static)
$fixtureOwner=[Windows.Forms.Form]::new()
$fixtureOther=[Windows.Forms.Form]::new()
$fixtureDialog=$null
try{
    $fixtureDialog=$constructor.Invoke([object[]]@($fixtureOwner.PSObject.BaseObject))
    Confirm-Close (@(@($fixtureOwner,$fixtureOther,$fixtureDialog)|Where-Object {$_.Visible -or $_.IsHandleCreated}).Count -eq 0) 'Coordinator fixtures are unshown and have no HWNDs before use'
    $coordinatorQueue=$deferredField.GetValue($null)
    Confirm-Close ($null -eq $pendingField.GetValue($null) -and $null -eq $ownerField.GetValue($null) -and [int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 0) 'The fresh component host has no real pending decision or queued editors'
    $pendingField.SetValue($null,$fixtureDialog.PSObject.BaseObject)
    Confirm-Close ([bool](Invoke-Close $deferMethod $null ([object[]]@($fixtureOther)))) 'A close request for another editor is deferred behind the pending decision'
    Confirm-Close ([Object]::ReferenceEquals($ownerField.GetValue($null),$fixtureOwner) -and [int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 1) 'A pending decision tracks its editor and one deferred request'
    $null=Invoke-Close $deferMethod $null ([object[]]@($fixtureOther))
    $null=Invoke-Close $deferMethod $null ([object[]]@($fixtureOwner))
    Confirm-Close ([int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 1) 'Same-editor and duplicate retries do not multiply pending decisions'
    $generation=[int]$generationField.GetValue($null)
    $null=Invoke-Close $cancelMethod $null ([object[]]@())
    Confirm-Close ([int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 0 -and $null -eq $ownerField.GetValue($null) -and [int]$generationField.GetValue($null) -eq ($generation+1)) 'Cancellation clears the batch and advances its callback generation'
    Confirm-Close ([Object]::ReferenceEquals($pendingField.GetValue($null),$fixtureDialog)) 'Cancellation of the batch preserves the currently open decision'
    $null=Invoke-Close $deferMethod $null ([object[]]@($fixtureOwner))
    Confirm-Close ($null -eq $ownerField.GetValue($null) -and [int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 0) 'Retrying only the pending editor cannot revive a canceled batch'
    $null=Invoke-Close $deferMethod $null ([object[]]@($fixtureOther))
    Confirm-Close ([Object]::ReferenceEquals($ownerField.GetValue($null),$fixtureOwner) -and [int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 1) 'A fresh other-editor request re-tracks the owner after cancellation'
    Confirm-Close ([bool](Invoke-Close $pendingMethod $null ([object[]]@()))) 'Retry activation recognizes the same guarded unshown fixture'
    Confirm-Close (-not [bool](Invoke-Close $messageMethod $null ([object[]]@($fixtureOther,[int]0,[IntPtr]::Zero)))) 'Non-close messages do not enter the pending close queue'
    Confirm-Close ([bool](Invoke-Close $messageMethod $null ([object[]]@($fixtureOther,[int]0x0010,[IntPtr]::Zero))) -and [int]$count.GetValue($coordinatorQueue.PSObject.BaseObject,$null) -eq 1) 'A close-message retry coalesces with its existing deferred editor'
    Confirm-Close (@(@($fixtureOwner,$fixtureOther,$fixtureDialog)|Where-Object {$_.Visible -or $_.IsHandleCreated}).Count -eq 0) 'Coordinator checks leave all fixtures unshown without HWNDs'
}finally{
    $pendingField.SetValue($null,$null)
    $null=Invoke-Close $cancelMethod $null ([object[]]@())
    if($null -ne $fixtureDialog){$fixtureDialog.Dispose()}
    $fixtureOwner.Dispose();$fixtureOther.Dispose()
}
Confirm-Close ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'No application form was opened by these checks'
$result=[ordered]@{
    Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();
    MainSourceSHA256=(Get-FileHash -LiteralPath $mainSourcePath -Algorithm SHA256).Hash;PromptSourceSHA256=(Get-FileHash -LiteralPath $promptSourcePath -Algorithm SHA256).Hash;
    Scope='Pure synthetic close-message/monitor geometry, unshown queue/coordinator/decision fixtures and signed call metadata, plus closing-source policy. Presentation returns before live screen/cursor/foreground work. No prompt display/activation, app entrypoint, native input/close, screenshot, clipboard or preference operation.';
    Unverified='Native foreground permission, actual taskbar Close all delivery, physical multi-monitor/DPI placement, mouse/keyboard Save/Don''t save/Cancel behavior, capture/recording finalization and installed-upgrade acceptance.'
}
$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'close-prompt-checks.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' close-prompt component/source-policy checks. No prompt was shown or activated.')
