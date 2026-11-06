# Pure signed-library regression. Synthetic chat-like pixels only; no windows,
# capture, input, application entrypoint, clipboard, preferences or helper EXEs.
param(
    [string]$ReleaseFolder='release-0.5.3-signed',
    [string]$ExpectedVersion='0.5.3.0',
    [switch]$ExpectLegacyFailure
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge application is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Run in a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$type=$assembly.GetType('SnipStudio.ScrollingStitchSession',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$ctor=$type.GetConstructor($flags,$null,[type[]]@([Drawing.Bitmap]),$null)
$append=$type.GetMethod('Append',$flags)
$build=$type.GetMethod('BuildImage',$flags)
$checks=New-Object 'System.Collections.Generic.List[string]'
$observations=New-Object 'System.Collections.Generic.List[object]'
$output=Join-Path $PSScriptRoot ($ReleaseFolder.Replace('-signed','-data')+'-tall-chrome')
[IO.Directory]::CreateDirectory($output)|Out-Null
function Confirm-Tall([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Read-Tall($Value,[string]$Name){return $Value.GetType().GetProperty($Name,$flags).GetValue($Value,$null)}
function New-ChatBody([int]$Height){
    $image=[Drawing.Bitmap]::new(400,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($image);$font=[Drawing.Font]::new('Segoe UI',10,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
    try{
        $g.Clear([Drawing.Color]::White)
        for($y=0;$y -lt $Height;$y++){
            # A small deterministic row marker makes precise alignment provable
            # without relying on installed fonts' particular rasterization.
            $brush=[Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255,32+($y*53)%192,32+($y*97)%192,32+($y*131)%192))
            try{$g.FillRectangle($brush,4,$y,24,1)}finally{$brush.Dispose()}
        }
        for($y=6;$y -lt $Height-20;$y+=32){
            $g.FillRectangle([Drawing.Brushes]::AliceBlue,42,$y,346,24)
            $g.DrawString(('Chat message {0:D4}: review this capture.' -f $y),$font,[Drawing.Brushes]::MidnightBlue,[single]49,[single]($y+5))
        }
    }finally{$font.Dispose();$g.Dispose()}
    return $image
}
function New-ChatFrame([Drawing.Bitmap]$Body,[int]$Offset,[int]$Header=24,[int]$Footer=150,[int]$Height=360){
    $frame=[Drawing.Bitmap]::new(400,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($frame);$font=[Drawing.Font]::new('Segoe UI',11,[Drawing.FontStyle]::Regular,[Drawing.GraphicsUnit]::Pixel)
    try{
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.Clear([Drawing.Color]::White)
        $g.FillRectangle([Drawing.Brushes]::LightSteelBlue,0,0,400,$Header)
        $g.DrawImage($Body,[Drawing.Rectangle]::new(0,$Header,400,$Height-$Header-$Footer),[Drawing.Rectangle]::new(0,$Offset,400,$Height-$Header-$Footer),[Drawing.GraphicsUnit]::Pixel)
        $g.FillRectangle([Drawing.Brushes]::WhiteSmoke,0,$Height-$Footer,400,$Footer)
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceOver
        $g.DrawLine([Drawing.Pens]::Gray,0,$Height-$Footer,399,$Height-$Footer)
        $g.DrawString('Chat workspace',$font,[Drawing.Brushes]::Black,[single]12,[single]4)
        $g.DrawString('Message composer - fixed while chat scrolls',$font,[Drawing.Brushes]::DimGray,[single]14,[single]($Height-$Footer+18))
        $g.DrawRectangle([Drawing.Pens]::LightGray,12,$Height-$Footer+45,374,[Math]::Max(8,$Footer-57))
    }finally{$font.Dispose();$g.Dispose()}
    return $frame
}
function Pixels-Tall([Drawing.Bitmap]$Image){
    $bits=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($y=0;$y -lt $Image.Height;$y++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($bits.Scan0,$y*$bits.Stride),$bytes,$y*$Image.Width*4,$Image.Width*4)}
        return ,$bytes
    }finally{$Image.UnlockBits($bits)}
}
function Same-Tall([Drawing.Bitmap]$A,[Drawing.Bitmap]$B){
    if($A.Size -ne $B.Size){return $false}
    return [Collections.StructuralComparisons]::StructuralEqualityComparer.Equals((Pixels-Tall $A),(Pixels-Tall $B))
}
$body=New-ChatBody 600;$first=$null;$session=$null;$expected=$null
try{
    $first=New-ChatFrame $body 0
    $session=$ctor.Invoke([object[]]@($first))
    $frame=New-ChatFrame $body 50
    try{$result=$append.Invoke($session,[object[]]@($frame))}finally{$frame.Dispose()}
    if($ExpectLegacyFailure){
        Confirm-Tall ((Read-Tall $result 'Status').ToString() -eq 'NoReliableOverlap') 'The old signed build rejects the valid tall-composer overlap'
        Confirm-Tall ((Read-Tall $session 'Height') -eq 360 -and (Read-Tall $session 'FrameCount') -eq 1) 'Legacy rejection preserves the first view'
        $kept=$build.Invoke($session,[object[]]@())
        try{Confirm-Tall (Same-Tall $kept $first) 'Legacy rejection keeps original pixels'}finally{$kept.Dispose()}
        [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;ExpectedLegacyFailureReproduced=$true;CheckCount=$checks.Count;Checks=$checks.ToArray();Scope='Synthetic signed-library tall-composer fixture; no live capture.'}|ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $output 'legacy-tall-composer.json') -Encoding UTF8
        Write-Output ('PASS: reproduced the old tall-composer failure in '+$checks.Count+' checks.')
        return
    }
    Confirm-Tall ((Read-Tall $result 'Status').ToString() -eq 'Accepted' -and (Read-Tall $result 'AddedHeight') -eq 50) 'A fixed 150/360-pixel composer no longer rejects a valid 50-pixel scroll'
    Confirm-Tall ((Read-Tall $session 'ScrollableHeight') -eq 186) 'The driver receives the actual moving body height after removing fixed chrome'
    $previous=50
    foreach($offset in @(120,170,300,410,414)){
        $frame=New-ChatFrame $body $offset
        try{
            $r=$append.Invoke($session,[object[]]@($frame))
            Confirm-Tall ((Read-Tall $r 'Status').ToString() -eq 'Accepted') ('Tall-composer frame at '+$offset+' is accepted')
            Confirm-Tall ((Read-Tall $r 'AddedHeight') -eq $offset-$previous) ('Tall-composer frame at '+$offset+' keeps its exact offset')
            Confirm-Tall ((Read-Tall $session 'ScrollableHeight') -eq 186) ('Tall-composer frame at '+$offset+' keeps a conservative body height')
            $observations.Add([ordered]@{Offset=$offset;Added=Read-Tall $r 'AddedHeight';BodyHeight=Read-Tall $session 'ScrollableHeight'})
        }finally{$frame.Dispose()}
        $previous=$offset
    }
    $expected=[Drawing.Bitmap]::new(400,774,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g=[Drawing.Graphics]::FromImage($expected)
    try{
        $g.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy
        $g.DrawImage($first,[Drawing.Rectangle]::new(0,0,400,24),[Drawing.Rectangle]::new(0,0,400,24),[Drawing.GraphicsUnit]::Pixel)
        $g.DrawImageUnscaled($body,0,24)
        $g.DrawImage($first,[Drawing.Rectangle]::new(0,624,400,150),[Drawing.Rectangle]::new(0,210,400,150),[Drawing.GraphicsUnit]::Pixel)
    }finally{$g.Dispose()}
    $actual=$build.Invoke($session,[object[]]@())
    try{
        Confirm-Tall (Same-Tall $actual $expected) 'Output contains every body pixel with one header and one tall composer'
        $actual.Save((Join-Path $output 'tall-composer-actual.png'),[Drawing.Imaging.ImageFormat]::Png)
        $expected.Save((Join-Path $output 'tall-composer-expected.png'),[Drawing.Imaging.ImageFormat]::Png)
    }finally{$actual.Dispose()}
    $last=New-ChatFrame $body 414
    try{$r=$append.Invoke($session,[object[]]@($last));Confirm-Tall ((Read-Tall $r 'Status').ToString() -eq 'Unchanged') 'The final unchanged view adds no duplicate composer/content'}finally{$last.Dispose()}
}finally{if($null -ne $expected){$expected.Dispose()};if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};$body.Dispose()}

# A selection dominated by stationary chrome has too little moving body to
# justify a stitch. Preserve it rather than guessing a displacement.
$body=New-ChatBody 400;$first=$null;$session=$null
try{
    $first=New-ChatFrame $body 0 150 150 360
    $session=$ctor.Invoke([object[]]@($first));$frame=New-ChatFrame $body 20 150 150 360
    try{$r=$append.Invoke($session,[object[]]@($frame))}finally{$frame.Dispose()}
    Confirm-Tall ((Read-Tall $r 'Status').ToString() -eq 'NoReliableOverlap') 'Combined fixed chrome cannot reduce the proven moving body below 64 pixels'
    $kept=$build.Invoke($session,[object[]]@())
    try{Confirm-Tall (Same-Tall $kept $first) 'Rejected chrome-dominated capture preserves every accepted pixel'}finally{$kept.Dispose()}
}finally{if($null -ne $session){$session.Dispose()};if($null -ne $first){$first.Dispose()};$body.Dispose()}
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Observations=$observations.ToArray();Scope='Synthetic signed-library tall fixed-composer overlap and exact pixel/ownership checks; no native capture or user input.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'tall-composer-results.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' tall-composer scrolling checks. Native application scrolling remains unverified.')
