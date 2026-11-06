# Signed-library synthetic overlap checks; no window, capture, input, entrypoint,
# preference access, clipboard or compiled helper is used.
param([string]$ReleaseFolder='release-0.5.3-signed',[string]$ExpectedVersion='0.5.3.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent;$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge application is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app);$type=$assembly.GetType('SnipStudio.ScrollingStitchSession',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic';$ctor=$type.GetConstructor($flags,$null,[type[]]@([Drawing.Bitmap]),$null)
$append=$type.GetMethod('Append',$flags);$build=$type.GetMethod('BuildImage',$flags)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Animation([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Read-Animation($Value,[string]$Name){return $Value.GetType().GetProperty($Name,$flags).GetValue($Value,$null)}
function Same-Animation([Drawing.Bitmap]$A,[Drawing.Bitmap]$B){
    if($A.Size -ne $B.Size){return $false}
    $rectangle=[Drawing.Rectangle]::new(0,0,$A.Width,$A.Height)
    $left=$A.LockBits($rectangle,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $right=$null
    try{
        $right=$B.LockBits($rectangle,[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $aRow=New-Object byte[] ($A.Width*4);$bRow=New-Object byte[] ($A.Width*4)
        for($y=0;$y -lt $A.Height;$y++){
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($left.Scan0,$y*$left.Stride),$aRow,0,$aRow.Length)
            [Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($right.Scan0,$y*$right.Stride),$bRow,0,$bRow.Length)
            if(-not [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals($aRow,$bRow)){return $false}
        }
        return $true
    }finally{if($null -ne $right){$B.UnlockBits($right)};$A.UnlockBits($left)}
}
$source=[Drawing.Bitmap]::new(320,700,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g=[Drawing.Graphics]::FromImage($source)
try{
    $g.Clear([Drawing.Color]::White)
    for($y=0;$y -lt 700;$y++){
        $color=[Drawing.Color]::FromArgb(255,32+($y*53)%192,32+($y*97)%192,32+($y*131)%192)
        $brush=[Drawing.SolidBrush]::new($color)
        try{$g.FillRectangle($brush,4,$y,24,1)}finally{$brush.Dispose()}
        if($y%24 -lt 8){$g.FillRectangle([Drawing.Brushes]::MidnightBlue,48,$y,110+($y%5)*24,1)}
    }
}finally{$g.Dispose()}
$first=$null;$next=$null;$expected=$null;$session=$null
$output=Join-Path $PSScriptRoot ($ReleaseFolder.Replace('-signed','-data')+'-animation')
[IO.Directory]::CreateDirectory($output)|Out-Null
try{
    $first=$source.Clone([Drawing.Rectangle]::new(0,0,320,400),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $next=$source.Clone([Drawing.Rectangle]::new(0,80,320,400),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($first);try{$g.FillRectangle([Drawing.Brushes]::Crimson,0,160,320,32)}finally{$g.Dispose()}
    $g=[Drawing.Graphics]::FromImage($next);try{$g.FillRectangle([Drawing.Brushes]::RoyalBlue,0,80,320,32)}finally{$g.Dispose()}
    $session=$ctor.Invoke([object[]]@($first));$r=$append.Invoke($session,[object[]]@($next))
    Confirm-Animation ((Read-Animation $r 'Status').ToString() -eq 'Accepted') 'A 10-percent animated overlap block can use distributed unchanged anchors'
    Confirm-Animation ((Read-Animation $r 'Offset') -eq 80 -and (Read-Animation $r 'AddedHeight') -eq 80) 'Localized animation retains the exact independently known scroll offset'
    Confirm-Animation ((Read-Animation $r 'Confidence') -le 0.85) 'Localized-animation fallback does not report full strict-match confidence'
    $expected=[Drawing.Bitmap]::new(320,480,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($expected)
    try{
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImageUnscaled($first,0,0)
        $g.DrawImage($next,[Drawing.Rectangle]::new(0,400,320,80),[Drawing.Rectangle]::new(0,320,320,80),[Drawing.GraphicsUnit]::Pixel)
    }finally{$g.Dispose()}
    $actual=$build.Invoke($session,[object[]]@())
    try{
        Confirm-Animation (Same-Animation $actual $expected) 'Animated-area output uses only exact captured pixels with no guessed rows'
        $actual.Save((Join-Path $output 'localized-animation-actual.png'),[Drawing.Imaging.ImageFormat]::Png)
    }finally{$actual.Dispose()}
}finally{if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};if($null -ne $next){$next.Dispose()};if($null -ne $expected){$expected.Dispose()}}
try{
    foreach($reflowStart in @(280,300,315)){
    foreach($shift in @(-8,8)){
        $first=$source.Clone([Drawing.Rectangle]::new(0,0,320,400),[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $next=[Drawing.Bitmap]::new(320,400,[Drawing.Imaging.PixelFormat]::Format32bppArgb);$session=$null
        $g=[Drawing.Graphics]::FromImage($next)
        try{
            $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.DrawImage($source,[Drawing.Rectangle]::new(0,0,320,$reflowStart),[Drawing.Rectangle]::new(0,80,320,$reflowStart),[Drawing.GraphicsUnit]::Pixel)
            # The last part reflows by a different offset. Most overlap still
            # agrees, but there is no single translation through the end.
            $g.DrawImage($source,[Drawing.Rectangle]::new(0,$reflowStart,320,400-$reflowStart),[Drawing.Rectangle]::new(0,80+$reflowStart+$shift,320,400-$reflowStart),[Drawing.GraphicsUnit]::Pixel)
        }finally{$g.Dispose()}
        try{
            $session=$ctor.Invoke([object[]]@($first));$r=$append.Invoke($session,[object[]]@($next))
            Confirm-Animation ((Read-Animation $r 'Status').ToString() -eq 'NoReliableOverlap') ('Piecewise reflow by '+$shift+' pixels at row '+$reflowStart+' cannot be mislabeled animation')
            $kept=$build.Invoke($session,[object[]]@())
            try{Confirm-Animation (Same-Animation $kept $first) ('Rejected reflow '+$shift+' at row '+$reflowStart+' preserves every accepted pixel')}finally{$kept.Dispose()}
        }finally{if($null -ne $session){$session.Dispose()};$first.Dispose();$next.Dispose()}
    }
    }
    # The final footer row animates, preventing a stable-footer inference.
    # Allowing outliers at the append boundary would silently omit body rows.
    $first=[Drawing.Bitmap]::new(320,400,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $next=[Drawing.Bitmap]::new(320,400,[Drawing.Imaging.PixelFormat]::Format32bppArgb);$session=$null
    foreach($item in @(@($first,0,[Drawing.Brushes]::Crimson),@($next,50,[Drawing.Brushes]::RoyalBlue))){
        $g=[Drawing.Graphics]::FromImage($item[0])
        try{
            $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
            $g.DrawImage($source,[Drawing.Rectangle]::new(0,0,320,360),[Drawing.Rectangle]::new(0,[int]$item[1],320,360),[Drawing.GraphicsUnit]::Pixel)
            $g.FillRectangle([Drawing.Brushes]::LightSteelBlue,0,360,320,40)
            $g.FillRectangle([Drawing.Brushes]::MidnightBlue,30,370,230,8)
            $g.FillRectangle($item[2],0,399,320,1)
        }finally{$g.Dispose()}
    }
    try{
        $session=$ctor.Invoke([object[]]@($first));$r=$append.Invoke($session,[object[]]@($next))
        Confirm-Animation ((Read-Animation $r 'Status').ToString() -eq 'NoReliableOverlap') 'An undetected animated fixed footer cannot be stitched through as body pixels'
        Confirm-Animation ((Read-Animation $session 'Height') -eq 400 -and (Read-Animation $session 'FrameCount') -eq 1) 'Animated-footer rejection cannot skip body rows or extend history'
        $kept=$build.Invoke($session,[object[]]@())
        try{Confirm-Animation (Same-Animation $kept $first) 'Animated-footer rejection preserves every accepted pixel'}finally{$kept.Dispose()}
    }finally{if($null -ne $session){$session.Dispose()};$first.Dispose();$next.Dispose()}
    $first=[Drawing.Bitmap]::new(320,400,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $next=[Drawing.Bitmap]::new(320,400,[Drawing.Imaging.PixelFormat]::Format32bppArgb);$session=$null
    foreach($item in @(@($first,0),@($next,3))){
        $g=[Drawing.Graphics]::FromImage($item[0])
        try{$g.Clear([Drawing.Color]::White);for($y=-16+[int]$item[1];$y -lt 400;$y+=16){$g.FillRectangle([Drawing.Brushes]::Navy,0,$y,320,8)}}finally{$g.Dispose()}
    }
    try{
        $session=$ctor.Invoke([object[]]@($first));$r=$append.Invoke($session,[object[]]@($next))
        Confirm-Animation ((Read-Animation $r 'Status').ToString() -eq 'NoReliableOverlap') 'Repeated text-like rows still require a unique offset and remain rejected'
        $kept=$build.Invoke($session,[object[]]@())
        try{Confirm-Animation (Same-Animation $kept $first) 'Ambiguous repeated rows preserve the accepted image'}finally{$kept.Dispose()}
    }finally{if($null -ne $session){$session.Dispose()};$first.Dispose();$next.Dispose()}
}finally{$source.Dispose()}
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Scope='Synthetic signed-library localized animation, exact-pixel output, piecewise reflow and repeated-row rejection. No live capture or native input.'}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'scrolling-animation-results.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' localized-animation/reflow scrolling checks; native application capture remains unverified.')
