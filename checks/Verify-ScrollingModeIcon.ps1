# Signed-library icon regression only. No forms, controls, desktop capture,
# input injection, clipboard, preference access, entrypoint or helper executable.
param([string]$ReleaseFolder='release-0.5.3-signed',[string]$ExpectedVersion='0.5.3.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$icons=$assembly.GetType('SnipStudio.IconArtwork',$true)
$combo=$assembly.GetType('SnipStudio.StudioComboBox',$true)
$flags=[Reflection.BindingFlags]'Static,Public,NonPublic'
$mapping=$combo.GetMethod('ModeGlyph',$flags)
$create=$icons.GetMethod('CreateToolIcon',$flags)
$svgMethod=$icons.GetMethod('GetToolSvg',$flags)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-ModeIcon([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
$labels=@('Rectangle','Freeform','Window','Full screen','Scrolling','All monitors')
$keys=[string[]]$icons.GetField('Keys',$flags).GetValue($null)
foreach($label in $labels){
    $key=[string]$mapping.Invoke($null,[object[]]@($label))
    Confirm-ModeIcon (-not [string]::IsNullOrEmpty($key) -and $keys -ccontains $key) ($label+' maps to a supported capture-mode icon')
}
Confirm-ModeIcon ([string]$mapping.Invoke($null,[object[]]@('sCrOlLiNg')) -ceq 'scrolling') 'Scrolling mapping is case-insensitive'
Confirm-ModeIcon (@($keys|Where-Object {$_ -ceq 'scrolling'}).Count -eq 1) 'The scrolling icon appears once in the catalog'
$svg=[string]$svgMethod.Invoke($null,[object[]]@('scrolling'))
$xml=[xml]$svg
Confirm-ModeIcon ($xml.DocumentElement.LocalName -eq 'svg' -and $xml.DocumentElement.GetAttribute('viewBox') -eq '0 0 24 24') 'Scrolling exports a valid 24-unit SVG'
Confirm-ModeIcon ($svg -match 'stroke="currentColor"') 'Scrolling SVG follows the theme color'
Confirm-ModeIcon ($xml.DocumentElement.ChildNodes.Count -ge 2) 'Scrolling SVG contains capture frame and movement geometry'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[IO.File]::WriteAllText((Join-Path $output 'scrolling.svg'),$svg,[Text.UTF8Encoding]::new($false))
$proofs=New-Object 'System.Collections.Generic.List[string]'
foreach($size in @(16,18,24,64)){
    foreach($ink in @([Drawing.Color]::Black,[Drawing.Color]::White)){
        $icon=$create.Invoke($null,[object[]]@('scrolling',$size,$ink))
        try{
            Confirm-ModeIcon ($icon.Width -eq $size -and $icon.Height -eq $size) ('Scrolling raster dimensions '+$size+' / '+$ink.Name)
            $visible=0;$inside=0
            for($y=0;$y -lt $size;$y++){
                for($x=0;$x -lt $size;$x++){
                    if($icon.GetPixel($x,$y).A -gt 32){$visible++;if($x -gt 0 -and $x -lt $size-1 -and $y -gt 0 -and $y -lt $size-1){$inside++}}
                }
            }
            Confirm-ModeIcon ($visible -gt 8 -and $inside -eq $visible) ('Scrolling raster is visible and stays inside its bounds '+$size+' / '+$ink.Name)
            if($size -eq 18 -or $size -eq 64){
                $name='scrolling-'+$size+'-'+$ink.Name.ToLowerInvariant()+'.png'
                $icon.Save((Join-Path $output $name),[Drawing.Imaging.ImageFormat]::Png)
                $proofs.Add($name)
            }
        }finally{$icon.Dispose()}
    }
}
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Proofs=$proofs.ToArray();Scope='Pure capture-mode mapping plus raster and SVG rendering from the signed application; no UI controls or forms were constructed.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'scrolling-mode-icon.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed scrolling-mode icon checks; raster/SVG proofs exported without UI.')
