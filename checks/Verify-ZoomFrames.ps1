# Actual signed-canvas wheel animation using deterministic frame timestamps.
# No form/app, physical input, desktop capture, clipboard, OCR or settings I/O.
# WinForms Timer can own a hidden timer window; no message loop is pumped.
param(
    [string]$ReleaseFolder='release-0.5.5-signed',
    [string]$ExpectedVersion='0.5.5.0',
    [switch]$ExpectLegacyChurn
)
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
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$zoomBy=$canvasType.GetMethod('ZoomBy',$flags)
$zoomByAt=$canvasType.GetMethod('ZoomByAt',$flags)
$advance=$canvasType.GetMethod('AdvanceZoom',$flags)
$stop=$canvasType.GetMethod('StopZoomAnimation',$flags)
$originProperty=$canvasType.GetProperty('ImageOrigin',$flags)
$activeProperty=$canvasType.GetProperty('ZoomAnimationActive',$flags)
$timestampField=$canvasType.GetField('zoomFrameTimestamp',$flags)
$targetField=$canvasType.GetField('zoomTarget',$flags)
$nativeBaselineField=$canvasType.GetField('zoomNativeScrollPosition',$flags)
$onScroll=$canvasType.GetMethod('OnScroll',$flags)
$checks=New-Object 'System.Collections.Generic.List[string]'
$samples=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Zoom([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Origin($Canvas){return $originProperty.GetValue($Canvas,$null)}
function Active($Canvas){return [bool]$activeProperty.GetValue($Canvas,$null)}
function Native-State($Canvas){return $Canvas.AutoScrollMinSize.ToString()+'|'+$Canvas.AutoScrollPosition.ToString()+'|'+$Canvas.ClientSize.ToString()}
function Wheel($Canvas,[int]$Delta,[Drawing.Point]$Point){
    $factor=[single][SnipStudio.ViewMotion]::WheelZoomFactor($Delta)
    if($null -ne $zoomByAt){
        $timestamp=[long]$timestampField.GetValue($Canvas)
        $null=$zoomByAt.Invoke($Canvas,[object[]]@($factor,$Point,[Math]::Max([long]1,$timestamp)))
    }else{$null=$zoomBy.Invoke($Canvas,[object[]]@($factor,$Point))}
}
function Frame($Canvas,[double]$Milliseconds=16){
    $timestamp=[long]$timestampField.GetValue($Canvas)
    $ticks=[long][Math]::Round($Milliseconds*[Diagnostics.Stopwatch]::Frequency/1000.0)
    $null=$advance.Invoke($Canvas,[object[]]@([long]($timestamp+[Math]::Max([long]1,$ticks))))
}
function Assert-Anchor($Canvas,[Drawing.Point]$Viewport,[Drawing.PointF]$ImagePoint,[string]$Label){
    $origin=Origin $Canvas
    # Check the rendered location directly rather than calling the production
    # anchor-transform helper to calculate the expected answer.
    $x=$origin.X+$ImagePoint.X*$Canvas.Zoom
    $y=$origin.Y+$ImagePoint.Y*$Canvas.Zoom
    Confirm-Zoom ([Math]::Abs($x-$Viewport.X) -lt .01 -and [Math]::Abs($y-$Viewport.Y) -lt .01) $Label
}
function Image-At($Canvas,[Drawing.Point]$Point){$origin=Origin $Canvas;return [Drawing.PointF]::new(($Point.X-$origin.X)/$Canvas.Zoom,($Point.Y-$origin.Y)/$Canvas.Zoom)}
function Settle($Canvas,[Drawing.Point]$Anchor,[Drawing.PointF]$ImagePoint,[string]$Label,[int]$Direction){
    $native=Native-State $Canvas
    $last=[double]$Canvas.Zoom;$frames=0;$changes=0
    while((Active $Canvas) -and $frames -lt 100){
        Frame $Canvas
        $frames++
        $value=[double]$Canvas.Zoom
        Confirm-Zoom (($Direction -ge 0 -and $value -ge $last-.0000001) -or ($Direction -lt 0 -and $value -le $last+.0000001)) ($Label+' scale is monotonic at frame '+$frames)
        Assert-Anchor $Canvas $Anchor $ImagePoint ($Label+' pointer stays on the same image point at frame '+$frames)
        if(Active $Canvas){
            if((Native-State $Canvas) -cne $native){$changes++}
            if(-not $ExpectLegacyChurn){Confirm-Zoom ((Native-State $Canvas) -ceq $native) ($Label+' keeps native range, position and viewport unchanged at frame '+$frames)}
        }
        $last=$value
    }
    Confirm-Zoom ($frames -gt 2 -and -not (Active $Canvas)) ($Label+' reaches its exact target through intermediate frames')
    $target=[single]$targetField.GetValue($Canvas)
    Confirm-Zoom ([Math]::Abs($Canvas.Zoom-$target) -lt .0000001) ($Label+' settles at the requested exact scale')
    $samples.Add([ordered]@{Case=$Label;Frames=$frames;NativeChangesWhileAnimating=$changes;FinalZoom=$Canvas.Zoom;Origin=(Origin $Canvas).ToString();Native=(Native-State $Canvas)})
    return $changes
}
$image=[Drawing.Bitmap]::new(4096,2160)
$canvas=$null
try{
    if($ExpectLegacyChurn){
        $canvas=[Activator]::CreateInstance($canvasType)
        $canvas.Size=[Drawing.Size]::new(1000,700);$canvas.SetImage($image)
        $point=[Drawing.Point]::new(850,550);$imagePoint=Image-At $canvas $point
        Wheel $canvas 120 $point
        $changes=Settle $canvas $point $imagePoint 'Legacy fitted wheel zoom' 1
        Confirm-Zoom ($changes -gt 0) 'The previous signed build changes native scrollbar geometry during wheel frames'
        Write-Output ('PASS: reproduced legacy per-frame native scrollbar churn ('+$changes+' changing frames).')
        return
    }
    foreach($configuration in @(
        @{Label='Fit to large image';Size=[Drawing.Size]::new(1000,700);Zoom=[single]0;Delta=120;Anchor=[Drawing.Point]::new(850,550)},
        @{Label='Manual fractional zoom';Size=[Drawing.Size]::new(1300,900);Zoom=[single]1.375;Delta=30;Anchor=[Drawing.Point]::new(70,65)},
        @{Label='Large display and reverse wheel';Size=[Drawing.Size]::new(1920,1080);Zoom=[single]2.25;Delta=-120;Anchor=[Drawing.Point]::new(1480,820)})){
        $canvas=[Activator]::CreateInstance($canvasType)
        try{
            $canvas.Size=$configuration.Size;$canvas.SetImage($image)
            if($configuration.Zoom -gt 0){$canvas.ZoomAt($configuration.Zoom,$configuration.Anchor)}
            $revision=$canvas.DocumentRevision;$identity=$canvas.ImageIdentity
            $imagePoint=Image-At $canvas $configuration.Anchor
            Wheel $canvas $configuration.Delta $configuration.Anchor
            $null=Settle $canvas $configuration.Anchor $imagePoint $configuration.Label ([Math]::Sign($configuration.Delta))
            Assert-Anchor $canvas $configuration.Anchor $imagePoint ($configuration.Label+' final native reconciliation preserves the rendered origin')
            Confirm-Zoom ($canvas.DocumentRevision -eq $revision -and [Object]::ReferenceEquals($canvas.ImageIdentity,$identity)) ($configuration.Label+' does not alter the image or document history')
        }finally{$canvas.Dispose();$canvas=$null}
    }
    $canvas=[Activator]::CreateInstance($canvasType)
    $canvas.Size=[Drawing.Size]::new(1100,760);$canvas.SetImage($image)
    $first=[Drawing.Point]::new(450,330)
    Wheel $canvas 120 $first;Frame $canvas;Frame $canvas
    $newPoint=[Drawing.Point]::new(780,440)
    $before=Origin $canvas;$beforeZoom=$canvas.Zoom;$imagePoint=Image-At $canvas $newPoint;$native=Native-State $canvas
    Wheel $canvas 120 $newPoint
    $after=Origin $canvas
    Confirm-Zoom ([Math]::Abs($after.X-$before.X) -lt .01 -and [Math]::Abs($after.Y-$before.Y) -lt .01 -and [Math]::Abs($canvas.Zoom-$beforeZoom) -lt .000001) 'Moving the pointer while retargeting has no immediate jump'
    Confirm-Zoom ((Native-State $canvas) -ceq $native) 'Retargeting the wheel burst does not rebuild native scrollbar geometry'
    Frame $canvas
    Assert-Anchor $canvas $newPoint $imagePoint 'A deliberately moved pointer becomes the new zoom anchor'
    $before=Origin $canvas;$beforeZoom=$canvas.Zoom
    Wheel $canvas -120 $newPoint
    Frame $canvas
    Confirm-Zoom ($canvas.Zoom -lt $beforeZoom) 'Wheel reversal changes direction on the next frame without a queued forward step'
    Assert-Anchor $canvas $newPoint $imagePoint 'Wheel reversal retains the image point at the pointer'
    $current=Origin $canvas;$currentScale=$canvas.Zoom
    $null=$stop.Invoke($canvas,[object[]]@())
    $after=Origin $canvas
    Confirm-Zoom (-not (Active $canvas) -and $canvas.Zoom -eq $currentScale -and [Math]::Abs($after.X-$current.X) -lt .01 -and [Math]::Abs($after.Y-$current.Y) -lt .01) 'Interrupting a burst commits its current frame rather than snapping to its queued target'
    Wheel $canvas 60 $newPoint;Frame $canvas
    $imagePoint=Image-At $canvas $newPoint
    $canvas.ZoomAt([single]1.2,$newPoint)
    Assert-Anchor $canvas $newPoint $imagePoint 'Explicit ZoomAt safely replaces an active animated view at the actual pointer'
    Confirm-Zoom (-not (Active $canvas) -and [Math]::Abs($canvas.Zoom-1.2) -lt .000001) 'Explicit zoom ends the old timer and applies its requested scale'

    Wheel $canvas 120 $newPoint;Frame $canvas
    $imagePoint=Image-At $canvas $newPoint
    $late=[long]$timestampField.GetValue($canvas)+[long]([Diagnostics.Stopwatch]::Frequency/2)
    $null=$zoomByAt.Invoke($canvas,[object[]]@([single][SnipStudio.ViewMotion]::WheelZoomFactor(120),$newPoint,$late))
    Confirm-Zoom (Active $canvas) 'A late wheel event starts a fresh animation after finishing the preceding burst'
    $null=Settle $canvas $newPoint $imagePoint 'Late event starts a fresh burst' 1
    Assert-Anchor $canvas $newPoint $imagePoint 'Finishing an old burst inside a wheel event does not lose the new burst anchor'

    Wheel $canvas 120 $newPoint;Frame $canvas
    $before=Origin $canvas;$nativeCurrent=$canvas.AutoScrollPosition
    # Model a scrollbar's already-applied native movement without injecting
    # input or showing a real scrollbar. OnScroll must preserve that delta.
    $nativeBaselineField.SetValue($canvas,[Drawing.Point]::new($nativeCurrent.X+37,$nativeCurrent.Y+29))
    $event=[Windows.Forms.ScrollEventArgs]::new([Windows.Forms.ScrollEventType]::ThumbTrack,0,37,[Windows.Forms.ScrollOrientation]::HorizontalScroll)
    $null=$onScroll.Invoke($canvas,[object[]]@($event))
    $after=Origin $canvas
    Confirm-Zoom (-not (Active $canvas) -and [Math]::Abs($after.X-($before.X-37)) -lt .01 -and [Math]::Abs($after.Y-($before.Y-29)) -lt .01) 'Native scroll interruption retains the first thumb delta instead of replacing it with the old zoom origin'
    Confirm-Zoom ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'No editor or app window was opened'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Samples=$samples.ToArray();Scope='Actual signed canvas ZoomBy/AdvanceZoom at deterministic frame times; native scrollbar properties and rendered pointer anchors checked on unshown components.';Unverified='Physical wheel cadence, visible WinForms painting/scrollbars, monitor DPI and subjective smoothness.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'zoom-frames.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed wheel-frame checks. No application or physical input was used.')
}finally{if($null -ne $canvas){$canvas.Dispose()};$image.Dispose()}
