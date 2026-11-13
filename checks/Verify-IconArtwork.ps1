# Signed-library artwork and unshown button-paint checks. Never runs the app
# entrypoint, shows a form, accesses preferences, captures the desktop or sends input.
param([string]$ReleaseFolder='release-0.6.1-signed',[string]$ExpectedVersion='0.6.1.0')
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
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$icons=$assembly.GetType('SnipStudio.IconArtwork',$true)
$buttonType=$assembly.GetType('SnipStudio.StudioButton',$true)
$createTool=$icons.GetMethod('CreateToolIcon',$static)
$createApp=$icons.GetMethod('CreateAppIconBitmap',$static)
$getToolSvg=$icons.GetMethod('GetToolSvg',$static)
$getAppSvg=$icons.GetMethod('GetAppSvg',$static)
$paintButton=$buttonType.GetMethod('OnPaint',$instance)
$checks=New-Object 'System.Collections.Generic.List[string]'
$rasters=New-Object 'System.Collections.Generic.List[object]'
$paints=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Artwork([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-Artwork([Reflection.MethodInfo]$Method,$Target,[object[]]$Arguments){
    for($i=0;$i -lt $Arguments.Length;$i++){if($null -ne $Arguments[$i]){$Arguments[$i]=$Arguments[$i].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    return $Method.Invoke($Target,$Arguments)
}
function Read-ArtworkPixels([Drawing.Bitmap]$Bitmap){
    $width=$Bitmap.Width;$height=$Bitmap.Height
    $data=$Bitmap.LockBits([Drawing.Rectangle]::new(0,0,$width,$height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($width*$height*4)
        for($row=0;$row -lt $height;$row++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$row*$data.Stride),$bytes,$row*$width*4,$width*4)}
        return [pscustomobject]@{Width=$width;Height=$height;Bytes=$bytes}
    }finally{$Bitmap.UnlockBits($data)}
}
function Test-ArtworkTight($Pixels){
    $top=$false;$bottom=$false;$left=$false;$right=$false
    for($x=0;$x -lt $Pixels.Width;$x++){
        if($Pixels.Bytes[$x*4+3] -gt 0){$top=$true}
        if($Pixels.Bytes[(($Pixels.Height-1)*$Pixels.Width+$x)*4+3] -gt 0){$bottom=$true}
    }
    for($y=0;$y -lt $Pixels.Height;$y++){
        if($Pixels.Bytes[($y*$Pixels.Width)*4+3] -gt 0){$left=$true}
        if($Pixels.Bytes[($y*$Pixels.Width+$Pixels.Width-1)*4+3] -gt 0){$right=$true}
    }
    return $top -and $bottom -and $left -and $right
}
function Test-ArtworkColor($Pixels,[Drawing.Color]$Color){
    $count=0;$red=0L;$green=0L;$blue=0L
    for($offset=0;$offset -lt $Pixels.Bytes.Length;$offset+=4){
        if($Pixels.Bytes[$offset+3] -ge 128){$count++;$blue+=$Pixels.Bytes[$offset];$green+=$Pixels.Bytes[$offset+1];$red+=$Pixels.Bytes[$offset+2]}
    }
    $total=$red+$green+$blue;$requestedTotal=[int]$Color.R+[int]$Color.G+[int]$Color.B
    if($count -eq 0 -or $total -le 0 -or $requestedTotal -le 0){return $false}
    # GDI+ bicubic resizing can shift intensity even in the preceding renderer.
    # Compare channel proportions separately from a bounded luminance change.
    $redError=[Math]::Abs($red/[double]$total-$Color.R/[double]$requestedTotal)
    $greenError=[Math]::Abs($green/[double]$total-$Color.G/[double]$requestedTotal)
    $blueError=[Math]::Abs($blue/[double]$total-$Color.B/[double]$requestedTotal)
    $requestedLuminance=.2126*$Color.R+.7152*$Color.G+.0722*$Color.B
    $luminance=(.2126*$red+.7152*$green+.0722*$blue)/[double]$count
    $gain=$luminance/$requestedLuminance
    return $redError -le .02 -and $greenError -le .02 -and $blueError -le .02 -and $gain -ge .8 -and $gain -le 1.2
}
function Read-ArtworkSvg([string]$Svg,[string]$Label){
    $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null
    $text=[IO.StringReader]::new($Svg);$reader=$null
    try{
        $reader=[Xml.XmlReader]::Create($text,$settings)
        $xml=[Xml.XmlDocument]::new();$xml.XmlResolver=$null;$xml.Load($reader)
    }finally{if($null -ne $reader){$reader.Dispose()};$text.Dispose()}
    $root=$xml.DocumentElement
    Confirm-Artwork ($root.LocalName -ceq 'svg' -and $root.NamespaceURI -ceq 'http://www.w3.org/2000/svg') ($Label+' is a valid SVG document')
    $parts=$root.GetAttribute('viewBox').Trim() -split '[\s,]+'
    $numbers=New-Object 'System.Collections.Generic.List[double]'
    foreach($part in $parts){
        $value=0.0
        if(-not [double]::TryParse($part,[Globalization.NumberStyles]::Float,[Globalization.CultureInfo]::InvariantCulture,[ref]$value) -or [double]::IsNaN($value) -or [double]::IsInfinity($value)){throw ('FAILED: '+$Label+' has a non-finite viewBox')}
        $numbers.Add($value)
    }
    Confirm-Artwork ($numbers.Count -eq 4 -and $numbers[2] -gt 0 -and $numbers[3] -gt 0) ($Label+' has a finite positive viewBox')
    $geometry=$xml.SelectNodes('//*[local-name()="path" or local-name()="rect" or local-name()="circle" or local-name()="ellipse" or local-name()="line" or local-name()="polyline" or local-name()="polygon"]')
    Confirm-Artwork ($geometry.Count -gt 0) ($Label+' contains vector geometry')
    return $xml
}
function Paint-ArtworkButton($Button,[single]$Dpi){
    $bitmap=[Drawing.Bitmap]::new($Button.Width,$Button.Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics=$null;$paint=$null
    try{
        $bitmap.SetResolution($Dpi,$Dpi)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        $paint=[Windows.Forms.PaintEventArgs]::new($graphics,[Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height))
        $null=Invoke-Artwork $paintButton $Button ([object[]]@($paint))
        $script:lastArtworkPaint=[ordered]@{DpiX=$graphics.DpiX;DpiY=$graphics.DpiY;PageUnit=[string]$graphics.PageUnit;PageScale=$graphics.PageScale}
        return $bitmap
    }catch{$bitmap.Dispose();throw}
    finally{if($null -ne $paint){$paint.Dispose()};if($null -ne $graphics){$graphics.Dispose()}}
}
function Measure-ArtworkDifference($First,$Second){
    if($First.Width -ne $Second.Width -or $First.Height -ne $Second.Height){throw 'Paint comparison dimensions differ.'}
    $minX=$First.Width;$minY=$First.Height;$maxX=-1;$maxY=-1
    for($y=0;$y -lt $First.Height;$y++){
        for($x=0;$x -lt $First.Width;$x++){
            $offset=($y*$First.Width+$x)*4
            if($First.Bytes[$offset] -ne $Second.Bytes[$offset] -or $First.Bytes[$offset+1] -ne $Second.Bytes[$offset+1] -or $First.Bytes[$offset+2] -ne $Second.Bytes[$offset+2] -or $First.Bytes[$offset+3] -ne $Second.Bytes[$offset+3]){
                $minX=[Math]::Min($minX,$x);$minY=[Math]::Min($minY,$y);$maxX=[Math]::Max($maxX,$x);$maxY=[Math]::Max($maxY,$y)
            }
        }
    }
    return [Drawing.Rectangle]::FromLTRB($minX,$minY,$maxX+1,$maxY+1)
}

$keys=[string[]]$icons.GetField('Keys',$static).GetValue($null)
$expectedKeys=@('new','rectangle','freeform','window','fullscreen','scrolling','crop','pen','highlighter','arrow','box','text','eraser','remove-area','color','move','pan','open','paste','copy','save','share','undo','redo','zoom-in','zoom-out','fit','help','text-scan','print','record','stop','microphone','volume','settings','more','camera','clock','video','shapes','ellipse','line','emoji')
Confirm-Artwork ($keys.Count -eq 43 -and (@($keys|Sort-Object -Unique).Count -eq $keys.Count) -and (($keys|Sort-Object)-join ',') -ceq (($expectedKeys|Sort-Object)-join ',')) 'The complete 43-key catalog contains every existing and new shape/emoji symbol once'
$ink=[Drawing.Color]::FromArgb(255,29,83,147)
$positiveInk=[pscustomobject]@{Bytes=[byte[]]@(147,83,29,255)}
Confirm-Artwork (Test-ArtworkColor $positiveInk $ink) 'The ink assertion accepts the exact requested color'
foreach($wrongInk in @([byte[]]@(0,0,0,255),[byte[]]@(255,255,255,255),[byte[]]@(29,83,147,255),[byte[]]@(221,125,44,255),[byte[]]@(74,42,15,255))){
    Confirm-Artwork (-not (Test-ArtworkColor ([pscustomobject]@{Bytes=$wrongInk}) $ink)) 'The ink assertion rejects black, white, reversed hue and excessive or insufficient brightness controls'
}
$svgByKey=@{}
foreach($key in $keys){
    foreach($size in @(18,20,24,32,64)){
        $bitmap=Invoke-Artwork $createTool $null ([object[]]@($key,$size,$ink))
        try{
            $pixels=Read-ArtworkPixels $bitmap
            Confirm-Artwork ($bitmap.Width -ge 1 -and $bitmap.Height -ge 1 -and $bitmap.Width -le $size -and $bitmap.Height -le $size -and (Test-ArtworkTight $pixels)) ($key+' '+$size+'px has visible ink at all four bitmap edges without transparent outer padding')
            if($size -eq 24){Confirm-Artwork (Test-ArtworkColor $pixels $ink) ($key+' preserves the requested toolbar ink proportions and bounded brightness')}
            if($key -ceq 'more'){Confirm-Artwork ($bitmap.Width -gt $bitmap.Height*3) ('More '+$size+'px preserves its wide, short proportions')}
            $rasters.Add([ordered]@{Key=$key;Requested=$size;Width=$bitmap.Width;Height=$bitmap.Height})
        }finally{$bitmap.Dispose()}
    }
    $svgByKey[$key]=[string](Invoke-Artwork $getToolSvg $null ([object[]]@($key)))
    $xml=Read-ArtworkSvg $svgByKey[$key] $key
    Confirm-Artwork ($xml.DocumentElement.GetAttribute('stroke') -ceq 'currentColor') ($key+' SVG remains themeable')
}
foreach($pair in @(@{Alias='highlight';Key='highlighter'},@{Alias='plus';Key='zoom-in'},@{Alias='minus';Key='zoom-out'})){
    Confirm-Artwork ([string](Invoke-Artwork $getToolSvg $null ([object[]]@($pair.Alias))) -ceq $svgByKey[$pair.Key]) ($pair.Alias+' keeps its existing canonical icon alias')
}
foreach($badKey in @($null,'','unknown-artwork-check')){
    foreach($method in @($createTool,$getToolSvg)){
        $rejected=$false
        try{if($method -eq $createTool){$unexpected=Invoke-Artwork $method $null ([object[]]@($badKey,24,$ink));if($null -ne $unexpected){$unexpected.Dispose()}}else{$null=Invoke-Artwork $method $null ([object[]]@($badKey))}}
        catch{$rejected=$_.Exception.GetBaseException() -is [ArgumentException]}
        Confirm-Artwork $rejected 'Unknown or empty icon keys fail explicitly instead of returning blank artwork'
    }
}
foreach($size in @(16,24,32,48,64,128,256,512)){
    $bitmap=Invoke-Artwork $createApp $null ([object[]]@($size))
    try{
        $pixels=Read-ArtworkPixels $bitmap
        Confirm-Artwork ($bitmap.Width -eq $size -and $bitmap.Height -eq $size -and (Test-ArtworkTight $pixels)) ('App '+$size+'px remains square with visible artwork touching every outer edge')
        Confirm-Artwork ($pixels.Bytes[3] -eq 0 -and $pixels.Bytes[($size-1)*4+3] -eq 0 -and $pixels.Bytes[(($size-1)*$size)*4+3] -eq 0 -and $pixels.Bytes[($size*$size-1)*4+3] -eq 0) ('App '+$size+'px keeps all four rounded corners transparent')
        $rasters.Add([ordered]@{Key='app';Requested=$size;Width=$bitmap.Width;Height=$bitmap.Height})
    }finally{$bitmap.Dispose()}
}
$null=Read-ArtworkSvg ([string](Invoke-Artwork $getAppSvg $null ([object[]]@()))) 'App artwork'
foreach($method in @($createTool,$createApp)){
    $bitmap=if($method -eq $createTool){Invoke-Artwork $method $null ([object[]]@('more',1,$ink))}else{Invoke-Artwork $method $null ([object[]]@(1))}
    try{Confirm-Artwork ($bitmap.Width -eq 1 -and $bitmap.Height -eq 1 -and (Test-ArtworkTight (Read-ArtworkPixels $bitmap))) 'The smallest supported artwork size remains a visible one-pixel bitmap'}finally{$bitmap.Dispose()}
}
$bitmap=Invoke-Artwork $createTool $null ([object[]]@('more',24,[Drawing.Color]::FromArgb(0,29,83,147)))
try{
    $pixels=Read-ArtworkPixels $bitmap
    Confirm-Artwork ($bitmap.Width -eq 1 -and $bitmap.Height -eq 1 -and $pixels.Bytes[3] -eq 0) 'Fully transparent requested ink remains explicitly transparent rather than adding a background'
}finally{$bitmap.Dispose()}
foreach($size in @(0,2049)){
    foreach($method in @($createTool,$createApp)){
        $rejected=$false
        try{if($method -eq $createTool){$unexpected=Invoke-Artwork $method $null ([object[]]@('more',$size,$ink))}else{$unexpected=Invoke-Artwork $method $null ([object[]]@($size))};if($null -ne $unexpected){$unexpected.Dispose()}}
        catch{$rejected=$_.Exception.GetBaseException() -is [ArgumentOutOfRangeException]}
        Confirm-Artwork $rejected 'Artwork size limits reject zero and excessive dimensions'
    }
}

foreach($dpi in @(96,144,192)){
    $scale=$dpi/96.0
    foreach($toolLayout in @($false,$true)){
        foreach($glyph in @('more','highlighter')){
            $button=[Activator]::CreateInstance($buttonType,$true)
            $baseline=$null;$painted=$null
            try{
                $button.Size=[Drawing.Size]::new([int](65*$scale),[int](47*$scale));$button.Text=''
                $buttonType.GetField('IconOnly',$instance).SetValue($button,$true)
                $buttonType.GetField('ToolLayout',$instance).SetValue($button,$toolLayout)
                Confirm-Artwork (-not $button.IsHandleCreated) 'An offscreen artwork button starts without a native handle'
                $baseline=Paint-ArtworkButton $button $dpi
                $buttonType.GetProperty('Glyph',$instance).SetValue($button,$glyph,$null)
                $painted=Paint-ArtworkButton $button $dpi
                $difference=Measure-ArtworkDifference (Read-ArtworkPixels $baseline) (Read-ArtworkPixels $painted)
                $iconSize=[int][Math]::Truncate($(if($toolLayout){22}else{18})*$scale)
                $centerX=$difference.Left+($difference.Width-1)/2.0
                $centerY=$difference.Top+($difference.Height-1)/2.0
                $expectedX=($button.Width-1)/2.0
                $expectedY=if($toolLayout){[Math]::Truncate(10*$scale)+($iconSize-1)/2.0}else{($button.Height-1)/2.0}
                $cached=$buttonType.GetField('glyphImage',$instance).GetValue($button)
                $detail=' [button='+$button.Width+'x'+$button.Height+'; diff='+$difference.X+','+$difference.Y+','+$difference.Width+','+$difference.Height+'; slot='+$iconSize+'; actualCenter='+$centerX+','+$centerY+'; expectedCenter='+$expectedX+','+$expectedY+'; cached='+$cached.Width+'x'+$cached.Height+' @'+$cached.HorizontalResolution+','+$cached.VerticalResolution+' DPI; target='+$painted.HorizontalResolution+','+$painted.VerticalResolution+' DPI; graphics='+$script:lastArtworkPaint.DpiX+','+$script:lastArtworkPaint.DpiY+' DPI '+$script:lastArtworkPaint.PageUnit+' scale='+$script:lastArtworkPaint.PageScale+']'
                Confirm-Artwork ($difference.Width -gt 0 -and $difference.Height -gt 0 -and $difference.Width -le $iconSize -and $difference.Height -le $iconSize) ($glyph+' button paints its natural bounds without stretching at '+$dpi+' DPI / tool='+$toolLayout+$detail)
                Confirm-Artwork ([Math]::Abs($centerX-$expectedX) -le 1 -and [Math]::Abs($centerY-$expectedY) -le 1) ($glyph+' is centered on both axes in its button icon slot at '+$dpi+' DPI / tool='+$toolLayout+$detail)
                if($glyph -ceq 'more'){Confirm-Artwork ($difference.Width -gt $difference.Height*3) ('More stays wide and short in actual button painting at '+$dpi+' DPI / tool='+$toolLayout)}
                Confirm-Artwork (-not $button.IsHandleCreated) 'Offscreen artwork painting leaves the button without a native handle'
                $paints.Add([ordered]@{Glyph=$glyph;Dpi=$dpi;ToolLayout=$toolLayout;ButtonWidth=$button.Width;ButtonHeight=$button.Height;InkBounds=[ordered]@{X=$difference.X;Y=$difference.Y;Width=$difference.Width;Height=$difference.Height}})
            }finally{if($null -ne $painted){$painted.Dispose()};if($null -ne $baseline){$baseline.Dispose()};$button.Dispose()}
        }
    }
}
Confirm-Artwork ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'Artwork checks did not show or register an application form'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Rasters=$rasters.ToArray();ButtonPaints=$paints.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;UserPreferencesReadOrWritten=$false;Scope='Actual signed-library tool and app raster alpha bounds, requested ink proportions within0.02 and mean luminance gain0.8..1.2 (with negative controls), complete SVG catalog with finite positive viewBoxes, and real unshown StudioButton painting at 96/144/192 DPI. In-memory GDI bitmaps and unparented controls only; no app start or helper compilation.';Unverified='Exact raster RGB equality, SVG path geometry bounds in an external SVG renderer, generated ICO/PNG/SVG release payloads, live toolbar appearance, Explorer icon cache and production installation.'}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $output 'icon-artwork.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed icon artwork checks; '+$rasters.Count+' rasters and '+$paints.Count+' actual offscreen button paints.')
