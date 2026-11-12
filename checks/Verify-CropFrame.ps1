# Signed geometry and unshown in-memory canvas checks. No application
# entrypoint, screenshots, mouse capture, shown form, OCR or preferences.
param([string]$ReleaseFolder='release-0.5.9-signed',[string]$ExpectedVersion='0.5.9.0')
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
$geometry=$assembly.GetType('SnipStudio.CropFrame',$true)
$handleType=$assembly.GetType('SnipStudio.CropFrameHandle',$true)
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Crop([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-CropMethod([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($index=0;$index -lt $Arguments.Length;$index++){
        if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}
    }
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function Resize-Crop([Drawing.Rectangle]$Frame,[int]$Handle,[Drawing.PointF]$Delta,[Drawing.Size]$Image){
    return Invoke-CropMethod ($geometry.GetMethod('Resize',$static)) $null ([object[]]@($Frame,[Enum]::ToObject($handleType,$Handle),$Delta,$Image))
}
function Create-Crop([Drawing.PointF]$First,[Drawing.PointF]$Second,[Drawing.Size]$Image){
    return Invoke-CropMethod ($geometry.GetMethod('Create',$static)) $null ([object[]]@($First,$Second,$Image))
}
function Hit-Crop([Drawing.Rectangle]$Frame,[Drawing.PointF]$Point,[single]$Radius){
    return [int](Invoke-CropMethod ($geometry.GetMethod('HitTest',$static)) $null ([object[]]@($Frame,$Point,$Radius)))
}
function Crop-BitmapHash([Drawing.Bitmap]$Image){
    $data=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($row=0;$row -lt $Image.Height;$row++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$row*$data.Stride),$bytes,$row*$Image.Width*4,$Image.Width*4)}
        $hash=[Security.Cryptography.SHA256]::Create()
        try{return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','')}finally{$hash.Dispose()}
    }finally{$Image.UnlockBits($data)}
}
function Crop-CanvasHash($Canvas){$image=$Canvas.RenderImage();try{return Crop-BitmapHash $image}finally{$image.Dispose()}}
function Set-CanvasCrop($Canvas,[Drawing.Rectangle]$Frame){return [bool](Invoke-CropMethod ($canvasType.GetMethod('SetCropSelection',$instance)) $Canvas ([object[]]@($Frame)))}
function Crop-Key($Canvas,[Windows.Forms.Keys]$Key){
    [object[]]$arguments=@([Windows.Forms.Message]::new(),$Key)
    return [bool](Invoke-CropMethod ($canvasType.GetMethod('ProcessCmdKey',$instance)) $Canvas $arguments)
}
function Set-CropField($Canvas,[string]$Name,$Value){$canvasType.GetField($Name,$instance).SetValue($Canvas,$Value)}

$frame=[Drawing.Rectangle]::new(40,30,140,100)
$imageSize=[Drawing.Size]::new(320,200)
$resizeCases=@(
    @{Handle=3;Delta=[Drawing.PointF]::new(-10,-5);Expected=[Drawing.Rectangle]::new(30,25,150,105)},
    @{Handle=2;Delta=[Drawing.PointF]::new(90,-10);Expected=[Drawing.Rectangle]::new(40,20,140,110)},
    @{Handle=6;Delta=[Drawing.PointF]::new(20,-15);Expected=[Drawing.Rectangle]::new(40,15,160,115)},
    @{Handle=4;Delta=[Drawing.PointF]::new(25,90);Expected=[Drawing.Rectangle]::new(40,30,165,100)},
    @{Handle=12;Delta=[Drawing.PointF]::new(20,25);Expected=[Drawing.Rectangle]::new(40,30,160,125)},
    @{Handle=8;Delta=[Drawing.PointF]::new(90,30);Expected=[Drawing.Rectangle]::new(40,30,140,130)},
    @{Handle=9;Delta=[Drawing.PointF]::new(-15,20);Expected=[Drawing.Rectangle]::new(25,30,155,120)},
    @{Handle=1;Delta=[Drawing.PointF]::new(-25,90);Expected=[Drawing.Rectangle]::new(15,30,165,100)})
foreach($case in $resizeCases){Confirm-Crop ((Resize-Crop $frame $case.Handle $case.Delta $imageSize) -eq $case.Expected) ('Crop handle '+$case.Handle+' changes only its selected image edges')}
Confirm-Crop ((Resize-Crop $frame 12 ([Drawing.PointF]::new(10000,10000)) $imageSize) -eq [Drawing.Rectangle]::new(40,30,280,170)) 'Right and bottom edges can reach the exclusive image Width and Height'
Confirm-Crop ((Resize-Crop $frame 3 ([Drawing.PointF]::new(10000,10000)) $imageSize) -eq [Drawing.Rectangle]::new(178,128,2,2)) 'Crossing the opposite edges clamps to two pixels without inversion'
Confirm-Crop ((Resize-Crop $frame 12 ([Drawing.PointF]::new(-10000,-10000)) $imageSize) -eq [Drawing.Rectangle]::new(40,30,2,2)) 'Dragging right/bottom past the anchor preserves the minimum frame without swapping sides'
Confirm-Crop ((Resize-Crop $frame 3 ([Drawing.PointF]::new(-10000,-10000)) $imageSize) -eq [Drawing.Rectangle]::new(0,0,180,130)) 'Top and left expansion clamp to the image origin'
Confirm-Crop ((Resize-Crop $frame 16 ([Drawing.PointF]::new(10000,10000)) $imageSize) -eq [Drawing.Rectangle]::new(180,100,140,100)) 'Moving to the lower-right keeps the crop dimensions and clamps the whole frame'
Confirm-Crop ((Resize-Crop $frame 16 ([Drawing.PointF]::new(-10000,-10000)) $imageSize) -eq [Drawing.Rectangle]::new(0,0,140,100)) 'Moving to the upper-left keeps the crop dimensions and clamps the whole frame'
Confirm-Crop ((Resize-Crop $frame 1 ([Drawing.PointF]::new(-0.5,0)) $imageSize) -eq [Drawing.Rectangle]::new(39,30,141,100)) 'Half-pixel drag deltas round consistently away from zero'
Confirm-Crop ((Resize-Crop $frame 4 ([Drawing.PointF]::new(0.49,0)) $imageSize) -eq $frame) 'Sub-half-pixel drag noise does not move an image edge'
foreach($delta in @([Drawing.PointF]::new([single]::NaN,0),[Drawing.PointF]::new(0,[single]::PositiveInfinity))){Confirm-Crop ((Resize-Crop $frame 12 $delta $imageSize) -eq $frame) 'Non-finite pointer deltas leave the existing crop unchanged'}
Confirm-Crop ((Resize-Crop $frame 4 ([Drawing.PointF]::new(1,1)) ([Drawing.Size]::Empty)) -eq $frame) 'An unavailable image does not alter the existing frame'
$createCases=@(
    @{First=[Drawing.PointF]::new(80,60);Second=[Drawing.PointF]::new(120,100);Expected=[Drawing.Rectangle]::new(80,60,40,40)},
    @{First=[Drawing.PointF]::new(80,60);Second=[Drawing.PointF]::new(40,100);Expected=[Drawing.Rectangle]::new(40,60,40,40)},
    @{First=[Drawing.PointF]::new(80,60);Second=[Drawing.PointF]::new(40,20);Expected=[Drawing.Rectangle]::new(40,20,40,40)},
    @{First=[Drawing.PointF]::new(80,60);Second=[Drawing.PointF]::new(120,20);Expected=[Drawing.Rectangle]::new(80,20,40,40)},
    @{First=[Drawing.PointF]::new(-5,-9);Second=[Drawing.PointF]::new(999,999);Expected=[Drawing.Rectangle]::new(0,0,320,200)},
    @{First=[Drawing.PointF]::new(10.5,20.5);Second=[Drawing.PointF]::new(30.5,40.5);Expected=[Drawing.Rectangle]::new(11,21,20,20)})
foreach($case in $createCases){Confirm-Crop ((Create-Crop $case.First $case.Second $imageSize) -eq $case.Expected) 'A newly drawn crop is ordered, rounded and clamped in source-pixel coordinates'}
Confirm-Crop ((Create-Crop ([Drawing.PointF]::new(10,10)) ([Drawing.PointF]::new(11,30)) $imageSize).IsEmpty) 'A one-pixel-wide creation is rejected'
Confirm-Crop ((Create-Crop ([Drawing.PointF]::new([single]::NaN,0)) ([Drawing.PointF]::new(20,30)) $imageSize).IsEmpty) 'A non-finite creation point is rejected'
$hitCases=@(
    @{Point=[Drawing.PointF]::new(40,30);Handle=3},@{Point=[Drawing.PointF]::new(110,30);Handle=2},
    @{Point=[Drawing.PointF]::new(180,30);Handle=6},@{Point=[Drawing.PointF]::new(180,80);Handle=4},
    @{Point=[Drawing.PointF]::new(180,130);Handle=12},@{Point=[Drawing.PointF]::new(110,130);Handle=8},
    @{Point=[Drawing.PointF]::new(40,130);Handle=9},@{Point=[Drawing.PointF]::new(40,80);Handle=1},
    @{Point=[Drawing.PointF]::new(40,42);Handle=1},@{Point=[Drawing.PointF]::new(36,80);Handle=1},
    @{Point=[Drawing.PointF]::new(110,80);Handle=16},@{Point=[Drawing.PointF]::new(0,0);Handle=0})
foreach($case in $hitCases){Confirm-Crop ((Hit-Crop $frame $case.Point 5) -eq $case.Handle) 'Corners and the full edge length expose the correct crop handle, including the grab tolerance'}
$fullFrame=[Drawing.Rectangle]::new(0,0,320,200)
Confirm-Crop ((Hit-Crop $fullFrame ([Drawing.PointF]::new(-4,0)) 5) -eq 3 -and (Hit-Crop $fullFrame ([Drawing.PointF]::new(324,100)) 5) -eq 4 -and
    (Hit-Crop $fullFrame ([Drawing.PointF]::new(160,204)) 5) -eq 8) 'Full-image handles remain grabbable just outside the image bounds'
foreach($radius in @([single]0,[single]-1,[single]::NaN,[single]::PositiveInfinity)){Confirm-Crop ((Hit-Crop $frame ([Drawing.PointF]::new(40,30)) $radius) -eq 0) 'Invalid handle radii cannot create a hit'}
Confirm-Crop ((Hit-Crop $frame ([Drawing.PointF]::new([single]::NaN,30)) 5) -eq 0) 'A non-finite pointer coordinate cannot hit a crop handle'

$fixture=[Drawing.Bitmap]::new(320,200,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($fixture)
try{
    for($row=0;$row -lt 10;$row++){
        for($column=0;$column -lt 16;$column++){
            $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,($column*13+$row*7)%256,($row*23+$column*3)%256,($column*11+$row*17)%256))
            try{$graphics.FillRectangle($brush,$column*20,$row*20,20,20)}finally{$brush.Dispose()}
        }
    }
}finally{$graphics.Dispose()}
$canvas=$null;$before=$null;$after=$null;$expected=$null;$tiny=$null
$cropTool=[Enum]::Parse($toolType,'Crop');$selectTool=[Enum]::Parse($toolType,'Select')
try{
    $canvas=[Activator]::CreateInstance($canvasType)
    $canvas.Size=[Drawing.Size]::new(700,500)
    $canvas.SnapHighlightToText=$false
    $canvas.SetImage($fixture);$canvas.MarkSaved();$canvas.ActualSize()
    $document=$canvasType.GetField('document',$instance).GetValue($canvas)
    $cleanRevision=$canvas.DocumentRevision;$cleanHash=Crop-CanvasHash $canvas;$cleanIdentity=$canvas.ImageIdentity;$initialZoom=$canvas.Zoom
    $script:cropDocumentEvents=0;$script:cropSelectionEvents=0
    $canvas.add_DocumentChanged([EventHandler]{$script:cropDocumentEvents++})
    $canvas.add_CropSelectionChanged([EventHandler]{$script:cropSelectionEvents++})
    $canvas.Tool=$cropTool
    Confirm-Crop ($canvas.CropModeActive -and $canvas.CropSelection -eq $fullFrame -and -not $canvas.CanApplyCrop) 'Entering Crop starts with the full source image and disables a no-op Apply'
    Confirm-Crop ($canvas.Zoom -eq $initialZoom -and $canvas.DocumentRevision -eq $cleanRevision -and [Object]::ReferenceEquals($canvas.ImageIdentity,$cleanIdentity) -and
        -not $canvas.IsDirty -and -not $canvas.CanUndo -and (Crop-CanvasHash $canvas) -ceq $cleanHash) 'Selecting the Crop tool does not shrink, dirty, resample or edit the snip'
    Confirm-Crop (-not $canvas.ApplyCrop() -and -not $canvas.CropModeActive -and -not $canvas.IsDirty -and -not $canvas.CanUndo -and $canvas.DocumentRevision -eq $cleanRevision) 'Applying an unchanged full-image frame exits without adding history'
    $canvas.Tool=$cropTool
    $selection=[Drawing.Rectangle]::new(40,30,200,140)
    $eventsBefore=$script:cropSelectionEvents
    Confirm-Crop (Set-CanvasCrop $canvas $selection) 'The same selection-update path used by dragging accepts a valid crop preview'
    Confirm-Crop ($canvas.CropSelection -eq $selection -and $canvas.CanApplyCrop -and $script:cropSelectionEvents -eq $eventsBefore+1) 'Preview changes notify contextual controls once and enable Apply'
    $eventsBefore=$script:cropSelectionEvents
    Confirm-Crop (-not (Set-CanvasCrop $canvas $selection) -and -not (Set-CanvasCrop $canvas ([Drawing.Rectangle]::new(-1,0,20,20))) -and
        -not (Set-CanvasCrop $canvas ([Drawing.Rectangle]::new(0,0,1,20))) -and $script:cropSelectionEvents -eq $eventsBefore) 'Duplicate, out-of-image and undersized frame updates are rejected without a preview event'
    Confirm-Crop ((Crop-CanvasHash $canvas) -ceq $cleanHash -and $canvas.DocumentRevision -eq $cleanRevision -and -not $canvas.IsDirty -and
        $script:cropDocumentEvents -eq 0) 'A resized preview changes no exported pixels, document identity, dirty state or Undo history'
    $canvas.CropCommandsEnabled=$false
    Confirm-Crop (-not $canvas.CanApplyCrop -and -not $canvas.ApplyCrop() -and (Crop-Key $canvas ([Windows.Forms.Keys]::Enter)) -and
        (Crop-Key $canvas ([Windows.Forms.Keys]::Escape)) -and $canvas.CropModeActive -and $canvas.CropSelection -eq $selection) 'Host busy guards refuse Apply and cancellation keys while retaining the crop draft'
    $canvas.CancelCrop()
    Confirm-Crop ($canvas.CropModeActive -and $canvas.CropSelection -eq $selection -and (Crop-CanvasHash $canvas) -ceq $cleanHash) 'The public Cancel command also honors the host busy guard'
    $canvas.CropCommandsEnabled=$true
    $null=Invoke-CropMethod ($canvasType.GetMethod('SetZoom',$instance)) $canvas ([object[]]@([single]1.25))
    $null=Invoke-CropMethod ($canvasType.GetMethod('PanView',$instance)) $canvas ([object[]]@([single]-15,[single]8))
    Confirm-Crop ($canvas.CropModeActive -and $canvas.CropSelection -eq $selection -and $canvas.DocumentRevision -eq $cleanRevision -and
        (Crop-CanvasHash $canvas) -ceq $cleanHash) 'Immediate zoom and the real pan-origin path retain the preview without editing source pixels'
    $prior=$canvas.CropSelection
    Set-CropField $canvas 'cropDragStart' $prior
    Set-CropField $canvas 'cropHandle' ([Enum]::ToObject($handleType,4))
    Set-CropField $canvas 'cropGestureMoved' $true
    $null=Set-CanvasCrop $canvas (Resize-Crop $prior 4 ([Drawing.PointF]::new(15,0)) $canvas.ImageSize)
    Confirm-Crop (-not $canvas.CanApplyCrop -and -not $canvas.ApplyCrop()) 'An in-progress frame drag cannot commit a partial crop'
    $null=Invoke-CropMethod ($canvasType.GetMethod('SetZoom',$instance)) $canvas ([object[]]@([single]1))
    Confirm-Crop ($canvas.CropSelection -eq $prior -and [int]$canvasType.GetField('cropHandle',$instance).GetValue($canvas) -eq 0 -and
        $canvas.DocumentRevision -eq $cleanRevision) 'A view change cancels an active drag back to its prior frame without acquiring mouse capture'
    Confirm-Crop (Crop-Key $canvas ([Windows.Forms.Keys]::Escape)) 'Escape handles an idle crop preview'
    Confirm-Crop (-not $canvas.CropModeActive -and $canvas.CropSelection.IsEmpty -and (Crop-CanvasHash $canvas) -ceq $cleanHash -and
        $canvas.DocumentRevision -eq $cleanRevision -and -not $canvas.CanUndo) 'Escape discards only the preview and leaves the complete original intact'

    $canvas.SetImage($fixture)
    $document.AddRectangle([Drawing.RectangleF]::new(60,50,45,30),[Drawing.Color]::Red,[single]3)
    $document.AddArrow([Drawing.PointF]::new(120,80),[Drawing.PointF]::new(210,140),[Drawing.Color]::Blue,[single]4)
    $document.AddRectangle([Drawing.RectangleF]::new(270,180,20,10),[Drawing.Color]::Green,[single]2)
    $canvas.MarkSaved()
    $before=$canvas.RenderImage();$beforeHash=Crop-BitmapHash $before;$originalRevision=$canvas.DocumentRevision;$originalIdentity=$canvas.ImageIdentity
    $originalBounds=@($document.GetAnnotationBounds(0),$document.GetAnnotationBounds(1),$document.GetAnnotationBounds(2))
    $canvas.Tool=$cropTool;$null=Set-CanvasCrop $canvas $selection
    Confirm-Crop ((Crop-CanvasHash $canvas) -ceq $beforeHash -and $document.AnnotationCount -eq 3) 'Previewing over annotated content preserves every existing annotation and pixel'
    $expected=$before.Clone($selection,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    Confirm-Crop ($canvas.ApplyCrop()) 'Apply commits a changed valid frame'
    $after=$canvas.RenderImage();$afterHash=Crop-BitmapHash $after
    Confirm-Crop ($canvas.ImageSize -eq $selection.Size -and $afterHash -ceq (Crop-BitmapHash $expected)) 'Applied output is pixel-identical to the selected region of the prior annotated image'
    Confirm-Crop ($canvas.IsDirty -and $canvas.DocumentRevision -gt $originalRevision -and -not [Object]::ReferenceEquals($canvas.ImageIdentity,$originalIdentity) -and
        -not $canvas.CropModeActive -and $canvas.CropSelection.IsEmpty) 'Apply creates one document edit and exits the preview mode'
    for($annotationIndex=0;$annotationIndex -lt $originalBounds.Count;$annotationIndex++){
        $bounds=$originalBounds[$annotationIndex]
        $translated=[Drawing.RectangleF]::new($bounds.X-$selection.X,$bounds.Y-$selection.Y,$bounds.Width,$bounds.Height)
        Confirm-Crop ($document.GetAnnotationBounds($annotationIndex) -eq $translated) 'Apply translates editable annotation coordinates by the crop origin without flattening or dropping them'
    }
    Confirm-Crop ($document.AnnotationCount -eq 3) 'Even annotations outside the new image remain in editable Undo history rather than being flattened'
    $canvas.Undo()
    Confirm-Crop ($canvas.ImageSize -eq $fixture.Size -and (Crop-CanvasHash $canvas) -ceq $beforeHash -and
        $canvas.DocumentRevision -eq $originalRevision -and [Object]::ReferenceEquals($canvas.ImageIdentity,$originalIdentity) -and -not $canvas.IsDirty) 'One Undo restores the exact complete image, annotation state and saved revision'
    $canvas.Redo()
    Confirm-Crop ((Crop-CanvasHash $canvas) -ceq $afterHash -and $canvas.ImageSize -eq $selection.Size) 'One Redo restores the exact applied crop'
    $canvas.SetImage($fixture);$canvas.MarkSaved();$canvas.Tool=$cropTool
    $edgeSelection=[Drawing.Rectangle]::new(250,160,70,40)
    Confirm-Crop (Set-CanvasCrop $canvas $edgeSelection) 'A preview may end at both exclusive right/bottom image edges'
    $edgeExpected=$fixture.Clone($edgeSelection,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $null=$canvas.ApplyCrop();$edgeActual=$canvas.RenderImage()
        try{Confirm-Crop ((Crop-BitmapHash $edgeActual) -ceq (Crop-BitmapHash $edgeExpected) -and
            $edgeActual.GetPixel(69,39).ToArgb() -eq $fixture.GetPixel(319,199).ToArgb()) 'A boundary crop retains the final source row and column without resampling'}finally{$edgeActual.Dispose()}
    }finally{$edgeExpected.Dispose()}
    $canvas.SetImage($fixture);$canvas.MarkSaved();$canvas.Tool=$cropTool;$null=Set-CanvasCrop $canvas $selection
    $canvas.Tool=$selectTool;$canvas.Tool=$cropTool
    Confirm-Crop ($canvas.CropSelection -eq $fullFrame -and -not $canvas.CanApplyCrop) 'Switching away and returning starts a fresh full-frame preview'
    $null=Set-CanvasCrop $canvas $selection
    $document.AddRectangle([Drawing.RectangleF]::new(10,10,10,10),[Drawing.Color]::Black,[single]1)
    $staleRevision=$canvas.DocumentRevision;$staleHash=Crop-CanvasHash $canvas
    Confirm-Crop ($canvas.CropSelection -eq $fullFrame -and -not $canvas.CanApplyCrop -and -not $canvas.ApplyCrop() -and
        $canvas.DocumentRevision -eq $staleRevision -and (Crop-CanvasHash $canvas) -ceq $staleHash) 'A changed document revision invalidates the old preview before it can crop newer content'
    $canvas.Tool=$cropTool;$null=Set-CanvasCrop $canvas $selection;$canvas.Undo()
    Confirm-Crop ($canvas.CropSelection -eq $fullFrame -and -not $canvas.CanApplyCrop) 'Undo resets a pending preview against its new document revision'
    $null=Set-CanvasCrop $canvas $selection;$canvas.SetImage($fixture)
    Confirm-Crop ($canvas.CropSelection -eq $fullFrame -and -not $canvas.CanApplyCrop) 'Replacing the source image discards its previous crop frame'
    $canvas.CancelCrop()
    Confirm-Crop (-not (Set-CanvasCrop $canvas $selection) -and -not $canvas.ApplyCrop()) 'Selection updates and Apply do nothing outside Crop mode'
    $tiny=[Drawing.Bitmap]::new(1,1,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $canvas.SetImage($tiny);$canvas.MarkSaved();$canvas.Tool=$cropTool
    Confirm-Crop (-not $canvas.CanApplyCrop -and -not $canvas.ApplyCrop() -and $canvas.ImageSize -eq [Drawing.Size]::new(1,1) -and -not $canvas.IsDirty) 'An image smaller than the crop minimum stays intact without an invalid commit'
    Confirm-Crop (-not $canvas.IsHandleCreated -and [Windows.Forms.Application]::OpenForms.Count -eq 0) 'The offscreen canvas checks created no canvas HWND or application form'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;CanvasShown=$false;CanvasHandleCreated=$canvas.IsHandleCreated;NativeScreenCapture=$false;NativeMouseCaptureRequested=$false;UserPreferencesOrRegistryAccessed=$false;Scope='Pure crop edge/corner/create/move geometry and real unshown canvas preview, busy guard, view/pan cancellation, exported pixels, annotations and Undo/Redo APIs. A drag already in progress is represented only by managed preview fields; no mouse event or Capture setter is called by the fixture. Offscreen GDI and ordinary canvas cursor resources may be allocated.';Unverified='Physical pointer dragging, high-DPI handle appearance, actual mouse capture/cancellation, displayed crop context toolbar and monitor layout.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'crop-frame.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed crop geometry and offscreen canvas checks.')
}finally{
    if($null -ne $tiny){$tiny.Dispose()};if($null -ne $expected){$expected.Dispose()};if($null -ne $after){$after.Dispose()};if($null -ne $before){$before.Dispose()};if($null -ne $canvas){$canvas.Dispose()};$fixture.Dispose()
}
