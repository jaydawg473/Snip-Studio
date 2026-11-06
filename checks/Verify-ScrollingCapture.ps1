# Synthetic signed-library verification only. No application/control windows,
# input injection, desktop capture, clipboard, preferences, registry or devices.
# Run: powershell.exe -STA -NoProfile -File checks\Verify-ScrollingCapture.ps1
param([string]$ReleaseFolder='release-0.5.0-signed', [string]$ExpectedVersion='0.5.0.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core'){throw 'Use Windows PowerShell 5 for this .NET Framework application.'}
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*' -or $null -eq $signature.TimeStamperCertificate){throw 'A valid timestamped Automation Forge LLC application signature is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Run this check in a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$flags=[Reflection.BindingFlags]'Public,NonPublic,Instance'
$sessionType=$assembly.GetType('SnipStudio.ScrollingStitchSession',$true)
$ctor=$sessionType.GetConstructor($flags,$null,[type[]]@([Drawing.Bitmap]),$null)
$appendMethod=$sessionType.GetMethod('Append',$flags,$null,[type[]]@([Drawing.Bitmap]),$null)
$buildMethod=$sessionType.GetMethod('BuildImage',$flags)
if($null -eq $ctor -or $null -eq $appendMethod -or $null -eq $buildMethod){throw 'The scrolling stitch session API was not found.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
$observations=New-Object 'System.Collections.Generic.List[object]'
$output=Join-Path $PSScriptRoot ($ReleaseFolder.Replace('-signed','-data')+'-scrolling')
[IO.Directory]::CreateDirectory($output)|Out-Null
function Confirm-Scroll([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Read-Value($Object,[string]$Name){return $Object.GetType().GetProperty($Name,$flags).GetValue($Object,$null)}
function New-Session([Drawing.Bitmap]$Frame){return $ctor.Invoke([object[]]@($Frame))}
function Append-Frame($Session,[Drawing.Bitmap]$Frame){
    $r=$appendMethod.Invoke($Session,[object[]]@($Frame))
    $observations.Add([ordered]@{Status=[string](Read-Value $r 'Status');AddedHeight=Read-Value $r 'AddedHeight';Offset=Read-Value $r 'Offset';Confidence=Read-Value $r 'Confidence';Message=Read-Value $r 'Message'})
    return $r
}
function Build-Image($Session){return $buildMethod.Invoke($Session,[object[]]@())}
function Get-Pixels([Drawing.Bitmap]$Bitmap){
    $rect=[Drawing.Rectangle]::new(0,0,$Bitmap.Width,$Bitmap.Height)
    $bits=$Bitmap.LockBits($rect,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $pixels=New-Object byte[] ($Bitmap.Width*$Bitmap.Height*4)
        for($y=0;$y -lt $Bitmap.Height;$y++){
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($bits.Scan0,$y*$bits.Stride),$pixels,$y*$Bitmap.Width*4,$Bitmap.Width*4)
        }
        return ,$pixels
    }finally{$Bitmap.UnlockBits($bits)}
}
function Pixel-Equal([Drawing.Bitmap]$Left,[Drawing.Bitmap]$Right){
    if($Left.Width -ne $Right.Width -or $Left.Height -ne $Right.Height){return $false}
    $a=Get-Pixels $Left;$b=Get-Pixels $Right
    return [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($a,$b)
}
function Pixel-Hash([Drawing.Bitmap]$Bitmap){
    $sha=[Security.Cryptography.SHA256]::Create()
    try{return [BitConverter]::ToString($sha.ComputeHash((Get-Pixels $Bitmap))).Replace('-','')}finally{$sha.Dispose()}
}
function New-Pattern([int]$Width,[int]$Height,[int]$Seed=473){
    $bitmap=[Drawing.Bitmap]::new($Width,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96,96)
    $pixels=New-Object byte[] ($Width*$Height*4)
    $random=[Random]::new($Seed);$random.NextBytes($pixels)
    for($i=3;$i -lt $pixels.Length;$i+=4){$pixels[$i]=255}
    $bits=$bitmap.LockBits([Drawing.Rectangle]::new(0,0,$Width,$Height),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{[Runtime.InteropServices.Marshal]::Copy($pixels,0,$bits.Scan0,$pixels.Length)}finally{$bitmap.UnlockBits($bits)}
    $g=[Drawing.Graphics]::FromImage($bitmap)
    $font=[Drawing.Font]::new('Consolas',11,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
    $pen=[Drawing.Pen]::new([Drawing.Color]::DarkBlue,2)
    try{
        $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::None
        for($y=8;$y -lt $Height-20;$y+=37){
            $g.FillRectangle([Drawing.Brushes]::White,8,$y,[Math]::Max(12,$Width-24),22)
            $g.DrawString(('ROW {0:D5}  PART-00123' -f $y),$font,[Drawing.Brushes]::Black,10,$y+2)
            $x=[Math]::Max(18,$Width-40)
            $g.DrawLine($pen,$x,$y+6,$x+20,$y+6)
            $g.DrawLine($pen,$x+20,$y+6,$x+14,$y+2)
            $g.DrawLine($pen,$x+20,$y+6,$x+14,$y+10)
            $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,($y*7)%256,($y*13)%256,($y*19)%256))
            try{$g.FillRectangle($brush,$Width-18,$y+12,10,9)}finally{$brush.Dispose()}
        }
    }finally{$g.Dispose();$font.Dispose();$pen.Dispose()}
    return $bitmap
}
function Crop-Frame([Drawing.Bitmap]$Document,[int]$Offset,[int]$Height){
    $frame=$Document.Clone([Drawing.Rectangle]::new(0,$Offset,$Document.Width,$Height),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $frame.SetResolution(96,96)
    return $frame
}
function New-StickyFrame([Drawing.Bitmap]$Body,[int]$Offset,[int]$Height=256,[int]$Header=24,[int]$Footer=20){
    $frame=[Drawing.Bitmap]::new($Body.Width,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $frame.SetResolution(96,96)
    $g=[Drawing.Graphics]::FromImage($frame)
    $font=[Drawing.Font]::new('Consolas',12,[Drawing.FontStyle]::Bold,[Drawing.GraphicsUnit]::Pixel)
    try{
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.FillRectangle([Drawing.Brushes]::MidnightBlue,0,0,$Body.Width,$Header)
        $g.FillRectangle([Drawing.Brushes]::DarkGreen,0,$Height-$Footer,$Body.Width,$Footer)
        $g.DrawString('FIXED HEADER 00123',$font,[Drawing.Brushes]::White,8,4)
        $g.DrawString('FIXED FOOTER',$font,[Drawing.Brushes]::White,8,$Height-$Footer+2)
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($Body,[Drawing.Rectangle]::new(0,$Header,$Body.Width,$Height-$Header-$Footer),[Drawing.Rectangle]::new(0,$Offset,$Body.Width,$Height-$Header-$Footer),[Drawing.GraphicsUnit]::Pixel)
    }finally{$g.Dispose();$font.Dispose()}
    return $frame
}
function Save-Proof([Drawing.Bitmap]$Image,[string]$Name){$Image.Save((Join-Path $output $Name),[Drawing.Imaging.ImageFormat]::Png)}

# Variable overlap includes a nine-pixel end-like movement. Exact comparisons
# include text, arrow rasterization, color cells and row-unique texture.
$document=New-Pattern 320 1031
$session=$null;$first=$null
try{
    $first=Crop-Frame $document 0 256;$inputHash=Pixel-Hash $first
    $session=New-Session $first
    Confirm-Scroll ((Read-Value $session 'Width') -eq 320 -and (Read-Value $session 'Height') -eq 256 -and (Read-Value $session 'FrameCount') -eq 1) 'Initial dimensions and frame count equal the native first viewport.'
    Confirm-Scroll ((Pixel-Hash $first) -ceq $inputHash) 'Constructing the session leaves the source bitmap unchanged.'
    $first.Dispose();$first=$null
    $previous=0
    foreach($offset in @(130,286,295,455,615,775)){
        $frame=Crop-Frame $document $offset 256
        try{
            $inputHash=Pixel-Hash $frame;$r=Append-Frame $session $frame
            Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'Accepted') "Variable-overlap frame at row $offset is accepted."
            Confirm-Scroll ((Read-Value $r 'Offset') -eq ($offset-$previous) -and (Read-Value $r 'AddedHeight') -eq ($offset-$previous)) "Frame at row $offset retains its exact pixel offset."
            Confirm-Scroll ((Pixel-Hash $frame) -ceq $inputHash) "Appending row $offset leaves the input pixels unchanged."
        }finally{$frame.Dispose()}
        $previous=$offset
    }
    $stitched=Build-Image $session
    try{
        Confirm-Scroll (Pixel-Equal $stitched $document) 'Variable-overlap output is pixel-for-pixel equal to the full source document.'
        Confirm-Scroll ([Math]::Abs($stitched.HorizontalResolution-96) -lt .1 -and [Math]::Abs($stitched.VerticalResolution-96) -lt .1) 'Output retains 96-DPI native raster dimensions for 100-percent viewing.'
        Save-Proof $stitched 'variable-overlap-actual.png';Save-Proof $document 'variable-overlap-expected.png'
    }finally{$stitched.Dispose()}
    $again=Build-Image $session
    try{Confirm-Scroll (Pixel-Equal $again $document) 'Disposing a returned image and every input leaves the session independently usable.'}finally{$again.Dispose()}
    $frame=Crop-Frame $document 775 256
    try{$r=Append-Frame $session $frame;Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'Unchanged' -and (Read-Value $r 'AddedHeight') -eq 0) 'An identical end frame adds no duplicate pixels.'}finally{$frame.Dispose()}
    $beforeCount=Read-Value $session 'FrameCount';$beforeHeight=Read-Value $session 'Height'
    foreach($size in @(@(321,256),@(320,255))){
        $bad=New-Pattern $size[0] $size[1] 915
        try{
            $rejected=$false
            try{$r=Append-Frame $session $bad;$rejected=([string](Read-Value $r 'Status')) -ne 'Accepted'}catch{$rejected=$true}
            Confirm-Scroll $rejected ('Changed geometry '+$size[0]+'x'+$size[1]+' is rejected.')
        }finally{$bad.Dispose()}
        Confirm-Scroll ((Read-Value $session 'FrameCount') -eq $beforeCount -and (Read-Value $session 'Height') -eq $beforeHeight) 'Geometry rejection preserves accepted dimensions and frame count.'
    }
    $unrelated=New-Pattern 320 256 925
    try{$r=Append-Frame $session $unrelated;Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'NoReliableOverlap') 'Unrelated content halts with no reliable overlap.'}finally{$unrelated.Dispose()}
    $kept=Build-Image $session
    try{Confirm-Scroll (Pixel-Equal $kept $document) 'A failed overlap preserves every accepted output pixel.'}finally{$kept.Dispose()}
    Confirm-Scroll ((Read-Value $session 'FrameCount') -eq 7 -and (Read-Value $session 'Height') -eq 1031) 'End, invalid geometry and no-overlap outcomes do not extend session state.'
}finally{if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};$document.Dispose()}
$disposedRejected=$false
try{$unused=Build-Image $session;if($null -ne $unused){$unused.Dispose()}}catch{$disposedRejected=$true}
Confirm-Scroll $disposedRejected 'Building after session disposal is rejected.'

# Fixed chrome must occur once each, with every scrolling content row retained.
$body=New-Pattern 320 837 1289
$session=$null;$first=$null;$expected=$null
try{
    $first=New-StickyFrame $body 0;$session=New-Session $first
    $previous=0
    foreach($offset in @(110,250,259,405,545,625)){
        $frame=New-StickyFrame $body $offset
        try{
            $r=Append-Frame $session $frame
            Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'Accepted' -and (Read-Value $r 'AddedHeight') -eq ($offset-$previous)) "Sticky-chrome frame at row $offset adds only new content."
        }finally{$frame.Dispose()}
        $previous=$offset
    }
    $expected=[Drawing.Bitmap]::new(320,881,[Drawing.Imaging.PixelFormat]::Format32bppArgb);$expected.SetResolution(96,96)
    $g=[Drawing.Graphics]::FromImage($expected)
    try{
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($first,[Drawing.Rectangle]::new(0,0,320,24),[Drawing.Rectangle]::new(0,0,320,24),[Drawing.GraphicsUnit]::Pixel)
        $g.DrawImageUnscaled($body,0,24)
        $g.DrawImage($first,[Drawing.Rectangle]::new(0,861,320,20),[Drawing.Rectangle]::new(0,236,320,20),[Drawing.GraphicsUnit]::Pixel)
    }finally{$g.Dispose()}
    $actual=Build-Image $session
    try{
        Confirm-Scroll (Pixel-Equal $actual $expected) 'Sticky header and footer appear once around all pixel-exact scrolling content.'
        Save-Proof $actual 'sticky-chrome-actual.png';Save-Proof $expected 'sticky-chrome-expected.png';Save-Proof $first 'sticky-first-viewport.png'
    }finally{$actual.Dispose()}
}finally{if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};if($null -ne $expected){$expected.Dispose()};$body.Dispose()}

