# Signed-library behavior checks only. Never runs the application entrypoint,
# shows a form, sends physical input, captures the desktop, or loads preferences.
param([string]$ReleaseFolder='release-0.6.0-signed',[string]$ExpectedVersion='0.6.0.0')
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
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$documentType=$assembly.GetType('SnipStudio.EditorDocument',$true)
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$styleType=$assembly.GetType('SnipStudio.ShapeStyle',$true)
$styleCtor=$styleType.GetConstructor([type[]]@([Drawing.Color],[Drawing.Color],[single],[int],[int]))
if($null -eq $styleCtor){throw 'The shape style contract is missing.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Editor([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-Editor([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($i=0;$i -lt $Arguments.Length;$i++){if($null -ne $Arguments[$i]){$Arguments[$i]=$Arguments[$i].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function Field-Editor($Object,[string]$Name){return $Object.GetType().GetField($Name,$instance).GetValue($Object)}
function Tool-Editor([string]$Name){return [Enum]::Parse($toolType,$Name)}
function Style-Editor([Drawing.Color]$Fill,[Drawing.Color]$Outline,[single]$Width=4,[int]$FillOpacity=100,[int]$OutlineOpacity=100){
    [object[]]$arguments=@($Fill,$Outline,$Width,$FillOpacity,$OutlineOpacity)
    for($i=0;$i -lt $arguments.Length;$i++){$arguments[$i]=$arguments[$i].PSObject.BaseObject}
    return $styleCtor.Invoke($arguments)
}
function Hash-EditorBitmap([Drawing.Bitmap]$Image){
    $data=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($row=0;$row -lt $Image.Height;$row++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$row*$data.Stride),$bytes,$row*$Image.Width*4,$Image.Width*4)}
        $hash=[Security.Cryptography.SHA256]::Create()
        try{return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','')}finally{$hash.Dispose()}
    }finally{$Image.UnlockBits($data)}
}
function Hash-Editor($Object){$render=$Object.RenderImage();try{return Hash-EditorBitmap $render}finally{$render.Dispose()}}
function New-EditorCanvas([Drawing.Bitmap]$Image){
    $result=[Activator]::CreateInstance($canvasType)
    $result.SnapHighlightToText=$false;$result.ClientSize=[Drawing.Size]::new(640,420);$result.SetImage($Image);$result.MarkSaved()
    return $result
}
function New-EditorDocument([Drawing.Bitmap]$Image){$result=[Activator]::CreateInstance($documentType);$result.SetImage($Image);$result.MarkSaved();return $result}
function Add-EditorShape($Document,[string]$Kind,[Drawing.PointF]$Start,[Drawing.PointF]$End,$Style){return [bool](Invoke-Editor ($documentType.GetMethod('AddShape',$instance)) $Document ([object[]]@((Tool-Editor $Kind),$Start,$End,$Style)))}
function Get-EditorShapeStyle($Document,[int]$Index){return Invoke-Editor ($documentType.GetMethod('GetShapeAnnotationStyle',$instance)) $Document ([object[]]@($Index))}
function Update-EditorShapeStyle($Document,[int]$Index,$Style){return [bool](Invoke-Editor ($documentType.GetMethod('UpdateShapeAnnotationStyle',$instance)) $Document ([object[]]@($Index,$Style)))}
function Begin-EditorNote($Canvas,[Drawing.PointF]$Location){return [bool](Invoke-Editor ($canvasType.GetMethod('BeginInlineTextNote',$instance)) $Canvas ([object[]]@($Location)))}
function Begin-EditorTextEdit($Canvas,[int]$Index){return [bool](Invoke-Editor ($canvasType.GetMethod('BeginInlineTextEdit',$instance)) $Canvas ([object[]]@($Index)))}
function Set-EditorDraft($Canvas,[string]$Text){$editor=Field-Editor $Canvas 'inlineTextEditor';$editor.SelectAll();$editor.SelectedText=$Text}

