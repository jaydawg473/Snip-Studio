# Regression rendering from an already signed application only. No compiler, application
# entrypoint, shown form, desktop capture, physical input, or preferences access.
param(
    [string]$ReleaseFolder='release-0.6.3-signed',
    [string]$ExpectedVersion='0.6.3.0',
    [string]$ExpectedSha256=''
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use -STA.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Invalid release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'Valid timestamped Automation Forge application required.'}
$sha=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
if($ExpectedSha256 -ne '' -and $sha -cne $ExpectedSha256){throw 'Unexpected executable hash.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -cne $ExpectedVersion){throw 'Unexpected version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$art=$assembly.GetType('SnipStudio.EmojiArtwork',$true)
$documentType=$assembly.GetType('SnipStudio.EditorDocument',$true)
$pickerType=$assembly.GetType('SnipStudio.EmojiPickerPopup',$true)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$catalog=$art.GetProperty('Catalog',$static).GetValue($null,$null)
$thumbMethod=$art.GetMethod('CreateThumbnail',$static)
$drawMethod=$art.GetMethod('Draw',$static)
$output=Join-Path $PSScriptRoot ($ReleaseFolder.Replace('-signed','-data')+'\emoji-rendering')
[IO.Directory]::CreateDirectory($output)|Out-Null
$records=New-Object 'Collections.Generic.List[object]'
$transformRecords=New-Object 'Collections.Generic.List[object]'
$negativeControls=New-Object 'Collections.Generic.List[object]'
$checks=New-Object 'Collections.Generic.List[string]'
$failures=New-Object 'Collections.Generic.List[string]'
function Confirm-Emoji([bool]$Condition,[string]$Label){if($Condition){$checks.Add($Label)}else{$failures.Add($Label);Write-Warning ('FAILED: '+$Label)}}
function Read-Pixels([Drawing.Bitmap]$Image){
    $data=$Image.LockBits([Drawing.Rectangle]::new(0,0,$Image.Width,$Image.Height),[Drawing.Imaging.ImageLockMode]::ReadOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{
        $bytes=New-Object byte[] ($Image.Width*$Image.Height*4)
        for($y=0;$y -lt $Image.Height;$y++){[Runtime.InteropServices.Marshal]::Copy([IntPtr]::Add($data.Scan0,$y*$data.Stride),$bytes,$y*$Image.Width*4,$Image.Width*4)}
        return ,$bytes
    }finally{$Image.UnlockBits($data)}
}
function Alpha-Bounds([Drawing.Bitmap]$Image){
    $bytes=Read-Pixels $Image;$left=$Image.Width;$right=-1;$top=$Image.Height;$bottom=-1;$count=0;$sum=0L;$edgeCount=0;$edgeSum=0L
    for($y=0;$y -lt $Image.Height;$y++){for($x=0;$x -lt $Image.Width;$x++){
        $a=$bytes[($y*$Image.Width+$x)*4+3];if($a -eq 0){continue}
        $count++;$sum+=$a;$left=[Math]::Min($left,$x);$right=[Math]::Max($right,$x);$top=[Math]::Min($top,$y);$bottom=[Math]::Max($bottom,$y)
        if($x -eq 0 -or $y -eq 0 -or $x -eq $Image.Width-1 -or $y -eq $Image.Height-1){$edgeCount++;$edgeSum+=$a}
    }}
    [pscustomobject]@{X=$left;Y=$top;Width=$right-$left+1;Height=$bottom-$top+1;VisiblePixels=$count;AlphaSum=$sum;EdgePixels=$edgeCount;EdgeAlphaSum=$edgeSum}
}
function New-Transparent([int]$Width,[int]$Height){return [Drawing.Bitmap]::new($Width,$Height,[Drawing.Imaging.PixelFormat]::Format32bppArgb)}
function New-OraclePixelImage([Drawing.Bitmap]$Source,$Bounds){
    # Transfer decoded BGRA samples directly, independently of the production
    # GDI copy. Neutral DPI prevents the reference draw from repeating the bug.
    $pixels=Read-Pixels $Source;$image=New-Transparent $Bounds.Width $Bounds.Height
    $data=$image.LockBits([Drawing.Rectangle]::new(0,0,$image.Width,$image.Height),[Drawing.Imaging.ImageLockMode]::WriteOnly,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try{for($row=0;$row -lt $image.Height;$row++){[Runtime.InteropServices.Marshal]::Copy($pixels,(($row+$Bounds.Y)*$Source.Width+$Bounds.X)*4,[IntPtr]::Add($data.Scan0,$row*$data.Stride),$image.Width*4)}}finally{$image.UnlockBits($data)}
    return $image
}
function Draw-SourceOracle([Drawing.Graphics]$Graphics,[Drawing.Bitmap]$Source,$Bounds,[Drawing.RectangleF]$Destination){
    # Independently copy every original visible sample first, then draw that
    # complete bitmap: no source subrectangle, artwork API or cached crop used.
    $fit=[single][Math]::Min($Destination.Width/$Bounds.Width,$Destination.Height/$Bounds.Height)
    $width=[single]($Bounds.Width*$fit);$height=[single]($Bounds.Height*$fit)
    $x=[single]($Destination.X+($Destination.Width-$width)/2)
    $y=[single]($Destination.Y+($Destination.Height-$height)/2)
    $Graphics.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $Graphics.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $Graphics.CompositingQuality=[Drawing.Drawing2D.CompositingQuality]::HighQuality
    $Graphics.DrawImage($Source,[Drawing.RectangleF]::new($x,$y,$width,$height),[Drawing.RectangleF]::new(0,0,$Source.Width,$Source.Height),[Drawing.GraphicsUnit]::Pixel)
}
function Compare-Bytes([Drawing.Bitmap]$Actual,[Drawing.Bitmap]$Oracle,[int]$OffsetX=0,[int]$OffsetY=0){
    $a=Read-Pixels $Actual;$o=Read-Pixels $Oracle;$different=0;$alphaDifference=0L;$channelDifference=0L
    $alphaDeficit=0L;$alphaSurplus=0L;$maxDeficit=0;$substantialDeficit=0;$oracleAlpha=0L;$maxCompositedDifference=0.0
    for($y=0;$y -lt $Actual.Height;$y++){for($x=0;$x -lt $Actual.Width;$x++){
        $ai=($y*$Actual.Width+$x)*4;$oi=(($y+$OffsetY)*$Oracle.Width+$x+$OffsetX)*4;$changed=$false
        $aa=[int]$a[$ai+3];$oa=[int]$o[$oi+3];$oracleAlpha+=$oa;$deficit=$oa-$aa
        if($aa -eq 0 -and $oa -eq 0){continue}
        if($deficit -gt 0){$alphaDeficit+=$deficit;$maxDeficit=[Math]::Max($maxDeficit,$deficit);if($deficit -ge 16){$substantialDeficit++}}else{$alphaSurplus-=$deficit}
        for($c=0;$c -lt 4;$c++){
            $delta=[Math]::Abs([int]$a[$ai+$c]-[int]$o[$oi+$c]);$channelDifference+=$delta;if($delta -gt 0){$changed=$true};if($c -eq 3){$alphaDifference+=$delta}
            else{
                # Compare visible colors over both black and white. RGB values
                # belonging to fully transparent pixels do not count as damage.
                $premultiplied=($a[$ai+$c]*$aa-$o[$oi+$c]*$oa)/255.0
                $maxCompositedDifference=[Math]::Max($maxCompositedDifference,[Math]::Max([Math]::Abs($premultiplied),[Math]::Abs($premultiplied+$oa-$aa)))
            }
        }
        if($changed){$different++}
    }}
    [pscustomobject]@{DifferentPixels=$different;AbsoluteAlphaDifference=$alphaDifference;AbsoluteChannelDifference=$channelDifference;AlphaDeficit=$alphaDeficit;AlphaSurplus=$alphaSurplus;MaxAlphaDeficit=$maxDeficit;PixelsWithAlphaDeficitAtLeast16=$substantialDeficit;OracleAlphaSum=$oracleAlpha;NormalizedAlphaDifference=($alphaDifference/[double][Math]::Max(1,$oracleAlpha));MaxCompositedChannelDifference=$maxCompositedDifference}
}
$sheet=New-Transparent 2040 736
$g=[Drawing.Graphics]::FromImage($sheet)
$font=[Drawing.Font]::new('Segoe UI',9)
try{
    $g.Clear([Drawing.Color]::FromArgb(235,235,235));$index=0
    foreach($definition in $catalog){
        $key=$definition.Key;$resource='SnipStudio.Emoji.'+$key+'.png';$stream=$assembly.GetManifestResourceStream($resource)
        try{$source=[Drawing.Bitmap]::new($stream)}catch{$stream.Dispose();throw}
        $thumb=$null;$oracle=$null;$base=$null;$model=$null;$render=$null;$oracleSource=$null
        try{
            $bounds=Alpha-Bounds $source
            $oracleSource=New-OraclePixelImage $source $bounds
            $thumb=$thumbMethod.Invoke($null,[object[]]@($key,[int]32))
            $entries=$art.GetField('entries',$static).GetValue($null);$entry=$entries[$key]
            $cached=$entry.GetType().GetField('Image',$instance).GetValue($entry)
            $cachedBounds=$entry.GetType().GetField('SourceBounds',$instance).GetValue($entry)
            $cacheComparison=Compare-Bytes $cached $source
            Confirm-Emoji ($cacheComparison.AbsoluteAlphaDifference -eq 0 -and $cacheComparison.MaxCompositedChannelDifference -le 1.01) ($key+' decode preserves every original alpha pixel and visible color within GDI rounding')
            Confirm-Emoji ($cachedBounds.X -eq $bounds.X -and $cachedBounds.Y -eq $bounds.Y -and $cachedBounds.Width -eq $bounds.Width -and $cachedBounds.Height -eq $bounds.Height) ($key+' retains original complete artwork bounds')
            $aspect=[single]($art.GetMethod('GetAspectRatio',$static).Invoke($null,[object[]]@($key)))
            Confirm-Emoji ([Math]::Abs($aspect-$bounds.Width/[double]$bounds.Height) -le .000001) ($key+' retains the original natural aspect ratio')
            $oracle=New-Transparent ($thumb.Width+16) ($thumb.Height+16)
            $og=[Drawing.Graphics]::FromImage($oracle)
            try{$og.Clear([Drawing.Color]::Transparent);Draw-SourceOracle $og $oracleSource $bounds ([Drawing.RectangleF]::new(8,8,$thumb.Width,$thumb.Height))}finally{$og.Dispose()}
            $oraclePixels=Read-Pixels $oracle;$outsidePixels=0;$outsideAlpha=0L
            for($y=0;$y -lt $oracle.Height;$y++){for($x=0;$x -lt $oracle.Width;$x++){
                if($x -ge 8 -and $x -lt 8+$thumb.Width -and $y -ge 8 -and $y -lt 8+$thumb.Height){continue}
                $a=$oraclePixels[($y*$oracle.Width+$x)*4+3];if($a -gt 0){$outsidePixels++;$outsideAlpha+=$a}
            }}
            $aspect=$bounds.Width/[single]$bounds.Height;$w=[single]96;$h=[single]96;if($aspect -gt 1){$h=96/$aspect}else{$w=96*$aspect}
            $base=New-Transparent 112 112;$model=[Activator]::CreateInstance($documentType);$model.SetImage($base)
            $null=$model.AddEmoji($key,[Drawing.RectangleF]::new((112-$w)/2,(112-$h)/2,$w,$h),[single]0)
            $render=$model.RenderImage()
            $docOracle=New-Transparent 112 112;$dg=[Drawing.Graphics]::FromImage($docOracle)
            try{$dg.Clear([Drawing.Color]::Transparent);Draw-SourceOracle $dg $oracleSource $bounds ([Drawing.RectangleF]::new((112-$w)/2,(112-$h)/2,$w,$h))}finally{$dg.Dispose()}
            try{$documentComparison=Compare-Bytes $render $docOracle}finally{$docOracle.Dispose()}
            $thumbnailComparison=Compare-Bytes $thumb $oracle 8 8
            if($thumbnailComparison.NormalizedAlphaDifference -ge .01 -or $thumbnailComparison.MaxCompositedChannelDifference -gt 3.01){Write-Output ('Thumbnail reference mismatch '+$key+': '+($thumbnailComparison|ConvertTo-Json -Compress))}
            Confirm-Emoji ($thumbnailComparison.NormalizedAlphaDifference -lt .01 -and $thumbnailComparison.MaxCompositedChannelDifference -le 3.01) ($key+' thumbnail matches the complete original-PNG reference within raster rounding')
            Confirm-Emoji ($documentComparison.NormalizedAlphaDifference -lt .01 -and $documentComparison.MaxCompositedChannelDifference -le 3.01) ($key+' placed sticker matches the complete original-PNG reference within raster rounding')
            if($key -eq 'happy'){
                $legacy=New-Transparent $source.Width $source.Height
                $ng=[Drawing.Graphics]::FromImage($legacy)
                try{$ng.CompositingMode=[Drawing.Drawing2D.CompositingMode]::SourceCopy;$ng.DrawImageUnscaled($source,0,0)}finally{$ng.Dispose()}
                try{
                    $legacyComparison=Compare-Bytes $legacy $source
                    Confirm-Emoji ($legacyComparison.MaxAlphaDeficit -ge 128 -and $legacyComparison.NormalizedAlphaDifference -gt .10) 'The original DPI-sensitive decode is rejected as a negative control'
                    $negativeControls.Add([pscustomobject]@{Kind='Original DrawImageUnscaled decode';Comparison=$legacyComparison})
                }finally{$legacy.Dispose()}
                $clipped=New-Transparent $thumb.Width $thumb.Height
                $ng=[Drawing.Graphics]::FromImage($clipped)
                try{$ng.SetClip([Drawing.Rectangle]::new(0,0,[int]($thumb.Width/2),$thumb.Height));$ng.DrawImage($thumb,[Drawing.Rectangle]::new(0,0,$thumb.Width,$thumb.Height),0,0,$thumb.Width,$thumb.Height,[Drawing.GraphicsUnit]::Pixel)}finally{$ng.Dispose()}
                try{
                    $clippedComparison=Compare-Bytes $clipped $thumb
                    Confirm-Emoji ($clippedComparison.MaxAlphaDeficit -ge 128 -and $clippedComparison.NormalizedAlphaDifference -gt .25) 'The visible-pixel oracle rejects deliberately clipped artwork'
                    $negativeControls.Add([pscustomobject]@{Kind='Deliberately clipped right half';Comparison=$clippedComparison})
                }finally{$clipped.Dispose()}
            }
            if($key -in @('happy','thumbs-up','cross','important','celebrate','pin')){
                $transformBase=New-Transparent 200 200;$transformModel=[Activator]::CreateInstance($documentType)
                try{
                    $transformModel.SetImage($transformBase);$first=$true
                    foreach($case in @(@{Width=[single]92;Height=[single]48;Angle=[single]37;Zoom=[single]1},@{Width=$w;Height=$h;Angle=[single]37;Zoom=[single]0.625},@{Width=$w;Height=$h;Angle=[single]-123;Zoom=[single]1.25},@{Width=$w;Height=$h;Angle=[single]90;Zoom=[single]2})){
                        $frame=[Drawing.RectangleF]::new(100-$case.Width/2,100-$case.Height/2,$case.Width,$case.Height)
                        if($first){$null=$transformModel.AddEmoji($key,$frame,$case.Angle);$first=$false}else{$null=$transformModel.UpdateEmojiAnnotationBounds(0,$frame,$case.Angle)}
                        $transformed=New-Transparent 256 256;$transformedOracle=New-Transparent 256 256
                        try{
                            $tg=[Drawing.Graphics]::FromImage($transformed)
                            try{
                                $tg.TranslateTransform(128,128);$tg.ScaleTransform($case.Zoom,$case.Zoom);$tg.TranslateTransform(-100,-100)
                                $null=$documentType.GetMethod('Draw',$instance,$null,[type[]]@([Drawing.Graphics]),$null).Invoke($transformModel,[object[]]@($tg))
                            }finally{$tg.Dispose()}
                            $tg=[Drawing.Graphics]::FromImage($transformedOracle)
                            try{
                                $tg.TranslateTransform(128,128);$tg.ScaleTransform($case.Zoom,$case.Zoom);$tg.TranslateTransform(-100,-100)
                                $tg.TranslateTransform(100,100);$tg.RotateTransform($case.Angle)
                                Draw-SourceOracle $tg $oracleSource $bounds ([Drawing.RectangleF]::new(-$case.Width/2,-$case.Height/2,$case.Width,$case.Height))
                            }finally{$tg.Dispose()}
                            $comparison=Compare-Bytes $transformed $transformedOracle
                            # Independent original samples avoid the cache's GDI
                            # premultiply/unpremultiply roundtrip. Bound accumulated
                            # copy/filter quantization explicitly; core-pixel loss
                            # (255 in both negative controls) cannot pass this gate.
                            Confirm-Emoji ($comparison.NormalizedAlphaDifference -lt .001 -and $comparison.MaxAlphaDeficit -le 2 -and $comparison.MaxCompositedChannelDifference -le 4.01) ($key+' preserves complete artwork at rotation '+$case.Angle+', viewer scale '+$case.Zoom+' and frame '+$case.Width+'x'+$case.Height)
                            $transformRecords.Add([pscustomobject]@{Key=$key;Rotation=$case.Angle;ViewerScale=$case.Zoom;FrameWidth=$case.Width;FrameHeight=$case.Height;Comparison=$comparison})
                        }finally{$transformedOracle.Dispose();$transformed.Dispose()}
                    }
                }finally{$transformModel.Dispose();$transformBase.Dispose()}
            }
            $row=[int][Math]::Floor($index/6);$col=$index%6;$cx=$col*340;$cy=$row*184
            $g.DrawString($key,$font,[Drawing.Brushes]::Black,$cx+4,$cy+3)
            $g.DrawString('Original PNG | signed thumbnail | placed sticker',$font,[Drawing.Brushes]::Black,$cx+4,$cy+23)
            # Full original (not crop) at 112: useful for visually detecting source clipping.
            $g.DrawImage($source,[Drawing.Rectangle]::new($cx+2,$cy+48,112,112),0,0,256,256,[Drawing.GraphicsUnit]::Pixel)
            $g.InterpolationMode=[Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
            $g.PixelOffsetMode=[Drawing.Drawing2D.PixelOffsetMode]::Half
            $g.DrawImage($thumb,[Drawing.Rectangle]::new($cx+116+[int]((96-$thumb.Width*3)/2),$cy+56+[int]((96-$thumb.Height*3)/2),$thumb.Width*3,$thumb.Height*3),0,0,$thumb.Width,$thumb.Height,[Drawing.GraphicsUnit]::Pixel)
            $g.DrawImageUnscaled($render,$cx+225,$cy+48)
            $records.Add([pscustomobject]@{Key=$key;SourceAlphaBounds=$bounds;CacheVsOriginal=$cacheComparison;ThumbnailWidth=$thumb.Width;ThumbnailHeight=$thumb.Height;ThumbnailAlpha=Alpha-Bounds $thumb;ThumbnailVsFullSourceOracle=$thumbnailComparison;OracleAlphaOutsideThumbnailPixels=$outsidePixels;OracleAlphaOutsideThumbnailSum=$outsideAlpha;PlacedStickerVsFullSourceOracle=$documentComparison})
            $index++
        }finally{if($null -ne $render){$render.Dispose()};if($null -ne $model){$model.Dispose()};if($null -ne $base){$base.Dispose()};if($null -ne $oracle){$oracle.Dispose()};if($null -ne $thumb){$thumb.Dispose()};if($null -ne $oracleSource){$oracleSource.Dispose()};$source.Dispose();$stream.Dispose()}
    }
    $sheet.Save((Join-Path $output 'source-thumbnail-document.png'),[Drawing.Imaging.ImageFormat]::Png)
}finally{$font.Dispose();$g.Dispose();$sheet.Dispose()}
$popupRecords=New-Object 'Collections.Generic.List[object]'
foreach($scale in @([single]0.75,[single]1,[single]1.25,[single]1.5,[single]1.75,[single]2,[single]3,[single]4)){
    $popup=$pickerType.GetConstructor($instance,$null,[type[]]@([Action[string]],[single]),$null).Invoke([object[]]@([Action[string]]{param($key)},$scale))
    try{
        $content=$pickerType.GetField('content',$instance).GetValue($popup)
        # The popup stays unshown. Detach its existing panel before DrawToBitmap,
        # since a hidden ToolStrip ancestor suppresses child rendering.
        $content.Parent=$null;$content.Visible=$true
        $buttons=@($content.Controls|Where-Object {$_.GetType().Name -eq 'StickerButton'})
        $panelImage=New-Transparent $content.Width $content.Height
        try{$content.DrawToBitmap($panelImage,[Drawing.Rectangle]::new(0,0,$panelImage.Width,$panelImage.Height));$panelImage.Save((Join-Path $output ('picker-scale-'+$scale.ToString('0.##',[Globalization.CultureInfo]::InvariantCulture)+'.png')),[Drawing.Imaging.ImageFormat]::Png)}finally{$panelImage.Dispose()}
        foreach($button in $buttons){
            $thumbnail=$button.GetType().GetField('thumbnail',$instance).GetValue($button)
            $actual=New-Transparent $button.ClientSize.Width $button.ClientSize.Height
            $expected=New-Transparent $button.ClientSize.Width $button.ClientSize.Height
            try{
                $button.DrawToBitmap($actual,[Drawing.Rectangle]::new(0,0,$actual.Width,$actual.Height))
                $eg=[Drawing.Graphics]::FromImage($expected)
                try{
                    $eg.Clear($content.BackColor)
                    $eg.DrawImage($thumbnail,[Drawing.Rectangle]::new([int][Math]::Truncate(($expected.Width-$thumbnail.Width)/2.0),[int][Math]::Truncate(($expected.Height-$thumbnail.Height)/2.0),$thumbnail.Width,$thumbnail.Height),0,0,$thumbnail.Width,$thumbnail.Height,[Drawing.GraphicsUnit]::Pixel)
                }finally{$eg.Dispose()}
                $buttonComparison=Compare-Bytes $actual $expected
                # Rounded-button background antialiasing differs by one RGB level
                # from a flat clear; alpha and every artwork pixel remain bounded.
                Confirm-Emoji ($thumbnail.Width -le $actual.Width -and $thumbnail.Height -le $actual.Height -and $buttonComparison.AbsoluteAlphaDifference -eq 0 -and $buttonComparison.MaxCompositedChannelDifference -le 1.01) ($button.AccessibleName+' native button paints the entire thumbnail at scale '+$scale)
                $popupRecords.Add([pscustomobject]@{Scale=$scale;Name=$button.AccessibleName;ClientWidth=$button.ClientSize.Width;ClientHeight=$button.ClientSize.Height;ThumbnailWidth=$thumbnail.Width;ThumbnailHeight=$thumbnail.Height;NativeButtonVsCompleteThumbnail=$buttonComparison})
            }finally{$expected.Dispose();$actual.Dispose()}
        }
        $content.Dispose()
    }finally{$popup.Dispose()}
}
if([Windows.Forms.Application]::OpenForms.Count -ne 0){throw 'No form may be shown.'}
[ordered]@{Application=$app;SHA256=$sha;Version=[Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion;CheckCount=$checks.Count;Checks=$checks.ToArray();FailureCount=$failures.Count;Failures=$failures.ToArray();EntryPointExecuted=$false;FormShown=$false;InputSent=$false;DesktopCaptured=$false;PreferencesAccessed=$false;Oracle='Every original visible pixel independently scanned and raw-copied from embedded PNG into a neutral-DPI bitmap; entire oracle bitmap drawn without a source subrectangle or artwork API. Visible-color comparison over both black and white. Roomy document canvases; intentional image-boundary crop excluded.';Artwork=$records.ToArray();Transforms=$transformRecords.ToArray();NegativeControls=$negativeControls.ToArray();Picker=$popupRecords.ToArray();Unverified='Shown gallery, physical gestures, mixed-monitor DPI transitions and production installation.'}|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $output 'render-diagnostics.json') -Encoding UTF8
if($failures.Count -gt 0){throw ($failures.Count.ToString()+' emoji rendering checks failed; see render-diagnostics.json.')}
Write-Output ('PASS: '+$checks.Count+' signed emoji decode, thumbnail, document and native-picker rendering checks. '+$output)