foreach($kind in @('flat','repeated-stripes')){
    $a=[Drawing.Bitmap]::new(320,256);$b=[Drawing.Bitmap]::new(320,256)
    $session=$null
    try{
        foreach($item in @(@($a,0),@($b,3))){
            $g=[Drawing.Graphics]::FromImage($item[0])
            try{
                $g.Clear([Drawing.Color]::White)
                if($kind -eq 'flat'){$g.Clear($(if($item[1] -eq 0){[Drawing.Color]::White}else{[Drawing.Color]::Gainsboro}))}
                else{for($y=-16+[int]$item[1];$y -lt 256;$y+=16){$g.FillRectangle([Drawing.Brushes]::DarkBlue,0,$y,320,8)}}
            }finally{$g.Dispose()}
        }
        $session=New-Session $a;$r=Append-Frame $session $b
        Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'NoReliableOverlap' -and (Read-Value $r 'AddedHeight') -eq 0) "Ambiguous $kind cannot produce a confident wrong offset."
        $kept=Build-Image $session
        try{Confirm-Scroll (Pixel-Equal $kept $a) "Ambiguous $kind preserves the original pixels."}finally{$kept.Dispose()}
    }finally{if($null -ne $session){$session.Dispose()};$a.Dispose();$b.Dispose()}
}

