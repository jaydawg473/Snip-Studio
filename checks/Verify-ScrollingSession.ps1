# Actual signed-library scrolling-session orchestration, with a virtual clock
# and memory-only frames. No app entrypoint/window, desktop capture, native
# target binding, injected input, helper compilation, clipboard or preferences.
param([string]$ReleaseFolder='release-0.5.3-signed',[string]$ExpectedVersion='0.5.3.0')
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
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$serviceType=$assembly.GetType('SnipStudio.ScrollingCaptureService',$true)
$driverType=$assembly.GetType('SnipStudio.ScrollingCaptureDriver',$true)
$interruptionType=$assembly.GetType('SnipStudio.ScrollingCaptureInterruption',$true)
$reasonType=$assembly.GetType('SnipStudio.ScrollingCaptureEndReason',$true)
$progressType=$assembly.GetType('SnipStudio.ScrollingCaptureProgress',$true)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$sessionMethod=$serviceType.GetMethod('CaptureSessionAsync',$instance)
$interruptionCtor=$interruptionType.GetConstructor($instance,$null,[type[]]@($reasonType,[string]),$null)
$interruptionDelegateType=([Func[object]]).GetGenericTypeDefinition().MakeGenericType([type[]]@($interruptionType))
$reportDelegateType=([Action[object]]).GetGenericTypeDefinition().MakeGenericType([type[]]@($progressType))
$maxDuration=[int]$serviceType.GetField('MaximumDurationSeconds',$static).GetRawConstantValue()*1000
if($null -eq $sessionMethod -or $null -eq $interruptionCtor){throw 'The shared production session driver was not found.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
$cases=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Session([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Pixel-Hash([Drawing.Bitmap]$Bitmap){
    $data=$Bitmap.LockBits([Drawing.Rectangle]::new(0,0,$Bitmap.Width,$Bitmap.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes=New-Object byte[] ($Bitmap.Width*$Bitmap.Height*4)
    try{for($y=0;$y -lt $Bitmap.Height;$y++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$y*$data.Stride),$bytes,$y*$Bitmap.Width*4,$Bitmap.Width*4)}}finally{$Bitmap.UnlockBits($data)}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')}finally{$sha.Dispose()}
}
function New-Pattern([int]$Width,[int]$Height,[int]$Seed){
    $bitmap=[Drawing.Bitmap]::new($Width,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bytes=New-Object byte[] ($Width*$Height*4);[Random]::new($Seed).NextBytes($bytes)
    for($i=3;$i -lt $bytes.Length;$i+=4){$bytes[$i]=255}
    $data=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,$Width,$Height),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{[Runtime.InteropServices.Marshal]::Copy($bytes,0,$data.Scan0,$bytes.Length)}finally{$bitmap.UnlockBits($data)}
    return $bitmap
}
$document=New-Pattern 160 1200 95417
$unrelated=New-Pattern 160 240 83231
function Run-SessionScenario([string]$Mode,[int]$GoalFrames=0){
    $state=@{Mode=$Mode;Clock=[long]0;Offset=0;PendingAt=[long]-1;PendingOffset=0;DownCount=0;ReadCount=0;BadReads=0;Bad=$false;Lost=$false;Stop=$false;Goal=$GoalFrames;Accepted=1;ScrollTimes=(New-Object 'System.Collections.Generic.List[long]');Deltas=(New-Object 'System.Collections.Generic.List[int]');Frames=(New-Object 'System.Collections.Generic.List[System.Drawing.Bitmap]');Cancellation=[Threading.CancellationTokenSource]::new()}
    $doc=$document;$bad=$unrelated;$duration=$maxDuration;$interruptionConstructor=$interruptionCtor;$enumType=$reasonType
    $read={
        $state.ReadCount++
        if($state.ReadCount -gt 1000){throw 'Fixture exceeded its bounded frame-read budget.'}
        if($state.PendingAt -ge 0 -and $state.Clock -ge $state.PendingAt){$state.Offset=$state.PendingOffset;$state.PendingAt=[long]-1}
        if($state.Mode -eq 'broad-animation'){
            if($state.ReadCount%2 -eq 0){$frame=$bad.Clone([Drawing.Rectangle]::new(0,0,160,240),[Drawing.Imaging.PixelFormat]::Format32bppArgb)}
            else{$frame=$doc.Clone([Drawing.Rectangle]::new(0,0,160,240),[Drawing.Imaging.PixelFormat]::Format32bppArgb)}
        }elseif($state.Bad){
            $state.BadReads++
            $frame=$bad.Clone([Drawing.Rectangle]::new(0,0,160,240),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            if($state.BadReads -ge 3){
                if($state.Mode -eq 'target-lost'){$state.Lost=$true}
                if($state.Mode -eq 'cancel-before-recovery'){$state.Cancellation.Cancel()}
            }
        }elseif($state.Mode -eq 'bad-geometry' -and $state.DownCount -gt 0){
            $frame=[Drawing.Bitmap]::new(159,240)
        }else{$frame=$doc.Clone([Drawing.Rectangle]::new(0,$state.Offset,160,240),[Drawing.Imaging.PixelFormat]::Format32bppArgb)}
        if($state.Mode -eq 'localized-animation'){
            # A continuously changing 20-row full-width banner covers 8.3% of
            # the viewport, leaving distinctive anchors in all four quarters.
            $graphics=[Drawing.Graphics]::FromImage($frame)
            try{$graphics.FillRectangle($(if($state.ReadCount%2){[Drawing.Brushes]::Red}else{[Drawing.Brushes]::Blue}),0,210,160,20)}finally{$graphics.Dispose()}
        }
        if($state.Mode -eq 'badge-delayed' -and $state.ReadCount -gt 1 -and $state.Offset -eq 0){
            # This one-time badge update is not evidence that the scroll itself
            # responded; the delayed page movement has not happened yet.
            $graphics=[Drawing.Graphics]::FromImage($frame)
            try{$graphics.FillRectangle([Drawing.Brushes]::Green,0,210,160,20)}finally{$graphics.Dispose()}
        }
        $state.Frames.Add($frame)
        return $frame
    }.GetNewClosure()
    $wheel={param([int]$delta)
        $state.Deltas.Add($delta);$state.ScrollTimes.Add($state.Clock)
        if($delta -lt 0){
            $state.DownCount++
            switch($state.Mode){
                'delayed' {$state.PendingAt=$state.Clock+750;$state.PendingOffset=80}
                'badge-delayed' {$state.PendingAt=$state.Clock+750;$state.PendingOffset=80}
                'localized-animation' {$state.Offset=80}
                'broad-animation' { }
                'stalled' { }
                'scroll-failed' {return $false}
                'bad-geometry' { }
                default {
                    if($state.DownCount -eq 1){$state.Offset=60}
                    elseif($state.DownCount -eq 2){$state.Bad=$true;$state.BadReads=0}
                    else{$state.Offset=80;$state.Bad=$false}
                }
            }
        }elseif($state.Mode -ne 'recovery-failed'){$state.Bad=$false;$state.Offset=60}
        return $true
    }.GetNewClosure()
    $delay={param([int]$milliseconds,[Threading.CancellationToken]$token)
        $token.ThrowIfCancellationRequested()
        if($state.Mode -eq 'duration-limit'){$state.Clock+=$duration+1}else{$state.Clock+=[Math]::Max(1,$milliseconds)}
        return [Threading.Tasks.Task]::FromResult(0)
    }.GetNewClosure()
    $elapsed={return [long]$state.Clock}.GetNewClosure()
    $interrupt={
        if($state.Lost){return $interruptionConstructor.Invoke([object[]]@([Enum]::Parse($enumType,'TargetChanged'),'Synthetic target was lost.'))}
        if($state.Stop){return $interruptionConstructor.Invoke([object[]]@([Enum]::Parse($enumType,'UserStopped'),'Synthetic goal reached.'))}
        return $null
    }.GetNewClosure()
    $report={param($progress)
        $state.Accepted=$progress.FrameCount
        if($state.Goal -gt 0 -and $progress.FrameCount -ge $state.Goal){$state.Stop=$true}
    }.GetNewClosure()
    $driver=[Activator]::CreateInstance($driverType,$true)
    $driverType.GetField('ReadFrame',$instance).SetValue($driver,[Func[Drawing.Bitmap]]$read)
    $driverType.GetField('ScrollWheel',$instance).SetValue($driver,[Func[int,bool]]$wheel)
    $driverType.GetField('DelayAsync',$instance).SetValue($driver,[Func[int,Threading.CancellationToken,Threading.Tasks.Task]]$delay)
    $driverType.GetField('ElapsedMilliseconds',$instance).SetValue($driver,[Func[long]]$elapsed)
    $driverType.GetField('CheckInterruption',$instance).SetValue($driver,[Management.Automation.LanguagePrimitives]::ConvertTo($interrupt,$interruptionDelegateType))
    $reportDelegate=[Management.Automation.LanguagePrimitives]::ConvertTo($report,$reportDelegateType)
    $service=[Activator]::CreateInstance($serviceType)
    $result=$null;$failure=$null
    try{
        $task=$sessionMethod.Invoke($service,[object[]]@([Drawing.Rectangle]::new(0,0,160,240),$driver,$reportDelegate,$state.Cancellation.Token))
        Confirm-Session $task.IsCompleted ($Mode+' uses only synchronously completed virtual delays')
        try{$result=$task.GetAwaiter().GetResult()}catch{$failure=$_.Exception.GetBaseException()}
        return [pscustomobject]@{Mode=$Mode;State=$state;Result=$result;Failure=$failure}
    }catch{if($null -ne $result){$result.Dispose()};$state.Cancellation.Dispose();throw}
}
function Confirm-OwnedFramesDisposed($Case){
    $allDisposed=$true
    # PowerShell's property adapter can suppress a disposed Width getter.
    # A real GDI+ pixel read reliably throws after Bitmap.Dispose().
    foreach($frame in $Case.State.Frames){try{$null=$frame.GetPixel(0,0);$allDisposed=$false}catch{}}
    Confirm-Session $allDisposed ($Case.Mode+' disposes every frame returned by its read driver')
}
function Confirm-Prefix($Case,[int]$Height){
    $expected=$document.Clone([Drawing.Rectangle]::new(0,0,160,$Height),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{Confirm-Session ($Case.Result.Image.Size -eq $expected.Size -and (Pixel-Hash $Case.Result.Image) -ceq (Pixel-Hash $expected)) ($Case.Mode+' keeps the exact accepted document prefix with no duplicated or missing rows')}finally{$expected.Dispose()}
}
function Finish-Scenario($Case){
    $cases.Add([ordered]@{Mode=$Case.Mode;EndReason=$(if($null -ne $Case.Result){[string]$Case.Result.EndReason}else{'Canceled'});FrameCount=$(if($null -ne $Case.Result){$Case.Result.FrameCount}else{$null});VirtualMilliseconds=$Case.State.Clock;Deltas=$Case.State.Deltas.ToArray();FrameReads=$Case.State.ReadCount})
    if($null -ne $Case.Result){$Case.Result.Dispose()}
    Confirm-OwnedFramesDisposed $Case
    $Case.State.Cancellation.Dispose()
}
try{
    foreach($mode in @('delayed','badge-delayed')){
        $case=Run-SessionScenario $mode 2
        try{
            Confirm-Session ($null -eq $case.Failure -and $case.Result.FrameCount -eq 2) ($mode+' waits for the actual scroll response delayed by 750 ms')
            Confirm-Session ($case.State.Deltas.Count -eq 1 -and $case.State.Clock -ge 750) ($mode+' neither sends a second wheel request nor reverses before delayed movement')
            Confirm-Prefix $case 320
        }finally{Finish-Scenario $case}
    }

    $case=Run-SessionScenario 'recovery' 3
    try{
        Confirm-Session ($null -eq $case.Failure -and $case.Result.FrameCount -eq 3) 'A failed overlap recovers and continues through the shared production loop'
        $deltas=$case.State.Deltas.ToArray();$reverseIndex=-1
        for($index=0;$index -lt $deltas.Length;$index++){if($deltas[$index] -gt 0){$reverseIndex=$index;break}}
        Confirm-Session ($reverseIndex -gt 0 -and $reverseIndex+1 -lt $deltas.Length) 'Recovery reverses only after an unsuccessful forward step'
        Confirm-Session ($deltas[$reverseIndex] -eq -$deltas[$reverseIndex-1] -and $deltas[$reverseIndex+1] -lt 0 -and [Math]::Abs($deltas[$reverseIndex+1]) -lt [Math]::Abs($deltas[$reverseIndex-1])) 'A verified reverse is followed by a smaller forward wheel delta'
        Confirm-Prefix $case 320
    }finally{Finish-Scenario $case}

    $case=Run-SessionScenario 'localized-animation' 2
    try{
        Confirm-Session ($null -eq $case.Failure -and $case.Result.FrameCount -eq 2 -and $case.Result.Height -eq 320) 'A small continuously animated banner does not prevent a uniquely anchored scroll capture'
        Confirm-Session ($case.State.Clock -ge 900 -and $case.State.Deltas.Count -eq 1) 'Localized animation receives an extended settle interval rather than repeated scrolling'
        foreach($band in @(@(0,210),@(230,60),@(310,10))){
            $expected=$document.Clone([Drawing.Rectangle]::new(0,$band[0],160,$band[1]),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $actual=$case.Result.Image.Clone([Drawing.Rectangle]::new(0,$band[0],160,$band[1]),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try{Confirm-Session ((Pixel-Hash $actual) -ceq (Pixel-Hash $expected)) ('Animated capture preserves exact unchanged content at rows '+$band[0]+' through '+($band[0]+$band[1]-1))}finally{$actual.Dispose();$expected.Dispose()}
        }
    }finally{Finish-Scenario $case}

    foreach($mode in @('recovery-failed','target-lost','cancel-before-recovery')){
        $case=Run-SessionScenario $mode
        try{
            if($mode -eq 'cancel-before-recovery'){
                Confirm-Session ($null -eq $case.Result -and $case.Failure -is [OperationCanceledException]) 'Cancellation before recovery returns no partial success result'
                Confirm-Session (@($case.State.Deltas|Where-Object {$_ -gt 0}).Count -eq 0) 'Cancellation prevents a reverse scroll request'
            }else{
                $expectedReason=if($mode -eq 'target-lost'){'TargetChanged'}else{'NoReliableOverlap'}
                Confirm-Session ($null -eq $case.Failure -and [string]$case.Result.EndReason -eq $expectedReason -and $case.Result.FrameCount -eq 2) ($mode+' stops with the accepted views intact')
                if($mode -eq 'target-lost'){Confirm-Session (@($case.State.Deltas|Where-Object {$_ -gt 0}).Count -eq 0) 'Losing the pinned target prevents a reverse request'}
                else{Confirm-Session (@($case.State.Deltas|Where-Object {$_ -gt 0}).Count -le 2) 'An unverifiable recovery stays bounded'}
                Confirm-Prefix $case 300
            }
        }finally{Finish-Scenario $case}
    }

    foreach($mode in @('stalled','duration-limit','scroll-failed','bad-geometry','broad-animation')){
        $case=Run-SessionScenario $mode
        try{
            $expectedReason=switch($mode){'stalled' {'ScrollingStalled'};'duration-limit' {'LimitReached'};'broad-animation' {'ContentDidNotSettle'};default {'CaptureFailed'}}
            Confirm-Session ($null -eq $case.Failure -and [string]$case.Result.EndReason -eq $expectedReason -and $case.Result.FrameCount -eq 1) ($mode+' returns the honest stop reason without inventing new content')
            if($mode -eq 'stalled'){
                Confirm-Session ($case.Result.StoppedEarly -and $case.Result.Detail -match 'bottom is not confirmed') 'Repeated no movement is explicitly partial with the bottom unconfirmed'
                Confirm-Session ($case.State.Deltas.Count -ge 4 -and $case.State.Clock -ge 4800) 'A stalled view receives multiple delayed retries before stopping'
            }
            Confirm-Prefix $case 240
        }finally{Finish-Scenario $case}
    }
    $output=Join-Path $PSScriptRoot ($ReleaseFolder.Replace('-signed','-data'))
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Cases=$cases.ToArray();Scope='Shared production CaptureSessionAsync; memory-only owned frames and deterministic virtual time; no native target, window, capture or input.';Unverified='Native WM_MOUSEWHEEL delivery, actual application animation/reflow, capture compositor and live bottom-of-page behavior.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'scrolling-session.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed scrolling-session checks across '+$cases.Count+' virtual-driver scenarios. No windows, desktop capture or native input used.')
}finally{$document.Dispose();$unrelated.Dispose()}
