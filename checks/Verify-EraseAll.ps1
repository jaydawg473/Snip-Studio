# Signed-library checks only: no application entrypoint, displayed form, desktop
# capture, physical input, preferences, installation, or source compilation.
param([string]$ReleaseFolder='release-0.6.3-signed',[string]$ExpectedVersion='0.6.3.0',[string]$ExpectedSha256='')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
$actualSha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
if($ExpectedSha256 -and $actualSha256 -cne $ExpectedSha256){throw 'The signed app does not match the expected SHA256.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$documentType=$assembly.GetType('SnipStudio.EditorDocument',$true)
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Erase([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Field-Erase($Object,[string]$Name){return $Object.GetType().GetField($Name,$flags).GetValue($Object)}
function SetField-Erase($Object,[string]$Name,$Value){$Object.GetType().GetField($Name,$flags).SetValue($Object,$Value)}
function Call-Erase([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($i=0;$i -lt $Arguments.Length;$i++){if($null -ne $Arguments[$i]){$Arguments[$i]=$Arguments[$i].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject};return $Method.Invoke($Target,$Arguments)
}
function Hash-EraseBitmap([Drawing.Bitmap]$Image){
    $data=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($row=0;$row -lt $Image.Height;$row++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$row*$data.Stride),$bytes,$row*$Image.Width*4,$Image.Width*4)}
        $hash=[Security.Cryptography.SHA256]::Create()
        try{return [BitConverter]::ToString($hash.ComputeHash($bytes)).Replace('-','')}finally{$hash.Dispose()}
    }finally{$Image.UnlockBits($data)}
}
function Hash-EraseDocument($Document){$render=$Document.RenderImage();try{return Hash-EraseBitmap $render}finally{$render.Dispose()}}
function New-EraseDocument([Drawing.Bitmap]$Image){$result=[Activator]::CreateInstance($documentType);$result.SetImage($Image);$result.MarkSaved();return $result}
function New-EraseCanvas([Drawing.Bitmap]$Image){$result=[Activator]::CreateInstance($canvasType);$result.SnapHighlightToText=$false;$result.ClientSize=[Drawing.Size]::new(640,420);$result.SetImage($Image);$result.MarkSaved();return $result}
function Add-EraseAnnotations($Document){
    $points=[Drawing.PointF[]]@([Drawing.PointF]::new(8,8),[Drawing.PointF]::new(45,22))
    $Document.AddStroke($points,[Drawing.Color]::Red,[single]3,$false)
    $Document.AddStroke($points,[Drawing.Color]::Yellow,[single]12,$true)
    $Document.AddArrow([Drawing.PointF]::new(20,60),[Drawing.PointF]::new(70,80),[Drawing.Color]::Blue,[single]3)
    $Document.AddRectangle([Drawing.RectangleF]::new(90,20,40,30),[Drawing.Color]::Green,[single]2)
    $Document.AddText('Erase all fixture',[Drawing.PointF]::new(20,100),[Drawing.Color]::DarkRed,[single]16)
    $null=$Document.AddEmoji('happy',[Drawing.RectangleF]::new(160,30,40,40),[single]20)
}
function Begin-EraseDraft($Canvas,[string]$Text){
    $method=$canvasType.GetMethod('BeginInlineTextNote',$flags,$null,[type[]]@([Drawing.PointF]),$null)
    if(-not (Call-Erase $method $Canvas ([object[]]@([Drawing.PointF]::new(20,120))))){throw 'Could not create a synthetic inline draft.'}
    $editor=Field-Erase $Canvas 'inlineTextEditor';$editor.SelectAll();$editor.SelectedText=$Text
}

$image=[Drawing.Bitmap]::new(240,180,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($image);try{$graphics.Clear([Drawing.Color]::AliceBlue)}finally{$graphics.Dispose()}
$document=$null;$canvas=$null
try{
    $document=[Activator]::CreateInstance($documentType)
    Confirm-Erase (-not $document.EraseAllAnnotations() -and $document.Revision -eq 0 -and -not $document.CanUndo) 'Clearing a document with no image is a no-op'
    $document.SetImage($image);$document.MarkSaved();$revision=$document.Revision
    Confirm-Erase (-not $document.EraseAllAnnotations() -and $document.Revision -eq $revision -and -not $document.IsDirty -and -not $document.CanUndo) 'An empty saved image stays clean with no extra history'
    $baseHash=Hash-EraseDocument $document
    Add-EraseAnnotations $document
    Confirm-Erase ($document.AnnotationCount -eq 6) 'The fixture contains pen, highlighter, arrow, rectangle, text and emoji annotations'
    $document.MarkSaved();$annotatedHash=Hash-EraseDocument $document
    $revision=$document.Revision;$history=(Field-Erase $document 'states').Count;$identity=$documentType.GetProperty('BaseIdentity',$flags).GetValue($document,$null)
    Confirm-Erase ($document.EraseAllAnnotations() -and $document.AnnotationCount -eq 0) 'Erase all removes every annotation type together'
    Confirm-Erase ($document.Revision -eq $revision+1 -and (Field-Erase $document 'states').Count -eq $history+1 -and $document.IsDirty) 'Erase all creates exactly one dirty history state'
    Confirm-Erase ([Object]::ReferenceEquals($identity,$documentType.GetProperty('BaseIdentity',$flags).GetValue($document,$null)) -and (Hash-EraseDocument $document) -ceq $baseHash) 'Clearing keeps the same image frame and exact base pixels'
    $clearedRevision=$document.Revision;$clearedHistory=(Field-Erase $document 'states').Count
    Confirm-Erase (-not $document.EraseAllAnnotations() -and $document.Revision -eq $clearedRevision -and (Field-Erase $document 'states').Count -eq $clearedHistory) 'Clearing twice does not consume a second Undo state'
    $document.Undo()
    Confirm-Erase ($document.AnnotationCount -eq 6 -and $document.Revision -eq $revision -and -not $document.IsDirty -and (Hash-EraseDocument $document) -ceq $annotatedHash) 'One Undo restores every annotation, saved revision and exact rendered output'
    $document.Redo()
    Confirm-Erase ($document.AnnotationCount -eq 0 -and $document.Revision -eq $clearedRevision -and (Hash-EraseDocument $document) -ceq $baseHash) 'One Redo restores the clear operation exactly'
    $document.Dispose();$document=New-EraseDocument $image
    $document.AddRectangle([Drawing.RectangleF]::new(10,10,20,20),[Drawing.Color]::Red,[single]2);$document.Undo();$revision=$document.Revision
    Confirm-Erase (-not $document.EraseAllAnnotations() -and $document.CanRedo -and $document.Revision -eq $revision) 'An empty-image no-op preserves the existing Redo branch'
    $document.Redo();$null=$document.EraseImageRegions([Drawing.Rectangle[]]@([Drawing.Rectangle]::new(2,2,5,5)),[Drawing.Color]::Magenta)
    $document.Crop([Drawing.Rectangle]::new(1,1,220,160))
    $copy=Call-Erase ($documentType.GetMethod('CopyBaseImage',$flags)) $document ([object[]]@())
    try{$editedBaseHash=Hash-EraseBitmap $copy}finally{$copy.Dispose()}
    $editedIdentity=$documentType.GetProperty('BaseIdentity',$flags).GetValue($document,$null)
    Confirm-Erase ($document.EraseAllAnnotations()) 'Annotations can be cleared after a source-pixel edit and crop'
    Confirm-Erase ($document.ImageSize -eq [Drawing.Size]::new(220,160) -and [Object]::ReferenceEquals($editedIdentity,$documentType.GetProperty('BaseIdentity',$flags).GetValue($document,$null)) -and (Hash-EraseDocument $document) -ceq $editedBaseHash) 'Clear preserves the current crop and pixel-removal result rather than reverting image edits'
    $document.Dispose();$document=$null

    $canvas=New-EraseCanvas $image;$canvasDocument=Field-Erase $canvas 'document'
    Add-EraseAnnotations $canvasDocument
    $canvas.Tool=[Enum]::Parse($toolType,'RemoveArea');$canvas.SelectSourceRemovalArea([Drawing.Rectangle]::new(15,15,20,15))
    SetField-Erase $canvas 'selectedIndex' 2
    $revision=$canvas.DocumentRevision;$identity=$canvas.ImageIdentity
    Confirm-Erase ($canvas.EraseAllAnnotations() -and $canvas.AnnotationCount -eq 0) 'Canvas forwards clear to the document'
    Confirm-Erase ((Field-Erase $canvas 'selectedIndex') -eq -1 -and -not $canvas.HasSelectedRemovalArea -and -not $canvas.HasSelectedRecognizedText) 'Clear cancels annotation, source-removal and recognized-text selection'
    Confirm-Erase ([Object]::ReferenceEquals($identity,$canvas.ImageIdentity) -and $canvas.DocumentRevision -eq $revision+1) 'Canvas clearing preserves image identity and adds only one revision'
    $canvas.Undo();Confirm-Erase ($canvas.AnnotationCount -eq 6) 'Canvas Undo restores all annotations in one step'
    $canvas.Redo();Confirm-Erase ($canvas.AnnotationCount -eq 0 -and -not $canvas.EraseAllAnnotations()) 'Canvas Redo and empty no-op retain the cleared state'
    $canvas.Dispose();$canvas=New-EraseCanvas $image;$canvasDocument=Field-Erase $canvas 'document'
    $canvasDocument.AddRectangle([Drawing.RectangleF]::new(10,10,20,20),[Drawing.Color]::Red,[single]2)
    Begin-EraseDraft $canvas 'Draft included in clear'
    Confirm-Erase ($canvas.HasPendingInlineTextChanges -and $canvas.AnnotationCount -eq 1) 'A pending inline draft remains separate before clearing'
    Confirm-Erase ($canvas.EraseAllAnnotations() -and -not $canvas.InlineTextEditing -and $canvas.AnnotationCount -eq 0) 'Clear safely commits a valid inline draft before erasing annotations'
    $canvas.Undo()
    Confirm-Erase ($canvas.AnnotationCount -eq 2 -and $canvasDocument.GetTextAnnotation(1).Text.Contains('Draft included in clear')) 'One Undo of clear restores existing annotations and the just-committed inline text'
    $canvas.Dispose();$canvas=New-EraseCanvas $image;$canvasDocument=Field-Erase $canvas 'document'
    $canvasDocument.AddRectangle([Drawing.RectangleF]::new(10,10,20,20),[Drawing.Color]::Red,[single]2)
    Begin-EraseDraft $canvas 'Retain this stale draft'
    $canvasDocument.AddRectangle([Drawing.RectangleF]::new(60,20,20,20),[Drawing.Color]::Blue,[single]2)
    $revision=$canvas.DocumentRevision;$history=(Field-Erase $canvasDocument 'states').Count;$beforeHash=Hash-EraseDocument $canvasDocument
    Confirm-Erase (-not $canvas.EraseAllAnnotations() -and $canvas.AnnotationCount -eq 2 -and $canvas.InlineTextEditing -and $canvas.HasPendingInlineTextChanges) 'An invalid stale inline draft blocks erasing and remains recoverable'
    Confirm-Erase ($canvas.DocumentRevision -eq $revision -and (Field-Erase $canvasDocument 'states').Count -eq $history -and (Hash-EraseDocument $canvasDocument) -ceq $beforeHash) 'Blocked clear leaves committed annotations, pixels and history untouched'
    $canvas.CancelInlineText()
    Confirm-Erase ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'The library checks did not show or register a form'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
    [IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=$actualSha256;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;UserPreferencesReadOrWritten=$false;Scope='Actual signed-library annotation clearing, image-frame and crop/pixel preservation, one-step history, saved/dirty state, empty/no-image no-op, Redo retention, selection cleanup, valid and stale unshown inline RichEdit drafts.';Unverified='Displayed Eraser options card and clicks, physical keyboard Undo, mixed-monitor DPI and production installation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'erase-all.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed erase-all checks.')
}finally{
    if($null -ne $canvas){$canvas.Dispose()};if($null -ne $document){$document.Dispose()};$image.Dispose()
}