# Exercise the frame bound with small native images, never a huge allocation.
$validateSize=$sessionType.GetMethod('ValidateInitialSize',[Reflection.BindingFlags]'NonPublic,Static')
if($null -eq $validateSize){throw 'The allocation-free initial size guard was not found.'}
foreach($size in @(@(31,256),@(320,63),@(16385,64),@(32,30001),@(8001,8000))){
    $rejected=$false
    try{$validateSize.Invoke($null,[object[]]@([int]$size[0],[int]$size[1]))|Out-Null}catch{$rejected=$true}
    Confirm-Scroll $rejected ('Initial size '+$size[0]+'x'+$size[1]+' is rejected before image allocation.')
}
$validateSize.Invoke($null,[object[]]@(8000,8000))|Out-Null
Confirm-Scroll $true 'An image exactly at the 64,000,000-pixel boundary passes allocation-free size validation.'
$maximumFrames=[int]$sessionType.GetField('MaximumFrames',[Reflection.BindingFlags]'Public,NonPublic,Static').GetRawConstantValue()
Confirm-Scroll ($maximumFrames -ge 2 -and $maximumFrames -le 200) 'The session exposes a bounded frame limit suitable for deterministic verification.'
$limitBody=New-Pattern 128 (128+42*$maximumFrames) 8201
$session=$null;$first=$null
try{
    $first=Crop-Frame $limitBody 0 128;$session=New-Session $first
    for($i=1;$i -lt $maximumFrames;$i++){
        $frame=Crop-Frame $limitBody ($i*42) 128
        try{$r=Append-Frame $session $frame;Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'Accepted' -and (Read-Value $r 'Offset') -eq 42) "Bound fixture accepts frame $($i+1) at its exact offset within the configured limit."}finally{$frame.Dispose()}
    }
    $before=Build-Image $session
    try{
        $frame=Crop-Frame $limitBody ($maximumFrames*42) 128
        try{$r=Append-Frame $session $frame;Confirm-Scroll (([string](Read-Value $r 'Status')) -ceq 'Limit' -and (Read-Value $r 'AddedHeight') -eq 0) 'The next frame is rejected at the configured frame limit.'}finally{$frame.Dispose()}
        $after=Build-Image $session
        try{Confirm-Scroll (Pixel-Equal $before $after) 'Limit rejection preserves the complete accepted image.'}finally{$after.Dispose()}
        Confirm-Scroll ((Read-Value $session 'FrameCount') -eq $maximumFrames) 'Limit rejection does not increment the accepted frame count.'
    }finally{$before.Dispose()}
}finally{if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};$limitBody.Dispose()}

[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;VerifiedUtc=[DateTime]::UtcNow.ToString('o');
    SignatureStatus=[string]$signature.Status;TimestampSigner=$signature.TimeStamperCertificate.Subject;CheckCount=$checks.Count;
    NoWindowsCreated=$true;NoInputInjection=$true;NoDesktopCapture=$true;NoClipboardAccess=$true;NoPreferencesAccess=$true;NoDeviceAccess=$true;
    Unverified='Native window selection, actual application scrolling, physical input, multi-monitor/DPI capture, cancellation UI and perceived performance.';
    Checks=$checks.ToArray();AppendObservations=$observations.ToArray();Proofs=@('variable-overlap-actual.png','variable-overlap-expected.png','sticky-chrome-actual.png','sticky-chrome-expected.png','sticky-first-viewport.png')
}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'scrolling-results.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed-library synthetic scrolling checks. Native desktop capture remains unverified.')
