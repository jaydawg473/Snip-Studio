# Signed-library object/component checks. Gesture entry points that capture the
# mouse are deliberately not invoked. No app entrypoint, shown forms or user data.
param([string]$ReleaseFolder='release-0.6.2-signed',[string]$ExpectedVersion='0.6.2.0')
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
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$documentType=$assembly.GetType('SnipStudio.EditorDocument',$true)
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$styleType=$assembly.GetType('SnipStudio.ShapeStyle',$true)
$artType=$assembly.GetType('SnipStudio.EmojiArtwork',$true)
$geometryType=$assembly.GetType('SnipStudio.EmojiStickerGeometry',$true)
$cornerType=$assembly.GetType('SnipStudio.EmojiResizeCorner',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Object([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-Object([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($i=0;$i -lt $Arguments.Length;$i++){if($null -ne $Arguments[$i]){$Arguments[$i]=$Arguments[$i].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject};return $Method.Invoke($Target,$Arguments)
}
function Field-Object($Object,[string]$Name){return $Object.GetType().GetField($Name,$flags).GetValue($Object)}
function Tool-Object([string]$Name){return [Enum]::Parse($toolType,$Name)}
function New-ObjectDocument([Drawing.Bitmap]$Image){$result=[Activator]::CreateInstance($documentType);$result.SetImage($Image);$result.MarkSaved();return $result}
function Near-Object([double]$First,[double]$Second){return [Math]::Abs($First-$Second) -lt .01}
function Same-ObjectPoint([Drawing.PointF]$First,[Drawing.PointF]$Second){return (Near-Object $First.X $Second.X) -and (Near-Object $First.Y $Second.Y)}
function Read-ObjectPixels([Drawing.Bitmap]$Bitmap){
    $data=$Bitmap.LockBits([Drawing.Rectangle]::new(0,0,$Bitmap.Width,$Bitmap.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{$bytes=New-Object byte[] ($Bitmap.Width*$Bitmap.Height*4);for($row=0;$row -lt $Bitmap.Height;$row++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$row*$data.Stride),$bytes,$row*$Bitmap.Width*4,$Bitmap.Width*4)};return ,$bytes}finally{$Bitmap.UnlockBits($data)}
}
function Hash-ObjectBitmap([Drawing.Bitmap]$Bitmap){$hash=[Security.Cryptography.SHA256]::Create();try{return [BitConverter]::ToString($hash.ComputeHash((Read-ObjectPixels $Bitmap))).Replace('-','')}finally{$hash.Dispose()}}
function Hash-ObjectDocument($Document){$bitmap=$Document.RenderImage();try{return Hash-ObjectBitmap $bitmap}finally{$bitmap.Dispose()}}
function Call-ObjectGeometry([string]$Method,[object[]]$Arguments){return Invoke-Object ($geometryType.GetMethod($Method,$flags)) $null $Arguments}

$manifest=Get-Content -LiteralPath (Join-Path $project 'assets\emoji\manifest.json') -Raw|ConvertFrom-Json
$catalog=@($artType.GetProperty('Catalog',$flags).GetValue($null,$null))
$resources=@($assembly.GetManifestResourceNames()|Where-Object {$_ -like 'SnipStudio.Emoji.*.png'})
Confirm-Object ($catalog.Count -eq 24 -and @($catalog.Key|Sort-Object -Unique).Count -eq 24 -and $manifest.assets.Count -eq 24 -and $resources.Count -eq 24) 'The 24 emoji catalog entries have a complete unique embedded PNG resource set'
foreach($definition in $catalog){
    $entry=@($manifest.assets|Where-Object {$_.key -ceq $definition.Key})
    Confirm-Object ($entry.Count -eq 1 -and $resources -ccontains $entry[0].resourceName) ($definition.Key+' has its manifest-declared embedded resource')
    $stream=$assembly.GetManifestResourceStream($entry[0].resourceName);$hasher=[Security.Cryptography.SHA256]::Create()
    try{$digest=[BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','').ToLowerInvariant()}finally{$hasher.Dispose();$stream.Dispose()}
    Confirm-Object ($digest -ceq $entry[0].sha256) ($definition.Key+' embeds the exact pinned PNG artwork bytes')
    $key=Invoke-Object ($artType.GetMethod('ResolveKey',$flags)) $null ([object[]]@($definition.Glyph))
    $aspect=[single](Invoke-Object ($artType.GetMethod('GetAspectRatio',$flags)) $null ([object[]]@($definition.Key)))
    Confirm-Object ($key -ceq $definition.Key -and -not [single]::IsNaN($aspect) -and -not [single]::IsInfinity($aspect) -and $aspect -gt 0) ($definition.Key+' resolves its glyph and has a valid source aspect ratio')
    $thumbnail=Invoke-Object ($artType.GetMethod('CreateThumbnail',$flags)) $null ([object[]]@($definition.Key,64))
    try{
        $pixels=Read-ObjectPixels $thumbnail;$visible=0;$colorful=0;$transparent=0
        for($offset=0;$offset -lt $pixels.Length;$offset+=4){if($pixels[$offset+3] -gt 0){$visible++;if($pixels[$offset+3] -ge 128 -and ([Math]::Max($pixels[$offset],[Math]::Max($pixels[$offset+1],$pixels[$offset+2]))-[Math]::Min($pixels[$offset],[Math]::Min($pixels[$offset+1],$pixels[$offset+2]))) -ge 25){$colorful++}}else{$transparent++}}
        Confirm-Object ($thumbnail.Width -ge 1 -and $thumbnail.Width -le 64 -and $thumbnail.Height -ge 1 -and $thumbnail.Height -le 64 -and $visible -gt 0 -and $transparent -gt 0) ($definition.Key+' decodes into a visible transparent thumbnail within its requested size')
        if($definition.Key -in @('happy','thumbs-up','check-mark','cross','heart')){Confirm-Object ($colorful -gt 0) ($definition.Key+' uses colored graphic pixels rather than monochrome font substitution')}
    }finally{$thumbnail.Dispose()}
}
$thumbnail=Invoke-Object ($artType.GetMethod('CreateThumbnail',$flags)) $null ([object[]]@('happy',32))
try{Confirm-Object ($thumbnail.Width -gt 0 -and $thumbnail.Height -gt 0) 'Disposing caller thumbnails leaves the shared embedded artwork usable'}finally{$thumbnail.Dispose()}

$frame=[Drawing.RectangleF]::new(50,40,80,40)
$corners=@(Call-ObjectGeometry 'Corners' ([object[]]@($frame,[single]30)))
foreach($corner in 0..3){
    $anchor=$corners[($corner+2)%4];$drag=$corners[$corner]
    $target=[Drawing.PointF]::new($anchor.X+1.5*($drag.X-$anchor.X),$anchor.Y+1.5*($drag.Y-$anchor.Y))
    $resized=Call-ObjectGeometry 'ResizeFromCorner' ([object[]]@($frame,[single]30,[Enum]::ToObject($cornerType,$corner),$target,[single]12))
    $nextCorners=@(Call-ObjectGeometry 'Corners' ([object[]]@($resized,[single]30)))
    Confirm-Object ((Near-Object $resized.Width 120) -and (Near-Object $resized.Height 60) -and (Same-ObjectPoint $anchor $nextCorners[($corner+2)%4])) ('Emoji corner '+$corner+' resizes proportionally while fixing the opposite rotated corner')
    Confirm-Object ((Call-ObjectGeometry 'ResizeFromCorner' ([object[]]@($frame,[single]30,[Enum]::ToObject($cornerType,$corner),$drag,[single]12))) -eq $frame) ('Emoji corner '+$corner+' does not jump on a stationary grab')
}
$minimum=Call-ObjectGeometry 'ResizeFromCorner' ([object[]]@($frame,[single]0,[Enum]::ToObject($cornerType,2),[Drawing.PointF]::new(50,40),[single]12))
Confirm-Object ((Near-Object $minimum.Width 24) -and (Near-Object $minimum.Height 12)) 'Emoji resizing enforces the minimum without stretching the aspect ratio'
$rotation=Call-ObjectGeometry 'RotateFromPointer' ([object[]]@($frame,[single]170,[Drawing.PointF]::new(90,20),[Drawing.PointF]::new(130,60)))
Confirm-Object (Near-Object $rotation -100) 'Emoji rotation follows the pointer angle and normalizes across 180 degrees'
$grip=Call-ObjectGeometry 'RotationGrip' ([object[]]@($frame,[single]0,[single]12))
Confirm-Object (Same-ObjectPoint $grip ([Drawing.PointF]::new(90,28))) 'The emoji rotation grip sits above its frame in source coordinates'

$image=[Drawing.Bitmap]::new(320,240,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($image);try{$graphics.Clear([Drawing.Color]::White)}finally{$graphics.Dispose()}
$document=$null;$canvas=$null
try{
    $document=New-ObjectDocument $image;$emptyHash=Hash-ObjectDocument $document
    Confirm-Object ($document.AddEmoji('happy',[Drawing.RectangleF]::new(60,50,80,60),[single]370)) 'A graphic emoji creates one independent document object'
    $info=$document.GetEmojiAnnotation(0)
    Confirm-Object ($document.AnnotationCount -eq 1 -and $info.Key -ceq 'happy' -and (Near-Object $info.RotationDegrees 10) -and $null -eq $document.GetTextAnnotation(0)) 'Emoji identity and normalized rotation remain separate from text'
    $originalHash=Hash-ObjectDocument $document;$revision=$document.Revision
    Confirm-Object ($originalHash -cne $emptyHash -and $document.HitTestAnnotation([Drawing.PointF]::new(100,80),[single]0.1) -eq 0) 'Graphic emoji renders and can be selected from its rotated frame'
    Confirm-Object (-not $document.UpdateEmojiAnnotationBounds(0,$info.Frame,$info.RotationDegrees) -and $document.Revision -eq $revision) 'An unchanged emoji frame avoids a duplicate history entry'
    $preview=Invoke-Object ($documentType.GetMethod('CreateEmojiAnnotationPreview',$flags)) $document ([object[]]@(0,[Drawing.RectangleF]::new(40,35,120,90),[single]30))
    Confirm-Object ($null -ne $preview -and $document.Revision -eq $revision -and (Hash-ObjectDocument $document) -ceq $originalHash) 'Emoji resize and rotation previews leave the committed document untouched'
    Confirm-Object ($document.UpdateEmojiAnnotationBounds(0,[Drawing.RectangleF]::new(40,35,120,90),[single]30)) 'An emoji frame can resize and rotate in one transaction'
    $editedHash=Hash-ObjectDocument $document
    $document.Undo();Confirm-Object ((Hash-ObjectDocument $document) -ceq $originalHash) 'One Undo restores an emoji frame change'
    $document.Redo();Confirm-Object ((Hash-ObjectDocument $document) -ceq $editedHash) 'Redo restores the exact resized and rotated emoji output'
    $before=$document.GetEmojiAnnotation(0);$null=$document.MoveAnnotation(0,[Drawing.PointF]::new(10,5));$moved=$document.GetEmojiAnnotation(0)
    Confirm-Object ((Near-Object ($moved.Frame.X-$before.Frame.X) 10) -and (Near-Object ($moved.Frame.Y-$before.Frame.Y) 5) -and $moved.Key -ceq $before.Key -and $moved.RotationDegrees -eq $before.RotationDegrees) 'Moving an emoji preserves its artwork and rotation'
    $document.Crop([Drawing.Rectangle]::new(5,7,300,220));$cropped=$document.GetEmojiAnnotation(0)
    Confirm-Object ((Near-Object ($cropped.Frame.X-$moved.Frame.X) -5) -and (Near-Object ($cropped.Frame.Y-$moved.Frame.Y) -7) -and $cropped.RotationDegrees -eq 30) 'Cropping translates the emoji frame without changing its angle'
    $beforeErase=Hash-ObjectDocument $document
    Confirm-Object ($document.Erase([Drawing.PointF]::new($cropped.Frame.X+$cropped.Frame.Width/2,$cropped.Frame.Y+$cropped.Frame.Height/2),[single]0.1) -and $document.AnnotationCount -eq 0) 'The eraser removes a selected graphic emoji as one object'
    $document.Undo();Confirm-Object ((Hash-ObjectDocument $document) -ceq $beforeErase) 'Undo restores erased emoji artwork and geometry'
    $document.Dispose();$document=$null

    $styleCtor=$styleType.GetConstructor([type[]]@([Drawing.Color],[Drawing.Color],[single],[int],[int]))
    [object[]]$styleArguments=@([Drawing.Color]::Blue,[Drawing.Color]::Red,[single]6,40,80)
    for($i=0;$i -lt $styleArguments.Length;$i++){$styleArguments[$i]=$styleArguments[$i].PSObject.BaseObject}
    $style=$styleCtor.Invoke($styleArguments)
    foreach($kind in @('Rectangle','Ellipse')){
        $document=New-ObjectDocument $image
        try{
            $null=Invoke-Object ($documentType.GetMethod('AddShape',$flags)) $document ([object[]]@((Tool-Object $kind),[Drawing.PointF]::new(30,40),[Drawing.PointF]::new(150,100),$style))
            $bounds=$document.GetClosedShapeBounds(0);$revision=$document.Revision;$hash=Hash-ObjectDocument $document
            Confirm-Object ($bounds -eq [Drawing.RectangleF]::new(30,40,120,60) -and $document.GetAnnotationBounds(0).Width -gt $bounds.Width) ($kind+' exposes its geometry frame separately from outline inflation')
            Confirm-Object (-not $document.UpdateClosedShapeBounds(0,$bounds) -and $document.Revision -eq $revision) ($kind+' does not add history for an unchanged resize frame')
            $preview=Invoke-Object ($documentType.GetMethod('CreateClosedShapeResizePreview',$flags)) $document ([object[]]@(0,[Drawing.RectangleF]::new(20,25,180,100)))
            Confirm-Object ($null -ne $preview -and $document.Revision -eq $revision -and (Hash-ObjectDocument $document) -ceq $hash) ($kind+' resize preview leaves committed pixels and history intact')
            Confirm-Object ($document.UpdateClosedShapeBounds(0,[Drawing.RectangleF]::new(20,25,180,100))) ($kind+' accepts a new frame as one edit')
            $retained=$document.GetShapeAnnotationStyle(0)
            Confirm-Object ($document.GetClosedShapeBounds(0) -eq [Drawing.RectangleF]::new(20,25,180,100) -and $retained.FillColor.ToArgb() -eq $style.FillColor.ToArgb() -and $retained.OutlineWidth -eq 6 -and $retained.FillOpacity -eq 40 -and $retained.OutlineOpacity -eq 80) ($kind+' keeps fill, outline width and both opacity settings while resizing')
            $resizedHash=Hash-ObjectDocument $document;$document.Undo();Confirm-Object ((Hash-ObjectDocument $document) -ceq $hash) ($kind+' resize is reversed by one Undo')
            $document.Redo();Confirm-Object ((Hash-ObjectDocument $document) -ceq $resizedHash) ($kind+' resize Redo restores identical pixels')
            $null=$document.UpdateClosedShapeBounds(0,[Drawing.RectangleF]::new(-20,-30,400,300));Confirm-Object ($document.GetClosedShapeBounds(0) -eq [Drawing.RectangleF]::new(0,0,320,240)) ($kind+' resize clamps geometry to the source image')
            $null=$document.UpdateClosedShapeBounds(0,[Drawing.RectangleF]::new(500,500,0,0));Confirm-Object ($document.GetClosedShapeBounds(0) -eq [Drawing.RectangleF]::new(318,238,2,2)) ($kind+' remains valid when a resize crosses an edge or collapses')
        }finally{$document.Dispose();$document=$null}
    }
    Confirm-Object ($canvasType.GetField('closedShapeHandles',$flags).GetValue($null).Length -eq 8) 'Closed shapes expose all eight edge and corner resize handles'

    $canvas=[Activator]::CreateInstance($canvasType);$canvas.SnapHighlightToText=$false;$canvas.ClientSize=[Drawing.Size]::new(640,420);$canvas.SetImage($image);$canvas.MarkSaved();$canvas.Tool=Tool-Object 'Text';$canvas.TextFontSize=16
    $document=Field-Object $canvas 'document';$revision=$document.Revision
    $beginFrame=$canvasType.GetMethod('BeginInlineTextNote',$flags,$null,[type[]]@([Drawing.RectangleF]),$null)
    $beganFrame=Invoke-Object $beginFrame $canvas ([object[]]@([Drawing.RectangleF]::new(20,30,140,80)))
    Confirm-Object ([bool]$beganFrame) 'A chosen text rectangle starts an inline draft at its own location and dimensions'
    $draft=Field-Object $canvas 'inlineTextEditor';$draft.SelectAll();$draft.SelectedText='Bold note';$draft.Select(0,4)
    $font=[Drawing.Font]::new('Segoe UI',16,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel);try{$draft.SelectionFont=$font}finally{$font.Dispose()}
    Confirm-Object ($document.AnnotationCount -eq 0 -and $document.Revision -eq $revision -and -not $canvas.Capture) 'A framed text draft remains uncommitted and does not capture desktop input'
    Confirm-Object ($canvas.CommitInlineText()) 'Framed inline text commits without an advanced popup'
    $info=$document.GetTextAnnotation(0);$rtf=$info.Rtf;$textHash=Hash-ObjectDocument $document
    Confirm-Object ($info.Location -eq [Drawing.PointF]::new(20,30) -and $info.LayoutWidth -eq 140 -and $info.MinimumHeight -eq 80 -and $info.Bounds.Height -ge 80 -and (Near-Object $info.FontSize 16)) 'Committed text retains the chosen frame and pixel font size'
    $revision=$document.Revision
    Confirm-Object ($document.UpdateTextAnnotationBounds(0,$info.Location,[single]16,[single]140,[single]120)) 'Changing text frame height is a document edit'
    $taller=$document.GetTextAnnotation(0)
    Confirm-Object ($taller.MinimumHeight -eq 120 -and $taller.Bounds.Height -ge 120 -and $taller.Rtf -ceq $rtf -and (Hash-ObjectDocument $document) -ceq $textHash) 'Height-only text resizing changes the frame while preserving formatted text and exported pixels'
    $document.Undo();Confirm-Object ($document.GetTextAnnotation(0).MinimumHeight -eq 80) 'Text frame height Undo restores the drawn rectangle'
    $document.Redo();Confirm-Object ($document.GetTextAnnotation(0).MinimumHeight -eq 120) 'Text frame height Redo restores its resized height'
    $null=$document.UpdateTextAnnotationBounds(0,$info.Location,[single]16,[single]40)
    $wrapped=$document.GetTextAnnotation(0)
    Confirm-Object ($wrapped.LayoutWidth -eq 40 -and $wrapped.MinimumHeight -eq 120 -and (Near-Object $wrapped.FontSize 16) -and $wrapped.Rtf -ceq $rtf -and (Hash-ObjectDocument $document) -cne $textHash) 'Narrowing a text frame wraps lines while retaining formatting, font size and remembered height'
    $revision=$document.Revision
    Confirm-Object (-not $document.UpdateTextAnnotationBounds(0,$wrapped.Location,$wrapped.FontSize,$wrapped.LayoutWidth,$wrapped.MinimumHeight) -and $document.Revision -eq $revision) 'An unchanged text frame creates no duplicate history entry'
    $null=$document.MoveAnnotation(0,[Drawing.PointF]::new(10,5));$document.Crop([Drawing.Rectangle]::new(5,7,300,220));$moved=$document.GetTextAnnotation(0)
    Confirm-Object ($moved.Location -eq [Drawing.PointF]::new(25,28) -and $moved.MinimumHeight -eq 120 -and $moved.Rtf -ceq $rtf) 'Moving and cropping preserve text frame height and rich formatting'
    foreach($height in @([single]-1,[single]::NaN,[single]32769)){
        $revision=$document.Revision;$rejected=$false
        try{$null=$document.UpdateTextAnnotationBounds(0,$moved.Location,$moved.FontSize,$moved.LayoutWidth,$height)}catch{$rejected=$_.Exception.GetBaseException() -is [ArgumentOutOfRangeException]}
        Confirm-Object ($rejected -and $document.Revision -eq $revision) 'Invalid text frame heights are rejected without changing the document'
    }
    $document=$null # The canvas owns its document.
    Confirm-Object (-not $canvas.Capture -and [Windows.Forms.Application]::OpenForms.Count -eq 0) 'Object component checks leave no mouse capture or shown application forms'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data');[IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;MouseCaptureRequested=$false;UserPreferencesReadOrWritten=$false;Scope='Signed component/model checks: pinned embedded emoji bytes and decoded color thumbnails; pure emoji corner/rotation geometry; graphic-object rendering, previews, move/crop/erase/history; closed-shape resize/style/clamp/history; framed inline RichEdit text, formatting, wrapping and MinimumHeight. In-memory GDI and unshown RichEdit handles may be created.';Unverified='Live mouse-drawn text frames, drag/capture/focus transitions, displayed eight-handle interaction, live rotation gestures, tool submenus, color palettes, mixed-monitor DPI and installation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'object-editing.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed object-editing component checks.')
}finally{if($null -ne $canvas){$canvas.Dispose()};if($null -ne $document){$document.Dispose()};$image.Dispose()}
