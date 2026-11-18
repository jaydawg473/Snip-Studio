# Signed-release, in-memory checks. Never starts the application entry point,
# shows an editor, captures a screen, uses the clipboard, or touches user settings.
param([string]$ReleaseFolder = 'release-0.4.2-signed')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Windows PowerShell 5 for the .NET Framework application.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run powershell.exe -STA -NoProfile -File checks\Verify-TranslationReplacement.ps1.' }
$project = Split-Path $PSScriptRoot -Parent
$app = Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
if (-not (Test-Path -LiteralPath $app -PathType Leaf)) { throw 'The signed release application is missing.' }
$signature = Get-AuthenticodeSignature -LiteralPath $app
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') { throw 'A valid Automation Forge LLC application signature is required.' }
if ($ReleaseFolder -notmatch '\Arelease-(\d+\.\d+\.\d+)-signed\z') { throw 'Choose a versioned signed release folder.' }
$expectedVersion = $Matches[1] + '.0'
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion
if ($version -ne $expectedVersion) { throw "Expected file version $expectedVersion; found $version." }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom($app)
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$canvasType = $assembly.GetType('SnipStudio.EditorCanvas', $true)
$documentType = $assembly.GetType('SnipStudio.EditorDocument', $true)
$replacementType = $assembly.GetType('SnipStudio.RecognizedTextReplacement', $true)
$layoutType = $assembly.GetType('SnipStudio.RichTextLayout', $true)
$requestType = $assembly.GetType('SnipStudio.ShortcutStartupRequest', $true)
$hostType = $assembly.GetType('SnipStudio.ShortcutHost', $true)
$lifetimeType = $assembly.GetType('SnipStudio.StudioApplicationContext', $true)
$checks = New-Object 'System.Collections.Generic.List[string]'
$owned = New-Object 'System.Collections.Generic.List[System.IDisposable]'
$output = Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
function Confirm-Snip([bool]$Value, [string]$Label) {
    if (-not $Value) { throw "FAILED: $Label" }
    $checks.Add($Label)
}
function Invoke-Static([Type]$Type, [string]$Name, [object[]]$Arguments) {
    $method = $Type.GetMethod($Name, $staticFlags)
    if ($null -eq $method) { throw "Required static method is missing: $($Type.FullName).$Name" }
    return $method.Invoke($null, $Arguments)
}
function Get-Internal($Target, [string]$Name) {
    $property = $Target.GetType().GetProperty($Name, $instanceFlags)
    if ($null -ne $property) { return $property.GetValue($Target, $null) }
    return $Target.GetType().GetField($Name, $instanceFlags).GetValue($Target)
}
function Set-CanvasField($Canvas, [string]$Name, $Value) {
    $canvasType.GetField($Name, $instanceFlags).SetValue($Canvas, $Value)
}
function Get-BitmapHash([Drawing.Bitmap]$Bitmap) {
    $bits=$Bitmap.LockBits([Drawing.Rectangle]::new(0,0,$Bitmap.Width,$Bitmap.Height),
        [Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $bytes=New-Object byte[] ([Math]::Abs($bits.Stride)*$bits.Height)
        [Runtime.InteropServices.Marshal]::Copy($bits.Scan0,$bytes,0,$bytes.Length)
        $hash=[Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($hash.ComputeHash($bytes)) } finally { $hash.Dispose() }
    } finally { $Bitmap.UnlockBits($bits) }
}
function Get-DocumentHash($Document) {
    $bitmap=$Document.RenderImage()
    try { return Get-BitmapHash $bitmap } finally { $bitmap.Dispose() }
}
function New-ReplacementList {
    $generic=[Collections.Generic.List[object]].GetGenericTypeDefinition().MakeGenericType([Type[]]@($replacementType))
    return ,([Activator]::CreateInstance($generic))
}
function Fit-Block([string]$Text, [Drawing.RectangleF]$Bounds, [Drawing.Rectangle]$Mask, [single]$Size = 20) {
    $arguments=[object[]]@($Text,$Bounds,$Mask,[Drawing.Color]::Black,[Drawing.Color]::White,'Segoe UI',
        [Drawing.FontStyle]::Regular,$Size,$null)
    $fit=[bool](Invoke-Static $canvasType 'TryFitTranslationBlock' $arguments)
    return [pscustomobject]@{ Fits=$fit; Replacement=$arguments[8] }
}
function Commit-Blocks($Document, $Blocks, $Identity, [long]$Revision) {
    return $documentType.GetMethod('ReplaceRecognizedTextBlocks',$instanceFlags).Invoke($Document,
        [object[]]@($Blocks,$Identity,$Revision))
}
function Parse-Arguments([string[]]$Values) {
    $arguments=New-Object object[] 2
    $arguments[0]=$Values; $arguments[1]=$null
    $ok=[bool](Invoke-Static $requestType 'TryFromArguments' $arguments)
    return [pscustomobject]@{ Accepted=$ok; Request=$arguments[1] }
}
function Parse-Body([string]$Body) {
    $arguments=[object[]]@($Body,$null)
    $ok=[bool](Invoke-Static $requestType 'TryParseBody' $arguments)
    return [pscustomobject]@{ Accepted=$ok; Request=$arguments[1] }
}
function Decode-Packet([byte[]]$Bytes) {
    $arguments=New-Object object[] 4
    $arguments[0]=$Bytes
    $ok=[bool](Invoke-Static $hostType 'TryDecodeRequest' $arguments)
    return [pscustomobject]@{ Accepted=$ok; Id=$arguments[1]; Body=$arguments[2]; Request=$arguments[3] }
}
function Apply-Guard($Canvas, $Snapshot, [string[]]$Text, [Threading.CancellationToken]$Token) {
    $list=New-Object 'System.Collections.Generic.List[string]'
    foreach ($line in $Text) { $list.Add($line) }
    # These fixtures reject before any source-style worker is scheduled.
    $task=$Canvas.ReplaceTranslatedTextAsync($Snapshot,$list,$Token)
    Confirm-Snip $task.IsCompleted 'Invalid or canceled replacement completes without background style matching.'
    return $task.GetAwaiter().GetResult()
}
try {
    $fixture=New-Object Drawing.Bitmap(420,220,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $owned.Add($fixture); $fixture.SetResolution(96,96)
    $graphics=[Drawing.Graphics]::FromImage($fixture)
    $font=New-Object Drawing.Font('Segoe UI',18,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawString('OLD FIRST',$font,[Drawing.Brushes]::Magenta,[single]20,[single]20)
        $graphics.DrawString('OLD SECOND',$font,[Drawing.Brushes]::Magenta,[single]20,[single]80)
        $graphics.FillRectangle([Drawing.Brushes]::Magenta,390,200,5,5)
    } finally { $font.Dispose(); $graphics.Dispose() }
    $document=New-Object SnipStudio.EditorDocument; $owned.Add($document)
    $document.SetImage($fixture)
    $document.AddArrow([Drawing.PointF]::new(290,140),[Drawing.PointF]::new(380,180),[Drawing.Color]::RoyalBlue,3)
    $document.AddText('Keep 0007',[Drawing.PointF]::new(255,20),[Drawing.Color]::DarkGreen,16,150)
    $keep=$document.GetTextAnnotation(1)
    $identity=Get-Internal $document 'BaseIdentity'
    $revision=$document.Revision; $count=$document.AnnotationCount
    $beforeHash=Get-DocumentHash $document
    $beforeRender=$document.RenderImage(); $owned.Add($beforeRender)
    $baseImage=$documentType.GetMethod('CopyBaseImage',$instanceFlags).Invoke($document,@())
    try { $baseHash=Get-BitmapHash $baseImage } finally { $baseImage.Dispose() }
    $first=Fit-Block 'NEW FIRST' ([Drawing.RectangleF]::new(20,20,200,28)) ([Drawing.Rectangle]::new(16,16,220,40))
    $second=Fit-Block 'NEW SECOND' ([Drawing.RectangleF]::new(20,80,200,28)) ([Drawing.Rectangle]::new(16,76,220,40))
    Confirm-Snip ($first.Fits -and $second.Fits) 'Both complete translated blocks fit their independent original source rectangles.'
    $blocks=New-ReplacementList; $blocks.Add($first.Replacement); $blocks.Add($second.Replacement)
    $last=Commit-Blocks $document $blocks $identity $revision
    Confirm-Snip ($last -eq $count+2 -and $document.AnnotationCount -eq $count+3 -and $document.Revision -eq $revision+1) 'Two replacement texts and a shared mask enter exactly one document revision.'
    Confirm-Snip ([Object]::ReferenceEquals($identity,(Get-Internal $document 'BaseIdentity'))) 'Atomic translation retains the original capture frame.'
    $baseImage=$documentType.GetMethod('CopyBaseImage',$instanceFlags).Invoke($document,@())
    try { Confirm-Snip ((Get-BitmapHash $baseImage) -ceq $baseHash) 'Translation leaves every original raster byte intact behind its reversible masks.' } finally { $baseImage.Dispose() }
    Confirm-Snip ($document.IsArrowAnnotation(1) -and $document.GetTextAnnotation(2).Rtf -ceq $keep.Rtf -and
        $document.GetTextAnnotation(2).Text -ceq 'Keep 0007') 'Unrelated arrow and formatted note survive the inserted mask with their values intact.'
    $rendered=$document.RenderImage(); $owned.Add($rendered)
    $oldColor=0; $newInk=0; $visibleBlocks=0
    foreach ($top in @(16,76)) {
        $blockInk=0
        for ($y=$top;$y -lt $top+40;$y++) {
            for ($x=16;$x -lt 236;$x++) {
                $pixel=$rendered.GetPixel($x,$y)
                if ($pixel.R -gt 150 -and $pixel.B -gt 150 -and $pixel.G -lt 100) { $oldColor++ }
                if ($pixel.R -lt 100 -and $pixel.G -lt 100 -and $pixel.B -lt 100) { $newInk++; $blockInk++ }
            }
        }
        if ($blockInk -gt 40) { $visibleBlocks++ }
    }
    Confirm-Snip ($oldColor -eq 0 -and $newInk -gt 80 -and $visibleBlocks -eq 2) 'Rendered source word pixels are covered and translated black glyphs are visible in both source areas.'
    Confirm-Snip ($rendered.GetPixel(392,202).ToArgb() -eq [Drawing.Color]::Magenta.ToArgb()) 'A control mark outside replacement areas retains its original pixels.'
    $right=[Drawing.Rectangle]::new(250,0,170,220)
    $beforeRight=$beforeRender.Clone($right,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $afterRight=$rendered.Clone($right,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        Confirm-Snip ((Get-BitmapHash $beforeRight) -ceq (Get-BitmapHash $afterRight)) 'Every rendered pixel of the unrelated note, arrow, and right-side source region is preserved.'
    } finally { $beforeRight.Dispose(); $afterRight.Dispose() }
    $afterHash=Get-BitmapHash $rendered
    $document.Undo()
    Confirm-Snip ($document.Revision -eq $revision -and $document.AnnotationCount -eq $count -and
        (Get-DocumentHash $document) -ceq $beforeHash) 'One Undo restores all original raster text and both unrelated annotations exactly.'
    $document.Redo()
    Confirm-Snip ((Get-DocumentHash $document) -ceq $afterHash) 'One Redo restores the exact complete multiblock replacement.'
    $document.Undo()
    $stable=$document.Revision
    $bad=New-ReplacementList; $bad.Add($first.Replacement); $bad.Add($null)
    $rejected=$false
    try { Commit-Blocks $document $bad $identity $stable | Out-Null } catch { $rejected=$true }
    Confirm-Snip ($rejected -and $document.Revision -eq $stable -and $document.CanRedo -and
        (Get-DocumentHash $document) -ceq $beforeHash) 'An invalid later block aborts the entire batch without pixels, revision, or redo history changing.'
    Confirm-Snip ((Commit-Blocks $document $blocks (New-Object object) $stable) -eq -1 -and
        (Commit-Blocks $document $blocks $identity ($stable-1)) -eq -1 -and $document.Revision -eq $stable) 'Document guards reject mismatched capture identity and stale revision without mutation.'
    $overflow=Fit-Block ('A long translated paragraph ' * 50) ([Drawing.RectangleF]::new(20,20,30,8)) ([Drawing.Rectangle]::new(18,18,34,12))
    Confirm-Snip (-not $overflow.Fits -and $null -eq $overflow.Replacement) 'A translation that cannot fit at six pixels is rejected with its complete text intact.'

    $canvas=New-Object SnipStudio.EditorCanvas; $owned.Add($canvas)
    $canvasDocument=$canvasType.GetField('document',$instanceFlags).GetValue($canvas)
    $canvasDocument.SetImage($fixture)
    $recognition=New-Object SnipStudio.TextRecognitionResult; $recognition.Available=$true
    foreach ($spec in @(@('OLD FIRST',20),@('OLD SECOND',80))) {
        $line=New-Object SnipStudio.TextLineRegion; $line.Text=$spec[0]
        $line.Bounds=[Drawing.RectangleF]::new(20,[single]$spec[1],200,28)
        $word=New-Object SnipStudio.TextWordRegion; $word.Text=$spec[0]; $word.Bounds=$line.Bounds
        $line.Words.Add($word); $recognition.Lines.Add($line)
    }
    $selection=New-Object SnipStudio.TextOverlaySelection -ArgumentList (,$recognition.Lines)
    Set-CanvasField $canvas 'recognizedText' $recognition
    Set-CanvasField $canvas 'recognizedBase' $canvas.ImageIdentity
    Set-CanvasField $canvas 'recognizedRevision' ([long]$canvas.DocumentRevision)
    Set-CanvasField $canvas 'textSelection' $selection
    $snapshot=$canvas.CaptureTranslationSource($false)
    Confirm-Snip ($snapshot.Blocks.Count -eq 2 -and $snapshot.Blocks[0].SourceText -ceq 'OLD FIRST' -and
        $snapshot.Blocks[1].Bounds.Y -eq 80 -and $snapshot.Revision -eq $canvas.DocumentRevision) 'Synthetic current OCR captures ordered source words, rectangles, image identity, and revision together.'
    $selection.Select(1,1)
    $selected=$canvas.CaptureTranslationSource($true)
    Confirm-Snip ($selected.Blocks.Count -eq 1 -and $selected.Blocks[0].LineIndex -eq 1 -and
        $selected.Blocks[0].SourceText -ceq 'OLD SECOND') 'Selection replacement captures only selected source words in their original line.'
    $canvasHash=Get-DocumentHash $canvasDocument; $canvasRevision=$canvas.DocumentRevision
    $none=[Threading.CancellationToken]::None
    $result=Apply-Guard $canvas $snapshot @('ONE ONLY') $none
    Confirm-Snip (-not $result.Applied) 'A translated block count mismatch is rejected.'
    $result=Apply-Guard $canvas $snapshot @('GOOD',' ') $none
    Confirm-Snip (-not $result.Applied) 'An empty translated block rejects the complete replacement.'
    $result=Apply-Guard $canvas $snapshot @(('X'*16001),'GOOD') $none
    Confirm-Snip (-not $result.Applied) 'An over-limit translated line is rejected before work starts.'
    $cancellation=New-Object Threading.CancellationTokenSource
    try {
        $cancellation.Cancel()
        $result=Apply-Guard $canvas $snapshot @('FIRST','SECOND') $cancellation.Token
        Confirm-Snip (-not $result.Applied -and -not $canvas.IsMatchingSourceText) 'A pre-canceled replacement leaves no matching operation pending.'
    } finally { $cancellation.Dispose() }
    Confirm-Snip ($canvas.DocumentRevision -eq $canvasRevision -and (Get-DocumentHash $canvasDocument) -ceq $canvasHash -and
        -not $canvas.CanUndo -and -not $canvas.IsHandleCreated) 'Invalid and canceled canvas requests preserve original pixels and Undo history without creating a canvas window.'
    $canvasDocument.AddRectangle([Drawing.RectangleF]::new(300,150,20,20),[Drawing.Color]::Blue,2)
    $changedHash=Get-DocumentHash $canvasDocument; $changedRevision=$canvas.DocumentRevision
    $result=Apply-Guard $canvas $snapshot @('FIRST','SECOND') $none
    Confirm-Snip (-not $result.Applied -and $canvas.DocumentRevision -eq $changedRevision -and
        (Get-DocumentHash $canvasDocument) -ceq $changedHash) 'A stale translation snapshot cannot change an edited snip.'
    $staleCapture=$false
    try { $canvas.CaptureTranslationSource($false) | Out-Null } catch { $staleCapture=$true }
    Confirm-Snip $staleCapture 'Stale recognized geometry cannot be recaptured as a new translation source.'

    $startupCases=@(
        [pscustomobject]@{ Args=[string[]]@(); Kind='Open'; Body='OPEN' },
        [pscustomobject]@{ Args=[string[]]@('--capture'); Kind='Capture'; Body='CAPTURE' },
        [pscustomobject]@{ Args=[string[]]@('--BACKGROUND'); Kind='Background'; Body='BACKGROUND' },
        [pscustomobject]@{ Args=[string[]]@('--shortcut-listener'); Kind='Background'; Body='BACKGROUND' })
    foreach ($case in $startupCases) {
        $parsed=Parse-Arguments $case.Args
        Confirm-Snip ($parsed.Accepted -and (Get-Internal $parsed.Request 'Kind').ToString() -eq $case.Kind -and
            (Parse-Body $case.Body).Accepted) ("Startup arguments accept only the expected $($case.Body) action.")
        $body=$requestType.GetMethod('ToBody',$instanceFlags).Invoke($parsed.Request,@())
        Confirm-Snip ($body -ceq $case.Body) ("Startup action serializes exactly as $($case.Body).")
    }
    foreach ($path in @('C:\synthetic snips\0007.PNG','C:\synthetic\..\part.jpg','\\server\share\part.tiff')) {
        $parsed=Parse-Arguments @($path)
        Confirm-Snip ($parsed.Accepted -and (Get-Internal $parsed.Request 'Kind').ToString() -eq 'Open') "Absolute supported image path is accepted: $path"
        $body=$requestType.GetMethod('ToBody',$instanceFlags).Invoke($parsed.Request,@())
        $roundtrip=Parse-Body $body
        Confirm-Snip ($roundtrip.Accepted -and (Get-Internal $roundtrip.Request 'ImagePath') -ceq
            [IO.Path]::GetFullPath($path)) 'Image protocol round trip preserves the normalized exact path.'
    }
    foreach ($invalid in @('part.png','C:part.png','\part.png','C:\part.exe','C:\part.png:other',
        '\\?\C:\part.png','\\.\pipe\part.png','\\server\part.png',"C:\a`npart.png",'--exit','--capture=now','')) {
        Confirm-Snip (-not (Parse-Arguments @($invalid)).Accepted) 'Unsafe, relative, unsupported, or unrecognized startup input is rejected.'
    }
    Confirm-Snip (-not (Parse-Arguments @('--capture','C:\part.png')).Accepted) 'Multiple startup actions are rejected.'
    foreach ($body in @('open','CAPTURE extra',"OPEN`nC:\part.png",'IMAGE','IMAGE\nC:\part.png',"IMAGE`nC:\a`0.png")) {
        Confirm-Snip (-not (Parse-Body $body).Accepted) 'Malformed or injected protocol body is rejected.'
    }
    $packetId='0123456789abcdef0123456789abcdef'
    foreach ($body in @('OPEN','CAPTURE','BACKGROUND',"IMAGE`nC:\synthetic\0007.png")) {
        $bytes=[Text.Encoding]::Unicode.GetBytes($packetId+"`n"+$body+"`0")
        $decoded=Decode-Packet $bytes
        Confirm-Snip ($decoded.Accepted -and $decoded.Id -ceq $packetId -and $decoded.Body -ceq $body -and
            $null -ne $decoded.Request) 'Strict UTF-16 protocol envelope accepts an exact ID and supported request.'
    }
    $valid=[Text.Encoding]::Unicode.GetBytes($packetId+"`nOPEN`0")
    $odd=New-Object byte[] ($valid.Length-1); [Array]::Copy($valid,$odd,$odd.Length)
    $unterminated=[Text.Encoding]::Unicode.GetBytes($packetId+"`nOPEN")
    $embedded=[Text.Encoding]::Unicode.GetBytes($packetId+"`nOPEN`0`0")
    $badId=[Text.Encoding]::Unicode.GetBytes(('Z'*32)+"`nOPEN`0")
    $badSeparator=[Text.Encoding]::Unicode.GetBytes($packetId+" OPEN`0")
    $malformed=[Text.Encoding]::Unicode.GetBytes($packetId+"`nOPEN`0")
    $malformed[0]=0; $malformed[1]=216 # Unpaired high UTF-16 surrogate.
    $tooLarge=New-Object byte[] 65538
    $atLimit=New-Object byte[] 65536 # Within byte limit, invalid semantic envelope.
    $badPackets=@(
        [pscustomobject]@{ Data=$null },[pscustomobject]@{ Data=(New-Object byte[] 0) },
        [pscustomobject]@{ Data=(New-Object byte[] 2) },[pscustomobject]@{ Data=$odd },
        [pscustomobject]@{ Data=$unterminated },[pscustomobject]@{ Data=$embedded },
        [pscustomobject]@{ Data=$badId },[pscustomobject]@{ Data=$badSeparator },
        [pscustomobject]@{ Data=$malformed },[pscustomobject]@{ Data=$tooLarge },[pscustomobject]@{ Data=$atLimit })
    foreach ($badPacket in $badPackets) {
        $decoded=Decode-Packet $badPacket.Data
        Confirm-Snip (-not $decoded.Accepted -and $null -eq $decoded.Id -and $null -eq $decoded.Body -and
            $null -eq $decoded.Request) 'Invalid envelope length, terminator, ID, separator, Unicode, or embedded NUL rejects all parsed outputs.'
    }
    Confirm-Snip ($hostType.GetField('MaxRequestBytes',$staticFlags).GetRawConstantValue() -eq 65536 -and
        $hostType.GetField('MaxQueuedRequests',$staticFlags).GetRawConstantValue() -eq 16) 'Protocol and pending queue bounds remain 65,536 bytes and 16 requests.'
    foreach ($windows in @(0,1,2)) {
        foreach ($keep in @($false,$true)) {
            foreach ($exit in @($false,$true)) {
                $actual=[bool](Invoke-Static $lifetimeType 'ShouldKeepListenerAlive' @($windows,$keep,$exit))
                Confirm-Snip ($actual -eq ($windows -eq 0 -and $keep -and -not $exit)) 'Resident lifetime policy requires zero editors, keep enabled, and no explicit exit.'
            }
        }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $rendered.Save((Join-Path $output 'translation-replacement-proof.png'),[Drawing.Imaging.ImageFormat]::Png)
    $report=[ordered]@{ Version=$version; Signature=[string]$signature.Status; CheckCount=$checks.Count;
        NoVisibleWindows=$true; NoDesktopCapture=$true; NoClipboardAccess=$true; NoPreferencesOrRegistryAccess=$true;
        NoShortcutHostConstruction=$true; NoNativeHotkeyOrTrayOrMutex=$true; Checks=$checks.ToArray();
        Unverified='Real OCR, live translation service, successful asynchronous source-style matching through a visible editor, native IPC discovery/delivery and deduplication, global hotkeys, tray, login startup, and actual close/reopen message-loop behavior.' }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'translation-replacement-results.json') -Encoding UTF8
    $checks | Set-Content -LiteralPath (Join-Path $output 'translation-replacement-results.txt') -Encoding UTF8
    Write-Output ("PASS: {0} signed-release data checks. No application window, desktop capture, clipboard, user preferences, registry, native shortcut listener, or helper executable was used." -f $checks.Count)
} finally {
    try { Invoke-Static $layoutType 'ClearCache' @() | Out-Null }
    finally { for ($i=$owned.Count-1; $i -ge 0; $i--) { $owned[$i].Dispose() } }
}

# SIG # Begin signature block
# MII2twYJKoZIhvcNAQcCoII2qDCCNqQCAQExDzANBglghkgBZQMEAgEFADB5Bgor
# BgEEAYI3AgEEoGswaTA0BgorBgEEAYI3AgEeMCYCAwEAAAQQH8w7YFlLCE63JNLG
# KX7zUQIBAAIBAAIBAAIBAAIBADAxMA0GCWCGSAFlAwQCAQUABCAg4OL+DkCh+bZZ
# yEvSMTdqVmIJHYIHYrGyyKu5IDmZYaCCG1MwggXMMIIDtKADAgECAhBUmNLR1FsZ
# lUgTecgRwIeZMA0GCSqGSIb3DQEBDAUAMHcxCzAJBgNVBAYTAlVTMR4wHAYDVQQK
# ExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jvc29mdCBJZGVu
# dGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRob3JpdHkgMjAy
# MDAeFw0yMDA0MTYxODM2MTZaFw00NTA0MTYxODQ0NDBaMHcxCzAJBgNVBAYTAlVT
# MR4wHAYDVQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xSDBGBgNVBAMTP01pY3Jv
# c29mdCBJZGVudGl0eSBWZXJpZmljYXRpb24gUm9vdCBDZXJ0aWZpY2F0ZSBBdXRo
# b3JpdHkgMjAyMDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBALORKgeD
# Bmf9np3gx8C3pOZCBH8Ppttf+9Va10Wg+3cL8IDzpm1aTXlT2KCGhFdFIMeiVPvH
# or+Kx24186IVxC9O40qFlkkN/76Z2BT2vCcH7kKbK/ULkgbk/WkTZaiRcvKYhOuD
# PQ7k13ESSCHLDe32R0m3m/nJxxe2hE//uKya13NnSYXjhr03QNAlhtTetcJtYmrV
# qXi8LW9J+eVsFBT9FMfTZRY33stuvF4pjf1imxUs1gXmuYkyM6Nix9fWUmcIxC70
# ViueC4fM7Ke0pqrrBc0ZV6U6CwQnHJFnni1iLS8evtrAIMsEGcoz+4m+mOJyoHI1
# vnnhnINv5G0Xb5DzPQCGdTiO0OBJmrvb0/gwytVXiGhNctO/bX9x2P29Da6SZEi3
# W295JrXNm5UhhNHvDzI9e1eM80UHTHzgXhgONXaLbZ7LNnSrBfjgc10yVpRnlyUK
# xjU9lJfnwUSLgP3B+PR0GeUw9gb7IVc+BhyLaxWGJ0l7gpPKWeh1R+g/OPTHU3mg
# trTiXFHvvV84wRPmeAyVWi7FQFkozA8kwOy6CXcjmTimthzax7ogttc32H83rwjj
# O3HbbnMbfZlysOSGM1l0tRYAe1BtxoYT2v3EOYI9JACaYNq6lMAFUSw0rFCZE4e7
# swWAsk0wAly4JoNdtGNz764jlU9gKL431VulAgMBAAGjVDBSMA4GA1UdDwEB/wQE
# AwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQWBBTIftJqhSobyhmYBAcnz1AQ
# T2ioojAQBgkrBgEEAYI3FQEEAwIBADANBgkqhkiG9w0BAQwFAAOCAgEAr2rd5hnn
# LZRDGU7L6VCVZKUDkQKL4jaAOxWiUsIWGbZqWl10QzD0m/9gdAmxIR6QFm3FJI9c
# Zohj9E/MffISTEAQiwGf2qnIrvKVG8+dBetJPnSgaFvlVixlHIJ+U9pW2UYXeZJF
# xBA2CFIpF8svpvJ+1Gkkih6PsHMNzBxKq7Kq7aeRYwFkIqgyuH4yKLNncy2RtNwx
# AQv3Rwqm8ddK7VZgxCwIo3tAsLx0J1KH1r6I3TeKiW5niB31yV2g/rarOoDXGpc8
# FzYiQR6sTdWD5jw4vU8w6VSp07YEwzJ2YbuwGMUrGLPAgNW3lbBeUU0i/OxYqujY
# lLSlLu2S3ucYfCFX3VVj979tzR/SpncocMfiWzpbCNJbTsgAlrPhgzavhgplXHT2
# 6ux6anSg8Evu75SjrFDyh+3XOjCDyft9V77l4/hByuVkrrOj7FjshZrM77nq81YY
# uVxzmq/FdxeDWds3GhhyVKVB0rYjdaNDmuV3fJZ5t0GNv+zcgKCf0Xd1WF81E+Al
# GmcLfc4l+gcK5GEh2NQc5QfGNpn0ltDGFf5Ozdeui53bFv0ExpK91IjmqaOqu/dk
# ODtfzAzQNb50GQOmxapMomE2gj4d8yu8l13bS3g7LfU772Aj6PXsCyM2la+YZr9T
# 03u4aUoqlmZpxJTG9F9urJh4iIAGXKKy7aIwggaxMIIEmaADAgECAhMzAAxhlyl6
# eE8Q6l8PAAAADGGXMA0GCSqGSIb3DQEBDAUAMFoxCzAJBgNVBAYTAlVTMR4wHAYD
# VQQKExVNaWNyb3NvZnQgQ29ycG9yYXRpb24xKzApBgNVBAMTIk1pY3Jvc29mdCBJ
# RCBWZXJpZmllZCBDUyBBT0MgQ0EgMDQwHhcNMjYxMTE3MDk1MDE4WhcNMjYxMTIw
# MDk1MDE4WjB0MQswCQYDVQQGEwJVUzERMA8GA1UECBMITmV3IFlvcmsxFDASBgNV
# BAcTC1NwZW5jZXJwb3J0MR0wGwYDVQQKExRBdXRvbWF0aW9uIEZvcmdlIExMQzEd
# MBsGA1UEAxMUQXV0b21hdGlvbiBGb3JnZSBMTEMwggGiMA0GCSqGSIb3DQEBAQUA
# A4IBjwAwggGKAoIBgQCOW/q1Szb+TCEemrAABKnvjy7UAtBiRDW6XLVrhkV7CBQH
# tt5bs5BjlgOYccWI9Qno1HiAAow3aZm+wjHHesRHK87LJ92KqhBpVuyHTBO+9h6Z
# zwwx9HSbfC5r4Mw90CHC7eiNEI7PO+lU2uLuLYoe8a2q9kLPJ0LkqeY361HDh3j2
# /NxS7xx08rcux2PZrKMEeFo6E0ZaSQOifK4UxIpZOSF0KDY6F/By4wVfDdVaKIWg
# ecV8Y3b3bWzwM64DC+8gOgXMSiBBprew4d/LZTxbhkPDuDc7hQy+m/AqCW72lKXQ
# hr+0szgNbmOXaSyq+nld5H5HBrKdCrBDJpsHgv7iFm06VemFxr0PdEMEiYQV9VHM
# mVeF0aCEzEViIpVCh+uxkuPCOAvfc4VPmKKuxrNe+ejDYlrQKlLqQhwlg+uUI1E7
# gPhDEansl8hwI97zN9nf+vJu4xuGSW4uXGwpuUTx6tZMQMEv5a+OyQixuE1F3wW9
# hZlqxW0PU6kMFVMJbgcCAwEAAaOCAdQwggHQMAwGA1UdEwEB/wQCMAAwDgYDVR0P
# AQH/BAQDAgeAMDsGA1UdJQQ0MDIGCisGAQQBgjdhAQAGCCsGAQUFBwMDBhorBgEE
# AYI3YYHZ++pilcS8boyU8DKCgaGqKjAdBgNVHQ4EFgQUjuK8UcF6MLnHSXikZLNX
# LxZ80o4wHwYDVR0jBBgwFoAUayVB3vtrfP0YgAotf492XapzPbgwZwYDVR0fBGAw
# XjBcoFqgWIZWaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWlj
# cm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENTJTIwQU9DJTIwQ0ElMjAwNC5jcmww
# dAYIKwYBBQUHAQEEaDBmMGQGCCsGAQUFBzAChlhodHRwOi8vd3d3Lm1pY3Jvc29m
# dC5jb20vcGtpb3BzL2NlcnRzL01pY3Jvc29mdCUyMElEJTIwVmVyaWZpZWQlMjBD
# UyUyMEFPQyUyMENBJTIwMDQuY3J0MFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsG
# AQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVw
# b3NpdG9yeS5odG0wDQYJKoZIhvcNAQEMBQADggIBAKeN+p5uHUCbUwD371G3ZZoP
# XcJ0NPLeT/s+Qd+2ctgF20DCsjGmfO7U5Am8fK2yWwfz6xdwbSIo6AHkCPxJtI8e
# i5k+UU54lmlYtBXbrBh8EhFPFqFYq6GT1ExxG7GdCBBFkvpprSG8jxEvIMDukoON
# t7Z8ODChqrTxoVjwcwcXIyJ1BMOS5M98+MA8ARRNsHnT2cV2Erbvu222d7Gh30T6
# 8wZuSX51DUHU+g5QYbKS99mKOax5IxNOIBmXW0VSggs8yc+nKlfBcrZIVAWAZ8VC
# 7tPI5vRLO4wpIrDjYm+INL82XXSYR9obYDsB2aVya2szql+oaguxZISw/yudfkHN
# U6TLV1B7SUJwbo0kpb8I+RpSuok8tnStPLj99J4nFpJHIRH4QKhVeuUYnZkn3YLT
# CfGLbfx6jJtjV4AQePY3pLBnmJ1BHfuXfawEqoeRldNR1j4NGsK2P6t4mk5t/++y
# ICj7fZFKqLA5bZ1qgpoNT2OGYit7XaacKyOlItyc8mRMAq743t1v926Hi6weoHUx
# g2LBF9dky2R+XlMeJvmIJaw3md1j0iTpBQuJRraE7d75eGKKECLiLoy55zMcWW7a
# iWChUi/XSvJlu0iR541/hKkjbKwlP90EnfWHS5wX1IfXXM6XwVHNLnvPccU6FxwK
# f4x/MJHxK6NbIrKgz8HEMIIHKDCCBRCgAwIBAgITMwAAABYxko2SAmV7mgAAAAAA
# FjANBgkqhkiG9w0BAQwFADBjMQswCQYDVQQGEwJVUzEeMBwGA1UEChMVTWljcm9z
# b2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytNaWNyb3NvZnQgSUQgVmVyaWZpZWQg
# Q29kZSBTaWduaW5nIFBDQSAyMDIxMB4XDTI2MDMyNjE4MTEyOVoXDTMxMDMyNjE4
# MTEyOVowWjELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3Jh
# dGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElEIFZlcmlmaWVkIENTIEFPQyBDQSAw
# NDCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAMpV+sjb6Akwz/RtDk5U
# o1284BLMttRQy9e5/5WXtga6h89pkdWjeAwcXmKdP4YxKkhx8hn2q1dTVQbryvLy
# 81tC04vg2bfSJ9emojX4HKIBYs7VhPRafMbtc866hN55aw1m/kWaPEKxF4Fm/LPL
# MLJdlu7URB8nFZMfh5tTC0CJb2uox/14OP/BiGIR8214lXdkV6JsPbO0Iev0mEV1
# 33tducIeBChMipzTZfnGVEq1QYFr7460cEGOIn+7AGfNOSq7gWOlmNB4m2uZ1r66
# vUJPaN+VYgH/Kmfu7tX229b3Alsli38fYS0nQY41bElntMS7yNY+Kd047eXM8/tS
# 3NL+ZUNX3ge5xZqW2aytrrNIbwGgQnzsgzxBJvu9+b87jUWFiRS/z1YiPkRLY7iT
# HKIxJ973kIyK8K5itE/aEq/Ht6A8ytaAMMGTEwuCspk72FE1Qyby+TfDlv1KiAc7
# IlWHHIWbxoVd8jGCoMXhLSDhuFuGfOWOZKUIuW6YxlxcUYOOkXa02gC7dUYUZi0e
# 1NI1Uq9mmAHdvjKdqgORs9/5aDjnbeO3hWd5qwGBmELWytkjY0KcIEF+CMurTimo
# QoBYSiHJbYrb0pKSQe+3lVoTVpzdi36jKI7MxLnu1fBAMksa8uD85cU7RU29zMOd
# 18h9dgTEH9pvY+4xLB3lUgE7AgMBAAGjggHcMIIB2DAOBgNVHQ8BAf8EBAMCAYYw
# EAYJKwYBBAGCNxUBBAMCAQAwHQYDVR0OBBYEFGslQd77a3z9GIAKLX+Pdl2qcz24
# MFQGA1UdIARNMEswSQYEVR0gADBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1p
# Y3Jvc29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wGQYJKwYBBAGC
# NxQCBAweCgBTAHUAYgBDAEEwEgYDVR0TAQH/BAgwBgEB/wIBADAfBgNVHSMEGDAW
# gBTZQSmwDw9jbO9p1/XNKZ6kSGow5jBwBgNVHR8EaTBnMGWgY6Bhhl9odHRwOi8v
# d3d3Lm1pY3Jvc29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJRCUyMFZl
# cmlmaWVkJTIwQ29kZSUyMFNpZ25pbmclMjBQQ0ElMjAyMDIxLmNybDB9BggrBgEF
# BQcBAQRxMG8wbQYIKwYBBQUHMAKGYWh0dHA6Ly93d3cubWljcm9zb2Z0LmNvbS9w
# a2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSUQlMjBWZXJpZmllZCUyMENvZGUlMjBT
# aWduaW5nJTIwUENBJTIwMjAyMS5jcnQwDQYJKoZIhvcNAQEMBQADggIBAAbVUF5U
# dNVEGWOVxkPciIzHA/IyNDIs1oSdW7mY4ObaFEEl8fwawEsBOascBzwXjuOaTkKe
# lQ+IiC4JvDpn8pWPgOyiKrbbw1Iswt7AV8YyuLu7A0YshILlxyny9R0AMMQixLZ0
# T5Aj07CQOm/de5QPt36ECBwtVww6XUCx8Rm2xl6eEa9JhSub8W4urQGoQYv0Zczl
# z4ej2ryj5Wf9L4ZZA3bL6CRE7XmzSQjTmdSmr904PiuL2uBWzq2KkR3RhmoaAP4J
# k2JplasNM5Bs0+dx3YX2o6xRrbSaJiu6hPk/AkBoj5BCJMTZkl4wk6Q6nOFNSCpU
# xnBmJ0RRkoKq51p5ADTxbCeRAx8rIfNpTyPjxQtxTiFTC8yy/t9K6s570YB1FAI+
# 8XxZxIrmgMd7xVUkzi2/oooKb3UeovH0lqYGBEjpHZJ9jee7boQbOe7SsKD/vr3P
# zFabg9VwLZlovpWUpWoWnU2w3xiozJ/m35tsZVvT2egUpwkb9TDIaXFGZAGV94FR
# DHn/K2XNnOwSeGskX19MB+N7yPc51fmUSd49MVAtGW/NkUGpRech5Aq/d8dCML2b
# Z+j9nTUMgj+qnd3wuEZVRbQEIbTh9HAck0fyKqoIb+qh8y/6TKbYDEEOcfM2BGMv
# rQk4WIGCK6u8uFhA2EH3kpaiMUDqyFCeCCQxMIIHnjCCBYagAwIBAgITMwAAAAeH
# ozSje6WOHAAAAAAABzANBgkqhkiG9w0BAQwFADB3MQswCQYDVQQGEwJVUzEeMBwG
# A1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMUgwRgYDVQQDEz9NaWNyb3NvZnQg
# SWRlbnRpdHkgVmVyaWZpY2F0aW9uIFJvb3QgQ2VydGlmaWNhdGUgQXV0aG9yaXR5
# IDIwMjAwHhcNMjEwNDAxMjAwNTIwWhcNMzYwNDAxMjAxNTIwWjBjMQswCQYDVQQG
# EwJVUzEeMBwGA1UEChMVTWljcm9zb2Z0IENvcnBvcmF0aW9uMTQwMgYDVQQDEytN
# aWNyb3NvZnQgSUQgVmVyaWZpZWQgQ29kZSBTaWduaW5nIFBDQSAyMDIxMIICIjAN
# BgkqhkiG9w0BAQEFAAOCAg8AMIICCgKCAgEAsvDArxmIKOLdVHpMSWxpCFUJtFL/
# ekr4weslKPdnF3cpTeuV8veqtmKVgok2rO0D05BpyvUDCg1wdsoEtuxACEGcgHfj
# PF/nZsOkg7c0mV8hpMT/GvB4uhDvWXMIeQPsDgCzUGzTvoi76YDpxDOxhgf8JuXW
# JzBDoLrmtThX01CE1TCCvH2sZD/+Hz3RDwl2MsvDSdX5rJDYVuR3bjaj2QfzZFmw
# fccTKqMAHlrz4B7ac8g9zyxlTpkTuJGtFnLBGasoOnn5NyYlf0xF9/bjVRo4Gzg2
# Yc7KR7yhTVNiuTGH5h4eB9ajm1OCShIyhrKqgOkc4smz6obxO+HxKeJ9bYmPf6KL
# XVNLz8UaeARo0BatvJ82sLr2gqlFBdj1sYfqOf00Qm/3B4XGFPDK/H04kteZEZsB
# Rc3VT2d/iVd7OTLpSH9yCORV3oIZQB/Qr4nD4YT/lWkhVtw2v2s0TnRJubL/hFMI
# Qa86rcaGMhNsJrhysLNNMeBhiMezU1s5zpusf54qlYu2v5sZ5zL0KvBDLHtL8F9g
# n6jOy3v7Jm0bbBHjrW5yQW7S36ALAt03QDpwW1JG1Hxu/FUXJbBO2AwwVG4Fre+Z
# Q5Od8ouwt59FpBxVOBGfN4vN2m3fZx1gqn52GvaiBz6ozorgIEjn+PhUXILhAV5Q
# /ZgCJ0u2+ldFGjcCAwEAAaOCAjUwggIxMA4GA1UdDwEB/wQEAwIBhjAQBgkrBgEE
# AYI3FQEEAwIBADAdBgNVHQ4EFgQU2UEpsA8PY2zvadf1zSmepEhqMOYwVAYDVR0g
# BE0wSzBJBgRVHSAAMEEwPwYIKwYBBQUHAgEWM2h0dHA6Ly93d3cubWljcm9zb2Z0
# LmNvbS9wa2lvcHMvRG9jcy9SZXBvc2l0b3J5Lmh0bTAZBgkrBgEEAYI3FAIEDB4K
# AFMAdQBiAEMAQTAPBgNVHRMBAf8EBTADAQH/MB8GA1UdIwQYMBaAFMh+0mqFKhvK
# GZgEByfPUBBPaKiiMIGEBgNVHR8EfTB7MHmgd6B1hnNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL2NybC9NaWNyb3NvZnQlMjBJZGVudGl0eSUyMFZlcmlm
# aWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1dGhvcml0eSUyMDIwMjAu
# Y3JsMIHDBggrBgEFBQcBAQSBtjCBszCBgQYIKwYBBQUHMAKGdWh0dHA6Ly93d3cu
# bWljcm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwSWRlbnRpdHkl
# MjBWZXJpZmljYXRpb24lMjBSb290JTIwQ2VydGlmaWNhdGUlMjBBdXRob3JpdHkl
# MjAyMDIwLmNydDAtBggrBgEFBQcwAYYhaHR0cDovL29uZW9jc3AubWljcm9zb2Z0
# LmNvbS9vY3NwMA0GCSqGSIb3DQEBDAUAA4ICAQB/JSqe/tSr6t1mCttXI0y6XmyQ
# 41uGWzl9xw+WYhvOL47BV09Dgfnm/tU4ieeZ7NAR5bguorTCNr58HOcA1tcsHQqt
# 0wJsdClsu8bpQD9e/al+lUgTUJEV80Xhco7xdgRrehbyhUf4pkeAhBEjABvIUpD2
# LKPho5Z4DPCT5/0TlK02nlPwUbv9URREhVYCtsDM+31OFU3fDV8BmQXv5hT2RurV
# sJHZgP4y26dJDVF+3pcbtvh7R6NEDuYHYihfmE2HdQRq5jRvLE1Eb59PYwISFCX2
# DaLZ+zpU4bX0I16ntKq4poGOFaaKtjIA1vRElItaOKcwtc04CBrXSfyL2Op6mvNI
# xTk4OaswIkTXbFL81ZKGD+24uMCwo/pLNhn7VHLfnxlMVzHQVL+bHa9KhTyzwdG/
# L6uderJQn0cGpLQMStUuNDArxW2wF16QGZ1NtBWgKA8Kqv48M8HfFqNifN6+zt6J
# 0GwzvU8g0rYGgTZR8zDEIJfeZxwWDHpSxB5FJ1VVU1LIAtB7o9PXbjXzGifaIMYT
# zU4YKt4vMNwwBmetQDHhdAtTPplOXrnI9SI6HeTtjDD3iUN/7ygbahmYOHk7VB7f
# wT4ze+ErCbMh6gHV1UuXPiLciloNxH6K4aMfZN1oLVk6YFeIJEokuPgNPa6EnTiO
# L60cPqfny+Fq8UiuZzGCGrowghq2AgEBMHEwWjELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjErMCkGA1UEAxMiTWljcm9zb2Z0IElE
# IFZlcmlmaWVkIENTIEFPQyBDQSAwNAITMwAMYZcpenhPEOpfDwAAAAxhlzANBglg
# hkgBZQMEAgEFAKCBhDAYBgorBgEEAYI3AgEMMQowCKACgAChAoAAMBkGCSqGSIb3
# DQEJAzEMBgorBgEEAYI3AgEEMBwGCisGAQQBgjcCAQsxDjAMBgorBgEEAYI3AgEV
# MC8GCSqGSIb3DQEJBDEiBCBdXYU1FiEv9wRaWuzu8Bmixr0EnJsFjgDZiaWOauE+
# NzANBgkqhkiG9w0BAQEFAASCAYAULPaMVpqRMB/OjlGDUDLSiOQ1VIkgl2zDBJdK
# Ujk5SwOQqKoAQoIF3w2EXOGaO/I9rigEoTq7C0W6Kblj3VHJO0XIhCuQq4FB4Fk0
# mW3UgNZ7GuiBGwNsTMSotdeHf+Ku2c67Gy0yhzRn7Jzh/KeYjKZ4R7tFHWwRxKRH
# 46sBBbqp/Gp96tiILwSKZgbA2TLnU31F9sQQA7KbNFJfxf2Y72AYeT5pHH/MQiAl
# /wrnKQkntOEyqgfIP7R0+eBMIWOA6Px7goD6a/ldCl7lBBL/HLKzgEUMrtjfXGAM
# XGvO0Jd71TF7mBCoBZaeKCUsKg0PeY5DzROUYEHxR6vcMmuLaRT+DGxX7i7OrQeV
# G3sftQXw3XXgtClqMBBPDkPGo/TtBoNeI1Ry+JDD0GhLacY9FFiI8MMID9fZD1s3
# IYejcz2QzesiBm9FPA2MTe9LSdhLn6TKhb2AWSNWfQg53uOZpsmYQCK/IjP9G6Xo
# ufka/jTJH+crN/TI9e7Y/B2WkMqhghgTMIIYDwYKKwYBBAGCNwMDATGCF/8wghf7
# BgkqhkiG9w0BBwKgghfsMIIX6AIBAzEPMA0GCWCGSAFlAwQCAQUAMIIBYQYLKoZI
# hvcNAQkQAQSgggFQBIIBTDCCAUgCAQEGCisGAQQBhFkKAwEwMTANBglghkgBZQME
# AgEFAAQgtScj8Qkn9LfZ8YdJMPohcXkTeCiOuriUpD5vz1Gf2MACBmr8jXjNjxgS
# MjAyNjExMTgyMTMxMDUuMDFaMASAAgH0oIHhpIHeMIHbMQswCQYDVQQGEwJVUzET
# MBEGA1UECBMKV2FzaGluZ3RvbjEQMA4GA1UEBxMHUmVkbW9uZDEeMBwGA1UEChMV
# TWljcm9zb2Z0IENvcnBvcmF0aW9uMSUwIwYDVQQLExxNaWNyb3NvZnQgQW1lcmlj
# YSBPcGVyYXRpb25zMScwJQYDVQQLEx5uU2hpZWxkIFRTUyBFU046N0EwMC0wNUUw
# LUQ5NDcxNTAzBgNVBAMTLE1pY3Jvc29mdCBQdWJsaWMgUlNBIFRpbWUgU3RhbXBp
# bmcgQXV0aG9yaXR5oIIPITCCB4IwggVqoAMCAQICEzMAAAAF5c8P/2YuyYcAAAAA
# AAUwDQYJKoZIhvcNAQEMBQAwdzELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jv
# c29mdCBDb3Jwb3JhdGlvbjFIMEYGA1UEAxM/TWljcm9zb2Z0IElkZW50aXR5IFZl
# cmlmaWNhdGlvbiBSb290IENlcnRpZmljYXRlIEF1dGhvcml0eSAyMDIwMB4XDTIw
# MTExOTIwMzIzMVoXDTM1MTExOTIwNDIzMVowYTELMAkGA1UEBhMCVVMxHjAcBgNV
# BAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1
# YmxpYyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwggIiMA0GCSqGSIb3DQEBAQUA
# A4ICDwAwggIKAoICAQCefOdSY/3gxZ8FfWO1BiKjHB7X55cz0RMFvWVGR3eRwV1w
# b3+yq0OXDEqhUhxqoNv6iYWKjkMcLhEFxvJAeNcLAyT+XdM5i2CgGPGcb95WJLiw
# 7HzLiBKrxmDj1EQB/mG5eEiRBEp7dDGzxKCnTYocDOcRr9KxqHydajmEkzXHOeRG
# wU+7qt8Md5l4bVZrXAhK+WSk5CihNQsWbzT1nRliVDwunuLkX1hyIWXIArCfrKM3
# +RHh+Sq5RZ8aYyik2r8HxT+l2hmRllBvE2Wok6IEaAJanHr24qoqFM9WLeBUSudz
# +qL51HwDYyIDPSQ3SeHtKog0ZubDk4hELQSxnfVYXdTGncaBnB60QrEuazvcob9n
# 4yR65pUNBCF5qeA4QwYnilBkfnmeAjRN3LVuLr0g0FXkqfYdUmj1fFFhH8k8YBoz
# rEaXnsSL3kdTD01X+4LfIWOuFzTzuoslBrBILfHNj8RfOxPgjuwNvE6YzauXi4or
# p4Sm6tF245DaFOSYbWFK5ZgG6cUY2/bUq3g3bQAqZt65KcaewEJ3ZyNEobv35Nf6
# xN6FrA6jF9447+NHvCjeWLCQZ3M8lgeCcnnhTFtyQX3XgCoc6IRXvFOcPVrr3D9R
# PHCMS6Ckg8wggTrtIVnY8yjbvGOUsAdZbeXUIQAWMs0d3cRDv09SvwVRd61evQID
# AQABo4ICGzCCAhcwDgYDVR0PAQH/BAQDAgGGMBAGCSsGAQQBgjcVAQQDAgEAMB0G
# A1UdDgQWBBRraSg6NS9IY0DPe9ivSek+2T3bITBUBgNVHSAETTBLMEkGBFUdIAAw
# QTA/BggrBgEFBQcCARYzaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9E
# b2NzL1JlcG9zaXRvcnkuaHRtMBMGA1UdJQQMMAoGCCsGAQUFBwMIMBkGCSsGAQQB
# gjcUAgQMHgoAUwB1AGIAQwBBMA8GA1UdEwEB/wQFMAMBAf8wHwYDVR0jBBgwFoAU
# yH7SaoUqG8oZmAQHJ89QEE9oqKIwgYQGA1UdHwR9MHsweaB3oHWGc2h0dHA6Ly93
# d3cubWljcm9zb2Z0LmNvbS9wa2lvcHMvY3JsL01pY3Jvc29mdCUyMElkZW50aXR5
# JTIwVmVyaWZpY2F0aW9uJTIwUm9vdCUyMENlcnRpZmljYXRlJTIwQXV0aG9yaXR5
# JTIwMjAyMC5jcmwwgZQGCCsGAQUFBwEBBIGHMIGEMIGBBggrBgEFBQcwAoZ1aHR0
# cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jZXJ0cy9NaWNyb3NvZnQlMjBJ
# ZGVudGl0eSUyMFZlcmlmaWNhdGlvbiUyMFJvb3QlMjBDZXJ0aWZpY2F0ZSUyMEF1
# dGhvcml0eSUyMDIwMjAuY3J0MA0GCSqGSIb3DQEBDAUAA4ICAQBfiHbHfm21WhV1
# 50x4aPpO4dhEmSUVpbixNDmv6TvuIHv1xIs174bNGO/ilWMm+Jx5boAXrJxagRhH
# QtiFprSjMktTliL4sKZyt2i+SXncM23gRezzsoOiBhv14YSd1Klnlkzvgs29XNjT
# +c8hIfPRe9rvVCMPiH7zPZcw5nNjthDQ+zD563I1nUJ6y59TbXWsuyUsqw7wXZoG
# zZwijWT5oc6GvD3HDokJY401uhnj3ubBhbkR83RbfMvmzdp3he2bvIUztSOuFzRq
# rLfEvsPkVHYnvH1wtYyrt5vShiKheGpXa2AWpsod4OJyT4/y0dggWi8g/tgbhmQl
# ZqDUf3UqUQsZaLdIu/XSjgoZqDjamzCPJtOLi2hBwL+KsCh0Nbwc21f5xvPSwym0
# Ukr4o5sCcMUcSy6TEP7uMV8RX0eH/4JLEpGyae6Ki8JYg5v4fsNGif1OXHJ2IWG+
# 7zyjTDfkmQ1snFOTgyEX8qBpefQbF0fx6URrYiarjmBprwP6ZObwtZXJ23jK3Fg/
# 9uqM3j0P01nzVygTppBabzxPAh/hHhhls6kwo3QLJ6No803jUsZcd4JQxiYHHc+Q
# /wAMcPUnYKv/q2O444LO1+n6j01z5mggCSlRwD9faBIySAcA9S8h22hIAcRQqIGE
# jolCK9F6nK9ZyX4lhthsGHumaABdWzCCB5cwggV/oAMCAQICEzMAAABdxjl4T0B9
# M/0AAAAAAF0wDQYJKoZIhvcNAQEMBQAwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoT
# FU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1Ymxp
# YyBSU0EgVGltZXN0YW1waW5nIENBIDIwMjAwHhcNMjYwOTE3MTg1NzIxWhcNMjcw
# OTE0MTg1NzIxWjCB2zELMAkGA1UEBhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24x
# EDAOBgNVBAcTB1JlZG1vbmQxHjAcBgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlv
# bjElMCMGA1UECxMcTWljcm9zb2Z0IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UE
# CxMeblNoaWVsZCBUU1MgRVNOOjdBMDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNy
# b3NvZnQgUHVibGljIFJTQSBUaW1lIFN0YW1waW5nIEF1dGhvcml0eTCCAiIwDQYJ
# KoZIhvcNAQEBBQADggIPADCCAgoCggIBAJ33qNzxFA+JrOqDscZ6+dPsV7GCAvPb
# hBo9jDI14cLJpN9SETe7u3YkVsVK9T31amVEEsKSdDZQeDaeSJqUNsIqOzGwBLux
# k/X8NlZRiQXWpxvO4DSika/dYqVOkfh0tr7JhkgJF9bioPywLlLbWfRzJOCGT7do
# 8j/1LFMTQfDBLA2/tY1Tg0pLAxmcrTdiJhfOVG2fOLABs5CMGqR41YyyDnCNO537
# p6f/KbGA/peBuk01fkCPSsgSqRxoPeOcReQY7vI6O2rjz5x7zEo9ocxn52SsPBZT
# 6pgHqblwpWUb6b7ahzy83um4DC0SMvUf0hsm/0++9t4vQmrxeVcTNdURAbwXDULV
# 9UL2d81qjD6ZPbuLCoJ2nFVaweldvs9YJeT4mhG0OpP+7IovdJu27sqdZ4T8bVWr
# qd7qdub7NJADmjiTzp8xuLmQrAdDR/PhBDQFggz+j88S51ZmyicHcnQkVtEPKi/k
# siHSVzKysfOXODEza9IZp45mrUVj/JTIo+nnSGFjy8SFseXSTmJ+GlRJheRnhQKG
# USscz0MNY31EJxAO8SdPqJaR2z30G8YvJNgTi0pUMx0ebwGtquF/l+yj4F1fz0SD
# mCZ1LikqppNEstixJHwa32fb9+aqb+UGBR/A1hr0oRoJWbAbiTCF1SyMk9bIhCfP
# X2JgLDDN/YzNAgMBAAGjggHLMIIBxzAdBgNVHQ4EFgQUQ9sKXZlO8H4eVu395aho
# yH9a57kwHwYDVR0jBBgwFoAUa2koOjUvSGNAz3vYr0npPtk92yEwbAYDVR0fBGUw
# YzBhoF+gXYZbaHR0cDovL3d3dy5taWNyb3NvZnQuY29tL3BraW9wcy9jcmwvTWlj
# cm9zb2Z0JTIwUHVibGljJTIwUlNBJTIwVGltZXN0YW1waW5nJTIwQ0ElMjAyMDIw
# LmNybDB5BggrBgEFBQcBAQRtMGswaQYIKwYBBQUHMAKGXWh0dHA6Ly93d3cubWlj
# cm9zb2Z0LmNvbS9wa2lvcHMvY2VydHMvTWljcm9zb2Z0JTIwUHVibGljJTIwUlNB
# JTIwVGltZXN0YW1waW5nJTIwQ0ElMjAyMDIwLmNydDAMBgNVHRMBAf8EAjAAMBYG
# A1UdJQEB/wQMMAoGCCsGAQUFBwMIMA4GA1UdDwEB/wQEAwIHgDBmBgNVHSAEXzBd
# MFEGDCsGAQQBgjdMg30BATBBMD8GCCsGAQUFBwIBFjNodHRwOi8vd3d3Lm1pY3Jv
# c29mdC5jb20vcGtpb3BzL0RvY3MvUmVwb3NpdG9yeS5odG0wCAYGZ4EMAQQCMA0G
# CSqGSIb3DQEBDAUAA4ICAQBCyETwKcMu124k4eFobUOoCnyXigtf6RyVDV/gjL73
# YFfBXoO6OKWgOJv6QrNPrzcQfy0kC60s42YCa4aUWgE04toodo2lHJ9dimsX2vgY
# 1RzMrHyGIxmPRoSJWmUdZvrD2pLYnqaX/KyM/5V/Plse6M4GhRGJ1NO+kerQmxd1
# cTufwcm1K1xzCDKHDbUGe3kLR3MBNsIKLaBs5s9LlNoCYi+51r5eH2rQlHLTlgQm
# Dh/dK073VDjdPlbQAaWrP5oV7q0MqQVVmyEz4q3XB4hKgHtinFBzAnmswbFL4R3y
# LENCsR3iDwA6WCZFUTTMQQooIQ6IrbBy0F++P+HCJPMSj6OgkOXmNQFCLlmOg4l0
# OBZo1ToPoCBDIng8jkIrpJjE3THOmCt5GB/128sZElCs7Ohq/B5JAWYlN7Mi5Htw
# mnXrynaP4RV0j2bi1lr1eYY6N2nfMeD4N6FqLh/bz/sJq6w81Smv6Zx0WEZAonPp
# SSgjHPuCBXPMPYOUgpKFPp+JwsYMAjuvKj8mnLNThsZVat+MH8MHQiCzEe3LTOb6
# Anz+q+xuu1+D1H11uQycKOmR3ndc66EY+IyGsWeS/SDZ7kvXFWpXcK/jWCJuZ5Ob
# EACcwae7ulG78hm7Vl2Dzbcdib7/ETWE08z6mhWidmM5mmDrbOQGw78kR9rzYcmT
# SjGCB0YwggdCAgEBMHgwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZXN0YW1waW5nIENBIDIwMjACEzMAAABdxjl4T0B9M/0AAAAAAF0wDQYJYIZIAWUD
# BAIBBQCgggSfMBEGCyqGSIb3DQEJEAIPMQIFADAaBgkqhkiG9w0BCQMxDQYLKoZI
# hvcNAQkQAQQwHAYJKoZIhvcNAQkFMQ8XDTI2MTExODIxMzEwNVowLwYJKoZIhvcN
# AQkEMSIEIPC9TWZ+HG6RQ7boDJwXjPOXUyJdM4f3mb6CPsHLwKAPMIG5BgsqhkiG
# 9w0BCRACLzGBqTCBpjCBozCBoAQgLTVkS2/p8+5CYqFlp0aiuYaFAENeb9zL6xe9
# 3MQCSl0wfDBlpGMwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29mdCBD
# b3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGltZXN0
# YW1waW5nIENBIDIwMjACEzMAAABdxjl4T0B9M/0AAAAAAF0wggNhBgsqhkiG9w0B
# CRACEjGCA1AwggNMoYIDSDCCA0QwggIsAgEBMIIBCaGB4aSB3jCB2zELMAkGA1UE
# BhMCVVMxEzARBgNVBAgTCldhc2hpbmd0b24xEDAOBgNVBAcTB1JlZG1vbmQxHjAc
# BgNVBAoTFU1pY3Jvc29mdCBDb3Jwb3JhdGlvbjElMCMGA1UECxMcTWljcm9zb2Z0
# IEFtZXJpY2EgT3BlcmF0aW9uczEnMCUGA1UECxMeblNoaWVsZCBUU1MgRVNOOjdB
# MDAtMDVFMC1EOTQ3MTUwMwYDVQQDEyxNaWNyb3NvZnQgUHVibGljIFJTQSBUaW1l
# IFN0YW1waW5nIEF1dGhvcml0eaIjCgEBMAcGBSsOAwIaAxUAvae6tbWxgM8HaMBs
# DxHEmsyWkPWgZzBlpGMwYTELMAkGA1UEBhMCVVMxHjAcBgNVBAoTFU1pY3Jvc29m
# dCBDb3Jwb3JhdGlvbjEyMDAGA1UEAxMpTWljcm9zb2Z0IFB1YmxpYyBSU0EgVGlt
# ZXN0YW1waW5nIENBIDIwMjAwDQYJKoZIhvcNAQELBQACBQDuqF15MCIYDzIwMjYx
# MTE4MTcyMzA1WhgPMjAyNjExMTkxNzIzMDVaMHcwPQYKKwYBBAGEWQoEATEvMC0w
# CgIFAO6oXXkCAQAwCgIBAAICAZ0CAf8wBwIBAAICEoIwCgIFAO6prvkCAQAwNgYK
# KwYBBAGEWQoEAjEoMCYwDAYKKwYBBAGEWQoDAqAKMAgCAQACAwehIKEKMAgCAQAC
# AwGGoDANBgkqhkiG9w0BAQsFAAOCAQEAmzLJyhp138JKpdDMDCczP/h36d6fR++5
# DQ7kSg1qlhJc12KQqYfC3XH33zFMYp9h7FrXNG52/mbHKXOEbD6UUODHER7NT05b
# +jh3v14hzjI4Es/ToBVLlnW04zZrpatYZMZmZ5jneFXLZwtetJ4Gcyjg9N0Ytju9
# bZpPFSsfuKYJLqcDiRCNVKw0GHcOHR3r5UmzxNxEUt9wJr+0BYTJRNOXFdh6p/8T
# J1ayGlziMg7JQ99wo4J7rMLhZGAntRvQ6KI3HuCShs4aztGffjZDjSiezsxIqZO9
# F+oN0CphJWlfY1uooHby1wt6818OH8PcO1x0Dn+xNdupgg+SrhVr+jANBgkqhkiG
# 9w0BAQEFAASCAgAipmZJS7vjxbxnG1IU+EqBcDHynKqoxRBqYOx6iI9ltglQD9CP
# ojz7ndGY7E4z0t6PGGZ9l51KxzY7iIyb4UCzjQo+t5+VuPNkPyWuzBV3RfbkIi1B
# 9aULcQSBUEXP0LW5tk+xi9/tup+LUsrzFLeWC+Q5fCk+ZD09Azb9kbNjuaJ45azL
# UoAE138hjvkbo6XtIq9P1XGrIcKeNvbVTVUyYpPweaUFC5gehPc2HpsbwASKZjfP
# 4as/baue19bfY1u46RxvAEw0DptfiuHqntDe+HIGBsZ1ZFTAugjbbTAqaY/rx6+Q
# jaegs7GRgf82bJwZ7HmpmKo7hdgcxpHuJ9SAkUpRCLMM8ZvOYLiVWaNAFepRWvaG
# ssJs1CwVGZ0zFmQEUkMHOIGs8W7Ke2Zhvs1cRBnmK4bni1RurzMskLo9mXmbU+dT
# HoWqlnaCTcb2PjCtkNtUaNauz5PArItn9XDVN+/y4JZ/FDGQXNrCyd1pQtl7nZJr
# ui5xh4FLd7tXV4KXY3wsrY+x0cdAA8w5LIimj0Op6vQM+9M82lmWeGsrpRkY5/iZ
# LNrYX+c0/Ea5jU33Zx396Ix2SA7v7XlTwvlTreJ5TlV9EDCxeduhatE9SDMAnHSW
# iLFd/BUA4CS5QQkD6Z1aPxcJKp6/KeDOyNmYdXOKeo3FiWX/dg1XUwiDaQ==
# SIG # End signature block