$image=[Drawing.Bitmap]::new(240,180,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($image);try{$graphics.Clear([Drawing.Color]::White)}finally{$graphics.Dispose()}
$cursorCanvas=$null;$textCanvas=$null;$emojiCanvas=$null;$model=$null
try{
    $cursorCanvas=New-EditorCanvas $image
    foreach($name in [Enum]::GetNames($toolType)){
        $cursorCanvas.Tool=Tool-Editor $name
        Confirm-Editor ([Object]::ReferenceEquals($cursorCanvas.Cursor,[Windows.Forms.Cursors]::Default)) ($name+' selection retains the normal mouse pointer')
    }
    $cache=$canvasType.GetField('toolPointers',$instance)
    if($null -ne $cache){Confirm-Editor ($cache.GetValue($cursorCanvas).Count -eq 0) 'Tool selection does not allocate bitmap tool cursors'}
    Confirm-Editor (-not $cursorCanvas.IsHandleCreated) 'Idle pointer checks did not create a canvas HWND'
    $cursorCanvas.Tool=Tool-Editor 'Crop'
    $cropHandleType=$assembly.GetType('SnipStudio.CropFrameHandle',$true)
    $setCropPointer=$canvasType.GetMethod('SetCropPointer',$instance)
    foreach($case in @(@{Handle=1;Pointer=[Windows.Forms.Cursors]::SizeWE},@{Handle=2;Pointer=[Windows.Forms.Cursors]::SizeNS},@{Handle=3;Pointer=[Windows.Forms.Cursors]::SizeNWSE},@{Handle=6;Pointer=[Windows.Forms.Cursors]::SizeNESW})){
        $null=Invoke-Editor $setCropPointer $cursorCanvas ([object[]]@([Enum]::ToObject($cropHandleType,$case.Handle)))
        Confirm-Editor ([Object]::ReferenceEquals($cursorCanvas.Cursor,$case.Pointer)) ('Crop handle '+$case.Handle+' retains its useful resize pointer')
    }
    $null=Invoke-Editor $setCropPointer $cursorCanvas ([object[]]@([Enum]::ToObject($cropHandleType,0)))
    Confirm-Editor ([Object]::ReferenceEquals($cursorCanvas.Cursor,[Windows.Forms.Cursors]::Default)) 'Leaving a crop edge restores the normal pointer'
    Confirm-Editor ([int](Tool-Editor 'Ellipse') -eq 9 -and [int](Tool-Editor 'Line') -eq 10 -and [int](Tool-Editor 'RemoveArea') -eq 8) 'New shape choices preserve existing tool values'
    foreach($case in @(@{Width=[single]0;Fill=100;Outline=100},@{Width=[single]::NaN;Fill=100;Outline=100},@{Width=[single]::PositiveInfinity;Fill=100;Outline=100},@{Width=[single]1025;Fill=100;Outline=100},@{Width=[single]4;Fill=-1;Outline=100},@{Width=[single]4;Fill=101;Outline=100},@{Width=[single]4;Fill=100;Outline=-1},@{Width=[single]4;Fill=100;Outline=101})){
        $rejected=$false
        try{$null=Style-Editor ([Drawing.Color]::Blue) ([Drawing.Color]::Red) $case.Width $case.Fill $case.Outline}catch{$rejected=$_.Exception.GetBaseException() -is [ArgumentOutOfRangeException]}
        Confirm-Editor $rejected 'Invalid shape width or channel opacity is rejected before drawing'
    }

    $filled=Style-Editor ([Drawing.Color]::Blue) ([Drawing.Color]::Red)
    foreach($kind in @('Rectangle','Ellipse','Line','Arrow')){
        $model=New-EditorDocument $image
        try{
            Confirm-Editor (Add-EditorShape $model $kind ([Drawing.PointF]::new(30,40)) ([Drawing.PointF]::new(150,100)) $filled) ($kind+' creates one styled shape')
            Confirm-Editor ($model.AnnotationCount -eq 1) ($kind+' is one movable annotation')
            $render=$model.RenderImage()
            try{
                $center=$render.GetPixel(90,70)
                if($kind -in @('Rectangle','Ellipse')){Confirm-Editor ($center.ToArgb() -eq [Drawing.Color]::Blue.ToArgb()) ($kind+' exports its fill at source-image resolution')}
                else{Confirm-Editor ($center.R -gt 220 -and $center.B -lt 40) ($kind+' uses the outline color rather than the fill color')}
            }finally{$render.Dispose()}
            $style=Get-EditorShapeStyle $model 0
            Confirm-Editor ($style.OutlineWidth -eq 4 -and $style.OutlineColor.ToArgb() -eq [Drawing.Color]::Red.ToArgb()) ($kind+' retains its outline settings')
            $changed=Style-Editor ([Drawing.Color]::Green) ([Drawing.Color]::Black) 8 50 60
            $revision=$model.Revision
            Confirm-Editor (Update-EditorShapeStyle $model 0 $changed) ($kind+' permits editing the selected shape style')
            Confirm-Editor ($model.Revision -ne $revision -and (Get-EditorShapeStyle $model 0).FillOpacity -eq 50 -and (Get-EditorShapeStyle $model 0).OutlineOpacity -eq 60) ($kind+' stores independent fill and outline opacity')
            Confirm-Editor (-not (Update-EditorShapeStyle $model 0 $changed)) ($kind+' avoids a duplicate undo entry for an unchanged style')
            $model.Undo()
            Confirm-Editor ((Get-EditorShapeStyle $model 0).OutlineWidth -eq 4) ($kind+' style change is restored by one Undo')
            $oldBounds=$model.GetAnnotationBounds(0)
            Confirm-Editor ($model.MoveAnnotation(0,[Drawing.PointF]::new(20,10))) ($kind+' remains movable')
            $movedBounds=$model.GetAnnotationBounds(0)
            Confirm-Editor ([Math]::Abs($movedBounds.X-$oldBounds.X-20) -lt 0.01 -and [Math]::Abs($movedBounds.Y-$oldBounds.Y-10) -lt 0.01) ($kind+' movement preserves its geometry')
            $model.Crop([Drawing.Rectangle]::new(10,5,200,160))
            $cropBounds=$model.GetAnnotationBounds(0)
            Confirm-Editor ([Math]::Abs($cropBounds.X-$movedBounds.X+10) -lt 0.01 -and [Math]::Abs($cropBounds.Y-$movedBounds.Y+5) -lt 0.01 -and (Get-EditorShapeStyle $model 0).OutlineColor.ToArgb() -eq [Drawing.Color]::Red.ToArgb()) ($kind+' preserves style and translates correctly when cropped')
            $model.Undo()
            Confirm-Editor ($model.ImageSize -eq $image.Size -and $model.GetAnnotationBounds(0) -eq $movedBounds) ($kind+' crop Undo restores its original canvas and bounds')
        }finally{$model.Dispose();$model=$null}
    }
    $model=New-EditorDocument $image
    $outlineOnly=Style-Editor ([Drawing.Color]::Empty) ([Drawing.Color]::Red)
    Confirm-Editor (Add-EditorShape $model 'Rectangle' ([Drawing.PointF]::new(30,40)) ([Drawing.PointF]::new(150,100)) $outlineOnly) 'An outline-only rectangle is supported'
    $render=$model.RenderImage();try{Confirm-Editor ($render.GetPixel(90,70).ToArgb() -eq [Drawing.Color]::White.ToArgb()) 'No-fill leaves captured pixels visible inside a shape'}finally{$render.Dispose()}
    Confirm-Editor ($model.HitTestAnnotation([Drawing.PointF]::new(90,70),[single]0.1) -lt 0 -and $model.HitTestAnnotation([Drawing.PointF]::new(30,70),[single]0.1) -eq 0) 'An unfilled shape selects on its border rather than empty interior'
    $model.Dispose();$model=New-EditorDocument $image
    $fillOnly=Style-Editor ([Drawing.Color]::Blue) ([Drawing.Color]::Empty) 4 50 100
    Confirm-Editor (Add-EditorShape $model 'Ellipse' ([Drawing.PointF]::new(30,40)) ([Drawing.PointF]::new(150,100)) $fillOnly) 'A fill-only ellipse is supported'
    $render=$model.RenderImage();try{$pixel=$render.GetPixel(90,70);Confirm-Editor ($pixel.A -eq 255 -and $pixel.R -ge 126 -and $pixel.R -le 129 -and $pixel.G -ge 126 -and $pixel.G -le 129 -and $pixel.B -eq 255) 'Fill opacity composites over the screenshot without changing canvas opacity'}finally{$render.Dispose()}
    Confirm-Editor ($model.HitTestAnnotation([Drawing.PointF]::new(90,70),[single]0.1) -eq 0) 'A filled shape can be selected from its interior'
    $alphaStyle=Style-Editor ([Drawing.Color]::FromArgb(128,10,20,30)) ([Drawing.Color]::FromArgb(200,40,50,60)) 3 25 40
    Confirm-Editor ($alphaStyle.EffectiveFillColor.A -eq 32 -and $alphaStyle.EffectiveOutlineColor.A -eq 80) 'Style opacity preserves and multiplies legacy input alpha'
    $model.Dispose();$model=New-EditorDocument $image
    $invisible=Style-Editor ([Drawing.Color]::Empty) ([Drawing.Color]::Empty)
    Confirm-Editor (-not (Add-EditorShape $model 'Rectangle' ([Drawing.PointF]::new(30,40)) ([Drawing.PointF]::new(150,100)) $invisible) -and $model.AnnotationCount -eq 0 -and -not $model.IsDirty) 'A wholly invisible new shape does not create a hidden annotation or dirty the snip'
    Confirm-Editor (-not (Add-EditorShape $model 'Line' ([Drawing.PointF]::new(30,40)) ([Drawing.PointF]::new(150,100)) (Style-Editor ([Drawing.Color]::Blue) ([Drawing.Color]::Empty)))) 'Line creation requires a visible outline rather than silently using a fill'
    $model.Dispose();$model=$null

    $textCanvas=New-EditorCanvas $image
    $textCanvas.Tool=Tool-Editor 'Text';$textCanvas.InkColor=[Drawing.Color]::DarkRed;$textCanvas.TextFontSize=18
    $textDocument=Field-Editor $textCanvas 'document'
    $originalHash=Hash-Editor $textCanvas;$originalIdentity=$textCanvas.ImageIdentity;$revision=$textCanvas.DocumentRevision
    Confirm-Editor (Begin-EditorNote $textCanvas ([Drawing.PointF]::new(30,40))) 'Text begins directly at the clicked source-image point'
    Confirm-Editor ($textCanvas.InlineTextEditing -and $textDocument.AnnotationCount -eq 0 -and $textCanvas.DocumentRevision -eq $revision) 'Opening an inline text draft does not commit an empty annotation'
    Set-EditorDraft $textCanvas "Inline hello`r`nSecond line"
    Confirm-Editor ($textCanvas.HasPendingInlineTextChanges -and $textCanvas.DocumentRevision -eq $revision -and (Hash-Editor $textDocument) -ceq $originalHash) 'Typing stays in a draft until it is committed'
    Confirm-Editor ($textCanvas.CanShowAdvancedTextEditor -and $null -ne $canvasType.GetMethod('ShowAdvancedTextEditor',$instance)) 'The advanced text popup remains explicitly available without opening it'
    Confirm-Editor ($textCanvas.CommitInlineText()) 'Inline text commits without requiring a popup'
    $info=$textDocument.GetTextAnnotation(0)
    Confirm-Editor (-not $textCanvas.InlineTextEditing -and $textDocument.AnnotationCount -eq 1 -and $info.Text.Contains('Inline hello') -and $info.Text.Contains('Second line')) 'A multiline inline draft becomes one text annotation'
    Confirm-Editor ($info.Location -eq [Drawing.PointF]::new(30,40) -and [Math]::Abs($info.FontSize-18) -lt 0.01 -and [Object]::ReferenceEquals($textCanvas.ImageIdentity,$originalIdentity)) 'Inline text retains source coordinates, image-pixel font size and captured bitmap identity'
    $committedHash=Hash-Editor $textCanvas
    Confirm-Editor ($committedHash -cne $originalHash) 'Committed inline text appears in exported pixels'
    $textCanvas.Undo()
    Confirm-Editor ($textDocument.AnnotationCount -eq 0 -and (Hash-Editor $textCanvas) -ceq $originalHash) 'One Undo removes a complete inline text entry'
    $textCanvas.Redo()
    Confirm-Editor ($textDocument.AnnotationCount -eq 1 -and (Hash-Editor $textCanvas) -ceq $committedHash) 'Redo restores the same inline text output'
    $revision=$textCanvas.DocumentRevision
    Confirm-Editor (Begin-EditorTextEdit $textCanvas 0) 'Clicking an existing text annotation can reopen its inline editor'
    Set-EditorDraft $textCanvas 'Canceled changes'
    $textCanvas.CancelInlineText()
    Confirm-Editor (-not $textCanvas.InlineTextEditing -and $textCanvas.DocumentRevision -eq $revision -and (Hash-Editor $textCanvas) -ceq $committedHash) 'Canceling an inline edit preserves the previous text and pixels'
    $null=Begin-EditorTextEdit $textCanvas 0;Set-EditorDraft $textCanvas 'Updated inline note'
    Confirm-Editor ($textCanvas.CommitInlineText() -and $textDocument.AnnotationCount -eq 1 -and $textDocument.GetTextAnnotation(0).Text.Contains('Updated inline note')) 'Editing existing text replaces that annotation instead of adding a duplicate'
    $textCanvas.Undo()
    Confirm-Editor ((Hash-Editor $textCanvas) -ceq $committedHash) 'One Undo restores the previous text after an inline update'
    $revision=$textCanvas.DocumentRevision;$count=$textDocument.AnnotationCount
    $null=Begin-EditorNote $textCanvas ([Drawing.PointF]::new(30,110));Set-EditorDraft $textCanvas '   '
    $null=$textCanvas.CommitInlineText()
    Confirm-Editor (-not $textCanvas.InlineTextEditing -and $textCanvas.DocumentRevision -eq $revision -and $textDocument.AnnotationCount -eq $count) 'An empty new draft is discarded without a blank annotation or history entry'
    $null=Begin-EditorTextEdit $textCanvas 0;Set-EditorDraft $textCanvas 'Draft to retain'
    $textDocument.AddRectangle([Drawing.RectangleF]::new(180,130,20,20),[Drawing.Color]::Blue,[single]2)
    $changedRevision=$textCanvas.DocumentRevision
    Confirm-Editor (-not $textCanvas.CommitInlineText() -and $textCanvas.InlineTextEditing -and $textCanvas.HasPendingInlineTextChanges -and $textCanvas.DocumentRevision -eq $changedRevision) 'A stale inline draft cannot overwrite a newer document and stays available to recover'
    $textCanvas.CancelInlineText()
    $zoomHashes=New-Object 'System.Collections.Generic.List[string]'
    foreach($factor in @([single]1,[single]0.5)){
        $zoomCanvas=New-EditorCanvas $image
        try{
            $zoomCanvas.Tool=Tool-Editor 'Text';$zoomCanvas.InkColor=[Drawing.Color]::DarkRed;$zoomCanvas.TextFontSize=18
            $zoomCanvas.ZoomAt($factor,[Drawing.Point]::new(320,210))
            Confirm-Editor (Begin-EditorNote $zoomCanvas ([Drawing.PointF]::new(30,40))) ('Inline text opens at zoom '+$factor)
            Set-EditorDraft $zoomCanvas 'Zoom invariant'
            Confirm-Editor ($zoomCanvas.CommitInlineText()) ('Inline text commits at zoom '+$factor)
            $zoomDocument=Field-Editor $zoomCanvas 'document'
            Confirm-Editor ([Math]::Abs($zoomDocument.GetTextAnnotation(0).FontSize-18) -lt 0.01) ('Zoom '+$factor+' does not change the stored image-pixel font size')
            $zoomHashes.Add((Hash-Editor $zoomCanvas))
        }finally{$zoomCanvas.Dispose()}
    }
    Confirm-Editor ($zoomHashes[0] -ceq $zoomHashes[1]) 'The same inline note exports identical pixels at 100% and 50% viewer zoom'

    $emojiCanvas=New-EditorCanvas $image
    $emojiDocument=Field-Editor $emojiCanvas 'document'
    $emoji=[Char]::ConvertFromUtf32(0x1F600)
    $revision=$emojiCanvas.DocumentRevision;$emptyEmojiHash=Hash-Editor $emojiDocument
    $emojiCanvas.BeginEmojiPlacement($emoji)
    Confirm-Editor ($emojiCanvas.EmojiPlacementPending -and $emojiCanvas.PendingEmoji -ceq $emoji -and $emojiCanvas.Tool -eq (Tool-Editor 'Text')) 'Choosing an emoji enters placement mode without opening a popup'
    Confirm-Editor ($emojiDocument.AnnotationCount -eq 0 -and $emojiCanvas.DocumentRevision -eq $revision -and (Hash-Editor $emojiDocument) -ceq $emptyEmojiHash) 'An emoji is not added until its location is chosen'
    $escaped=Invoke-Editor ($canvasType.GetMethod('ProcessCmdKey',$instance)) $emojiCanvas ([object[]]@([Windows.Forms.Message]::new(),[Windows.Forms.Keys]::Escape))
    Confirm-Editor ($escaped -and -not $emojiCanvas.EmojiPlacementPending -and $emojiDocument.AnnotationCount -eq 0 -and $emojiCanvas.DocumentRevision -eq $revision) 'Escape cancels a pending emoji without adding an annotation or undo entry'
    $emojiCanvas.BeginEmojiPlacement($emoji);$emojiCanvas.Tool=Tool-Editor 'Pen'
    Confirm-Editor (-not $emojiCanvas.EmojiPlacementPending -and $emojiDocument.AnnotationCount -eq 0 -and $emojiCanvas.DocumentRevision -eq $revision) 'Changing tools cancels a pending emoji without editing the snip'
    $emojiCanvas.BeginEmojiPlacement($emoji)
    $placed=Invoke-Editor ($canvasType.GetMethod('TryPlacePendingEmoji',$instance)) $emojiCanvas ([object[]]@([Drawing.PointF]::new(500,500)))
    $emojiInfo=$emojiDocument.GetTextAnnotation(0)
    Confirm-Editor ($placed -and -not $emojiCanvas.EmojiPlacementPending -and $emojiDocument.AnnotationCount -eq 1 -and $null -ne $emojiInfo -and $emojiInfo.Text -ceq $emoji) 'Placing an emoji creates exactly one editable text annotation with the chosen glyph'
    Confirm-Editor ($emojiInfo.FontFamily -ceq 'Segoe UI Emoji' -and $emojiInfo.Rtf.Contains('Segoe UI Emoji')) 'The emoji retains its Segoe UI Emoji formatting in rich text'
    Confirm-Editor ($emojiInfo.Bounds.Left -ge 0 -and $emojiInfo.Bounds.Top -ge 0 -and $emojiInfo.Bounds.Right -le $image.Width -and $emojiInfo.Bounds.Bottom -le $image.Height) 'Emoji placement clamps its annotation bounds inside the source image'
    Confirm-Editor ($emojiCanvas.Tool -eq (Tool-Editor 'Select') -and $emojiCanvas.CanShowAdvancedTextEditor) 'A placed emoji is selected for normal move and advanced text actions'
    $emojiHash=Hash-Editor $emojiDocument
    Confirm-Editor ($emojiHash -cne $emptyEmojiHash) 'A placed emoji appears in exported pixels'
    $emojiCanvas.Undo()
    Confirm-Editor ($emojiDocument.AnnotationCount -eq 0 -and (Hash-Editor $emojiDocument) -ceq $emptyEmojiHash) 'One Undo removes a complete emoji placement'
    $emojiCanvas.Redo()
    Confirm-Editor ($emojiDocument.AnnotationCount -eq 1 -and $emojiDocument.GetTextAnnotation(0).Text -ceq $emoji -and (Hash-Editor $emojiDocument) -ceq $emojiHash) 'Redo restores the chosen emoji and identical exported pixels'
    $revision=$emojiCanvas.DocumentRevision
    Confirm-Editor (Begin-EditorTextEdit $emojiCanvas 0) 'An emoji remains editable through the inline text editor'
    $emojiCanvas.CancelInlineText()
    Confirm-Editor ($emojiCanvas.DocumentRevision -eq $revision -and $emojiDocument.AnnotationCount -eq 1 -and (Hash-Editor $emojiDocument) -ceq $emojiHash) 'Canceling an emoji text edit preserves its original glyph and formatting'
    Confirm-Editor ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'Editor checks did not show or register an application form'

    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;UserPreferencesReadOrWritten=$false;AdvancedPopupShown=$false;Scope='Actual signed-library idle cursor, contextual crop pointer, styled-shape rendering/hit/movement/crop/history, unshown inline RichEdit draft/commit/cancel/stale policies and direct emoji placement/cancellation/editability/history. Offscreen GDI, stock cursor resources and rich-edit HWNDs may be created.';Unverified='Physical typing, caret placement, click-outside/focus transitions, displayed shape or emoji palette, actual popup appearance, mixed-monitor DPI and production installation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'editor-tool-updates.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed editor pointer, shape, inline-text and emoji checks.')
}finally{
    if($null -ne $model){$model.Dispose()};if($null -ne $emojiCanvas){$emojiCanvas.Dispose()};if($null -ne $textCanvas){$textCanvas.Dispose()};if($null -ne $cursorCanvas){$cursorCanvas.Dispose()};$image.Dispose()
}
