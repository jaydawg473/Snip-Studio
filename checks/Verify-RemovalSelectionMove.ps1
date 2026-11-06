# Signed-library checks for moving a pending source-removal selection.
# No helper compilation, application entrypoint, form, native input/capture,
# clipboard, OCR request, preference load/save or registry access.
param([string]$ReleaseFolder='release-0.5.4-signed',[string]$ExpectedVersion='0.5.4.0')
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
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$moveMethod=$canvasType.GetMethod('MoveSourceRemovalSelection',$flags)
$keyMethod=$canvasType.GetMethod('ProcessCmdKey',$flags)
$beginMethod=$canvasType.GetMethod('BeginSourceRemoval',$flags)
if($null -eq $moveMethod -or $null -eq $keyMethod -or $null -eq $beginMethod){throw 'The pending-removal movement and input hooks are required.'}
$removeTool=[Enum]::Parse($toolType,'RemoveArea')
$moveTool=[Enum]::Parse($toolType,'Select')
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-RemovalMove([bool]$Value,[string]$Label){if(-not $Value){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Bitmap-Hash([Drawing.Bitmap]$Image){
    $data=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($y=0;$y -lt $Image.Height;$y++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$y*$data.Stride),$bytes,$y*$Image.Width*4,$Image.Width*4)}
        $sha=[Security.Cryptography.SHA256]::Create()
        try{return [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-','')}finally{$sha.Dispose()}
    }finally{$Image.UnlockBits($data)}
}
function Canvas-Hash($Canvas){$image=$Canvas.RenderImage();try{return Bitmap-Hash $image}finally{$image.Dispose()}}
function Press-RemovalKey($Canvas,[Windows.Forms.Keys]$Key){
    $arguments=New-Object object[] 2
    $arguments[0]=[Windows.Forms.Message]::new();$arguments[1]=$Key
    return [bool]$keyMethod.Invoke($Canvas,$arguments)
}
function Move-Removal($Canvas,[Drawing.Point]$Delta){return [bool]$moveMethod.Invoke($Canvas,[object[]]@($Delta))}
function Compare-Outside([Drawing.Bitmap]$Before,[Drawing.Bitmap]$After,[Drawing.Rectangle]$Area){
    $regions=[Drawing.Rectangle[]]@(
        [Drawing.Rectangle]::new(0,0,$Before.Width,$Area.Top),
        [Drawing.Rectangle]::new(0,$Area.Bottom,$Before.Width,$Before.Height-$Area.Bottom),
        [Drawing.Rectangle]::new(0,$Area.Top,$Area.Left,$Area.Height),
        [Drawing.Rectangle]::new($Area.Right,$Area.Top,$Before.Width-$Area.Right,$Area.Height))
    foreach($region in $regions){
        if($region.Width -lt 1 -or $region.Height -lt 1){continue}
        $a=$Before.Clone($region,[Drawing.Imaging.PixelFormat]::Format32bppArgb);$b=$After.Clone($region,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try{if((Bitmap-Hash $a) -cne (Bitmap-Hash $b)){return $false}}finally{$a.Dispose();$b.Dispose()}
    }
    return $true
}
$fixture=[Drawing.Bitmap]::new(320,200,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g=[Drawing.Graphics]::FromImage($fixture)
try{$g.Clear([Drawing.Color]::White);$g.FillRectangle([Drawing.Brushes]::Blue,30,25,70,35);$g.FillRectangle([Drawing.Brushes]::Blue,180,40,90,110)}finally{$g.Dispose()}
$canvas=$null;$before=$null;$after=$null
try{
    $canvas=[Activator]::CreateInstance($canvasType)
    $canvas.SnapHighlightToText=$false
    $canvas.SetImage($fixture)
    $document=$canvasType.GetField('document',$flags).GetValue($canvas)
    $document.AddRectangle([Drawing.RectangleF]::new(10,160,30,20),[Drawing.Color]::Red,[single]2)
    $annotationBounds=$document.GetAnnotationBounds(0)
    $before=$canvas.RenderImage();$beforeHash=Bitmap-Hash $before
    $identity=$canvas.ImageIdentity;$revision=$canvas.DocumentRevision
    $script:removalMoveDocumentChanges=0;$script:removalMoveSelectionChanges=0
    $canvas.add_DocumentChanged([EventHandler]{$script:removalMoveDocumentChanges++})
    $canvas.add_SourceRemovalSelectionChanged([EventHandler]{$script:removalMoveSelectionChanges++})
    $selection=[Drawing.Rectangle]::new(28,23,74,39)
    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection)
    $fill=[Drawing.Color]::FromArgb(237,245,250)
    $canvasType.GetField('removalFill',$flags).SetValue($canvas,$fill)
    $selectionEventsBeforeMove=$script:removalMoveSelectionChanges
    $canvas.Tool=$moveTool
    Confirm-RemovalMove ($canvas.HasSelectedRemovalArea -and $canvas.SelectedRemovalArea -eq $selection) 'Switching to Move retains the pending deletion box'
    Confirm-RemovalMove ($canvas.SourceRemovalFillColor.ToArgb() -eq $fill.ToArgb()) 'Switching to Move retains the chosen fill'
    Confirm-RemovalMove ($script:removalMoveSelectionChanges -gt $selectionEventsBeforeMove) 'Tool transition refreshes pending-removal guidance'
    Confirm-RemovalMove ($canvas.DocumentRevision -eq $revision -and [Object]::ReferenceEquals($canvas.ImageIdentity,$identity)) 'Tool transition changes neither source identity nor document history'
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $beforeHash) 'Tool transition leaves exported pixels and annotations exact'

    Confirm-RemovalMove (Move-Removal $canvas ([Drawing.Point]::new(140,30))) 'Move translates the pending deletion box'
    Confirm-RemovalMove ($script:removalMoveSelectionChanges -gt $selectionEventsBeforeMove+1) 'Selection movement notifies its context controls'
    $moved=[Drawing.Rectangle]::new(168,53,74,39)
    Confirm-RemovalMove ($canvas.SelectedRemovalArea -eq $moved) 'Translated selection retains exact source-coordinate dimensions'
    Confirm-RemovalMove ($canvas.SourceRemovalFillColor.ToArgb() -eq $fill.ToArgb()) 'Translation does not resample or replace the chosen fill'
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $beforeHash -and $canvas.DocumentRevision -eq $revision) 'Moving the box changes no pixels or history before Delete'
    Confirm-RemovalMove ($document.AnnotationCount -eq 1 -and $document.GetAnnotationBounds(0) -eq $annotationBounds) 'Moving the removal box does not move an annotation'

    Confirm-RemovalMove (Move-Removal $canvas ([Drawing.Point]::new([int]::MaxValue,[int]::MaxValue))) 'Large positive deltas clamp without overflow'
    Confirm-RemovalMove ($canvas.SelectedRemovalArea -eq [Drawing.Rectangle]::new(246,161,74,39)) 'Right and bottom clamping retains selection width and height'
    Confirm-RemovalMove (-not (Move-Removal $canvas ([Drawing.Point]::new(50,50)))) 'A box already at the edge does not report a further move'
    Confirm-RemovalMove (Move-Removal $canvas ([Drawing.Point]::new([int]::MinValue,[int]::MinValue))) 'Large negative deltas clamp without overflow'
    Confirm-RemovalMove ($canvas.SelectedRemovalArea -eq [Drawing.Rectangle]::new(0,0,74,39)) 'Left and top clamping retains selection width and height'
    Confirm-RemovalMove (Press-RemovalKey $canvas ([Windows.Forms.Keys]::Right)) 'Move accepts one-pixel arrow nudging for a pending removal box'
    Confirm-RemovalMove ($canvas.SelectedRemovalArea.X -eq 1) 'Plain arrow moves the box one source pixel'
    Confirm-RemovalMove (Press-RemovalKey $canvas ([Windows.Forms.Keys]::Shift -bor [Windows.Forms.Keys]::Down)) 'Move accepts a Shift arrow nudge'
    Confirm-RemovalMove ($canvas.SelectedRemovalArea.Y -eq 10) 'Shift arrow moves the box ten source pixels'
    $canvas.Tool=$removeTool
    Confirm-RemovalMove ($canvas.HasSelectedRemovalArea -and $canvas.SelectedRemovalArea -eq [Drawing.Rectangle]::new(1,10,74,39)) 'Switching back to Remove area retains the repositioned box'
    $canvas.Tool=$moveTool
    $null=Move-Removal $canvas ([Drawing.Point]::new(167,43))
    Confirm-RemovalMove ($canvas.SelectedRemovalArea -eq $moved) 'A box can be returned to the desired deletion coordinates'
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $beforeHash -and $canvas.DocumentRevision -eq $revision) 'Clamping and nudging remain preview-only operations'
    Confirm-RemovalMove ($script:removalMoveDocumentChanges -eq 0) 'Preview movement sends no document-change notifications'

    Confirm-RemovalMove (Press-RemovalKey $canvas ([Windows.Forms.Keys]::Delete)) 'Delete is handled while Move has a pending removal box'
    Confirm-RemovalMove ($script:removalMoveDocumentChanges -eq 1) 'Delete sends exactly one document-change notification'
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea) 'Committed deletion clears the pending box'
    $after=$canvas.RenderImage();$afterHash=Bitmap-Hash $after
    Confirm-RemovalMove ($after.GetPixel(200,70).ToArgb() -eq $fill.ToArgb()) 'Delete fills the repositioned region with the retained color'
    Confirm-RemovalMove ($after.GetPixel(35,30).ToArgb() -eq $before.GetPixel(35,30).ToArgb()) 'Delete leaves the originally selected object untouched'
    Confirm-RemovalMove (Compare-Outside $before $after $moved) 'Every exported pixel outside the moved box remains exact'
    Confirm-RemovalMove ($document.AnnotationCount -eq 1 -and $document.GetAnnotationBounds(0) -eq $annotationBounds) 'Deletion preserves existing editable annotation geometry'
    Confirm-RemovalMove ([Object]::ReferenceEquals($canvas.ImageIdentity,$identity) -eq $false -and $canvas.DocumentRevision -gt $revision) 'Deletion creates a document edit rather than moving the background'
    $canvas.Undo()
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $beforeHash) 'One Undo restores exact pre-deletion pixels and annotations'
    $canvas.Redo()
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $afterHash) 'One Redo restores the exact moved-box deletion result'

    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection);$canvas.Tool=$moveTool
    $escapeRevision=$canvas.DocumentRevision;$escapeHash=Canvas-Hash $canvas
    Confirm-RemovalMove (Press-RemovalKey $canvas ([Windows.Forms.Keys]::Escape)) 'Escape is handled for a pending removal box in Move'
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea -and $canvas.DocumentRevision -eq $escapeRevision -and (Canvas-Hash $canvas) -ceq $escapeHash) 'Escape cancels only the preview without editing the snip'

    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection);$canvas.Tool=$moveTool
    $outside=[Windows.Forms.MouseEventArgs]::new([Windows.Forms.MouseButtons]::Left,1,0,0,0)
    Confirm-RemovalMove (-not [bool]$beginMethod.Invoke($canvas,[object[]]@($outside))) 'Clicking outside the box falls through to normal annotation selection'
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea) 'An outside Move click dismisses the pending removal box'
    Confirm-RemovalMove ((Canvas-Hash $canvas) -ceq $escapeHash) 'An outside Move click does not move or change the captured bitmap'

    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection);$canvas.Tool=[Enum]::Parse($toolType,'Pen')
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea) 'Choosing a different drawing tool cancels the pending removal box'
    Confirm-RemovalMove (-not (Move-Removal $canvas ([Drawing.Point]::new(5,5)))) 'A canceled box cannot be translated'
    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection);$canvas.Tool=$moveTool
    $document.AddRectangle([Drawing.RectangleF]::new(110,160,20,20),[Drawing.Color]::Black,[single]1)
    $staleHash=Canvas-Hash $canvas
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea -and -not (Move-Removal $canvas ([Drawing.Point]::new(5,5)))) 'A changed document revision invalidates pending movement'
    Confirm-RemovalMove (-not $canvas.RemoveSelectedSourceArea() -and (Canvas-Hash $canvas) -ceq $staleHash) 'A stale box cannot delete content'
    $canvas.Tool=$removeTool;$canvas.SelectSourceRemovalArea($selection);$canvas.Tool=$moveTool;$canvas.SetImage($fixture)
    Confirm-RemovalMove (-not $canvas.HasSelectedRemovalArea -and -not (Move-Removal $canvas ([Drawing.Point]::new(5,5)))) 'Replacing the source image clears pending movement'
    Confirm-RemovalMove (-not $canvas.IsHandleCreated -and [Windows.Forms.Application]::OpenForms.Count -eq 0) 'Checks leave the canvas without a native handle and open no application forms'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Scope='Signed canvas library; public selection movement and actual Delete/Escape/arrow command handling, in-memory pixels and document history. No app or native input.';Unverified='Physical pointer dragging, live context menus, displayed selection rendering and DPI interaction.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'removal-selection-move.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed removal-selection movement checks. No application or input was launched.')
}finally{if($null -ne $after){$after.Dispose()};if($null -ne $before){$before.Dispose()};if($null -ne $canvas){$canvas.Dispose()};$fixture.Dispose()}
