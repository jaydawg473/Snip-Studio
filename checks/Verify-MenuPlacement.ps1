# Signed-library popup geometry checks. No app entrypoint, forms, native
# monitor discovery, menu display, cursor movement, desktop capture or input.
param([string]$ReleaseFolder='release-0.5.4-signed',[string]$ExpectedVersion='0.5.4.0')
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
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$placementType=$assembly.GetType('SnipStudio.MenuPopupPlacement',$true)
$placement=$placementType.GetMethod('Calculate',$static)
if($null -eq $placement){throw 'The pure menu placement helper is required.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
$samples=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Menu([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Check-MenuPlacement([string]$Label,[Drawing.Rectangle]$Anchor,[Drawing.Size]$Popup,[Drawing.Rectangle]$Work,[Drawing.Point]$Expected){
    $arguments=New-Object object[] 3
    $arguments[0]=$Anchor;$arguments[1]=$Popup;$arguments[2]=$Work
    $location=$placement.Invoke($null,$arguments)
    Confirm-Menu ($location -eq $Expected) ($Label+' chooses the expected owner-monitor location')
    Confirm-Menu ($location.X -ge $Work.Left -and $location.Y -ge $Work.Top) ($Label+' stays inside the owner working-area origin')
    if($Popup.Width -le $Work.Width -and $Popup.Height -le $Work.Height){
        Confirm-Menu ($Work.Contains([Drawing.Rectangle]::new($location,$Popup))) ($Label+' keeps the complete popup in the owner working area')
    }
    $samples.Add([ordered]@{Case=$Label;Anchor=$Anchor.ToString();Popup=$Popup.ToString();WorkingArea=$Work.ToString();Location=$location.ToString();Expected=$Expected.ToString()})
}
$primary=[Drawing.Rectangle]::new(0,0,1920,1040)
$popup=[Drawing.Size]::new(280,680)
Check-MenuPlacement 'Ordinary top-right More button' ([Drawing.Rectangle]::new(1870,40,34,34)) $popup $primary ([Drawing.Point]::new(1624,74))
Check-MenuPlacement 'Button reaches adjacent-right monitor boundary' ([Drawing.Rectangle]::new(1900,40,34,34)) $popup $primary ([Drawing.Point]::new(1640,74))
Check-MenuPlacement 'Same boundary with no right monitor' ([Drawing.Rectangle]::new(1900,40,34,34)) $popup $primary ([Drawing.Point]::new(1640,74))
Check-MenuPlacement 'Owner is the right-hand monitor' ([Drawing.Rectangle]::new(3800,40,34,34)) $popup ([Drawing.Rectangle]::new(1920,0,1920,1040)) ([Drawing.Point]::new(3554,74))
Check-MenuPlacement 'Owner is left of primary' ([Drawing.Rectangle]::new(-60,40,34,34)) $popup ([Drawing.Rectangle]::new(-1920,0,1920,1040)) ([Drawing.Point]::new(-306,74))
Check-MenuPlacement 'Owner is above primary' ([Drawing.Rectangle]::new(1850,-1040,34,34)) $popup ([Drawing.Rectangle]::new(0,-1080,1920,1080)) ([Drawing.Point]::new(1604,-1006))
Check-MenuPlacement 'Owner is above and left' ([Drawing.Rectangle]::new(-60,-1040,34,34)) $popup ([Drawing.Rectangle]::new(-1920,-1080,1920,1040)) ([Drawing.Point]::new(-306,-1006))
Check-MenuPlacement 'Bottom-edge button opens above' ([Drawing.Rectangle]::new(1870,1000,34,34)) $popup $primary ([Drawing.Point]::new(1624,320))
Check-MenuPlacement 'Centered button opens above when below cannot fit' ([Drawing.Rectangle]::new(900,720,34,34)) $popup $primary ([Drawing.Point]::new(654,40))
Check-MenuPlacement 'Left-edge button clamps left' ([Drawing.Rectangle]::new(0,40,34,34)) $popup $primary ([Drawing.Point]::new(0,74))
Check-MenuPlacement 'Taskbar reserves the left edge' ([Drawing.Rectangle]::new(48,40,34,34)) $popup ([Drawing.Rectangle]::new(48,0,1872,1040)) ([Drawing.Point]::new(48,74))
Check-MenuPlacement 'Taskbar reserves the top edge' ([Drawing.Rectangle]::new(1870,40,34,34)) $popup ([Drawing.Rectangle]::new(0,40,1920,1000)) ([Drawing.Point]::new(1624,74))
Check-MenuPlacement 'Scaled popup on a high-resolution right display' ([Drawing.Rectangle]::new(4410,60,51,51)) ([Drawing.Size]::new(420,1020)) ([Drawing.Rectangle]::new(1920,0,2560,1400)) ([Drawing.Point]::new(4041,111))
Check-MenuPlacement 'Doubled popup dimensions on negative-origin display' ([Drawing.Rectangle]::new(-100,80,68,68)) ([Drawing.Size]::new(560,1360)) ([Drawing.Rectangle]::new(-3840,0,3840,2080)) ([Drawing.Point]::new(-592,148))
Check-MenuPlacement 'Monitor layout has a horizontal gap' ([Drawing.Rectangle]::new(1900,40,34,34)) $popup $primary ([Drawing.Point]::new(1640,74))
Check-MenuPlacement 'Small popup remains below its button' ([Drawing.Rectangle]::new(900,720,34,34)) ([Drawing.Size]::new(200,100)) $primary ([Drawing.Point]::new(734,754))
Check-MenuPlacement 'Wider-than-monitor popup pins to owner left' ([Drawing.Rectangle]::new(1870,40,34,34)) ([Drawing.Size]::new(2400,680)) $primary ([Drawing.Point]::new(0,74))
Check-MenuPlacement 'Taller-than-monitor popup pins to owner top' ([Drawing.Rectangle]::new(1870,40,34,34)) ([Drawing.Size]::new(280,1300)) $primary ([Drawing.Point]::new(1624,0))
Check-MenuPlacement 'Popup larger than a small working area pins to its origin' ([Drawing.Rectangle]::new(180,10,34,34)) $popup ([Drawing.Rectangle]::new(0,0,220,180)) ([Drawing.Point]::new(0,0))
Check-MenuPlacement 'Neither vertical direction fully fits' ([Drawing.Rectangle]::new(1870,600,34,34)) ([Drawing.Size]::new(280,900)) $primary ([Drawing.Point]::new(1624,0))

# Inspect actual production call sites without opening a popup or editor.
$opcodeMap=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null)
    $opcodeMap[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-MenuCalls([Reflection.MethodBase]$Method){
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $op=$opcodeMap[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $op){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$op.OperandType.ToString();$length=0
        switch($operand){
            'InlineNone' {$length=0}
            'ShortInlineBrTarget' {$length=1}
            'ShortInlineI' {$length=1}
            'ShortInlineVar' {$length=1}
            'InlineVar' {$length=2}
            'InlineI8' {$length=8}
            'InlineR' {$length=8}
            'InlineSwitch' {$length=4+4*[BitConverter]::ToInt32($bytes,$offset)}
            default {$length=4}
        }
        if($operand -eq 'InlineMethod'){
            $target=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset))
            [pscustomobject]@{Method=$target;Name=$target.Name;Type=$target.DeclaringType.FullName;Opcode=$op.Name}
        }
        $offset+=$length
    }
}

$formType=$assembly.GetType('SnipStudio.MainForm',$true)
$build=$formType.GetMethod('BuildSettingsButton',$instance)
$show=$formType.GetMethod('ShowMoreMenu',$instance)
$reposition=$formType.GetMethod('RepositionMoreMenu',$instance)
Confirm-Menu ($null -ne $build -and $null -ne $show -and $null -ne $reposition) 'The actual More button and menu placement handlers exist'
$buildCalls=@(Read-MenuCalls $build)
$showCalls=@(Read-MenuCalls $show)
$repositionCalls=@(Read-MenuCalls $reposition)
Confirm-Menu (@($buildCalls | Where-Object {$_.Method -eq $show -and $_.Opcode -eq 'ldftn'}).Count -eq 1) 'More button activation invokes the dedicated anchor handler'
Confirm-Menu (@($buildCalls | Where-Object {$_.Name -eq 'add_Opened'}).Count -eq 1) 'The menu subscribes to post-open correction'
Confirm-Menu (@($buildCalls | Where-Object {$_.Name -eq 'add_SizeChanged'}).Count -eq 1) 'The menu also corrects location after later native sizing'
$openedHandlerCalls=@(foreach($callback in @($buildCalls | Where-Object {$_.Opcode -eq 'ldftn' -and $_.Method.DeclaringType.Assembly -eq $assembly})){Read-MenuCalls $callback.Method})
Confirm-Menu (@($openedHandlerCalls | Where-Object {$_.Method -eq $reposition}).Count -eq 2) 'Both subscribed handlers invoke actual-size menu correction'
Confirm-Menu (@($showCalls | Where-Object {$_.Name -eq 'RectangleToScreen' -and $_.Type -eq 'System.Windows.Forms.Control'}).Count -ge 1) 'Menu placement derives screen coordinates from its button bounds'
Confirm-Menu (@($showCalls | Where-Object {$_.Name -eq 'FromControl' -and $_.Type -eq 'System.Windows.Forms.Screen'}).Count -ge 1) 'Menu placement chooses the monitor from its control'
Confirm-Menu (@($showCalls | Where-Object {$_.Method -eq $placement}).Count -eq 1) 'The shown menu uses the tested owner-area clamp'
Confirm-Menu (@($showCalls | Where-Object {$_.Name -eq 'PointToClient' -and $_.Type -eq 'System.Windows.Forms.Control'}).Count -eq 1) 'The clamped popup position is converted to button-relative coordinates'
$controlShow=@($showCalls | Where-Object {
    if($_.Name -ne 'Show'){return $false}
    $parameters=$_.Method.GetParameters()
    return $parameters.Count -eq 3 -and $parameters[0].ParameterType -eq [Windows.Forms.Control] -and $parameters[1].ParameterType -eq [Drawing.Point] -and $parameters[2].ParameterType -eq [Windows.Forms.ToolStripDropDownDirection]
})
Confirm-Menu ($controlShow.Count -eq 1) 'Popup Show retains its source button rather than using global cursor coordinates'
Confirm-Menu (@($repositionCalls | Where-Object {$_.Name -eq 'get_SourceControl'}).Count -ge 1) 'Post-open placement reads the current source button'
Confirm-Menu (@($repositionCalls | Where-Object {$_.Name -eq 'get_Size'}).Count -ge 1) 'Post-open placement uses the actual popup dimensions'
Confirm-Menu (@($repositionCalls | Where-Object {$_.Method -eq $placement}).Count -eq 1) 'Post-open placement reapplies the tested clamp after native sizing'
Confirm-Menu (@($repositionCalls | Where-Object {$_.Name -eq 'set_Location' -or $_.Name -eq 'set_Bounds' -or $_.Name -eq 'SetBounds'}).Count -ge 1) 'Post-open placement updates the popup location'
$allPlacementCalls=@($showCalls)+@($repositionCalls)+@(Read-MenuCalls $placement)
Confirm-Menu (@($allPlacementCalls | Where-Object {$_.Name -eq 'get_Position' -and $_.Type -eq 'System.Windows.Forms.Cursor'}).Count -eq 0) 'More menu placement does not consult the cursor on another monitor'
Confirm-Menu (@(Read-MenuCalls $placement | Where-Object {$_.Type -eq 'System.Windows.Forms.Screen' -or $_.Type -eq 'System.Windows.Forms.Cursor' -or $_.Type -eq 'System.Windows.Forms.SystemInformation'}).Count -eq 0) 'Pure placement depends only on the supplied owner monitor, anchor and popup size'

$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Samples=$samples.ToArray();Scope='Pure physical-coordinate placement and signed call metadata only. No control/form, native monitor discovery, popup, app entrypoint, input or desktop capture.';Unverified='Visible Windows popup placement, physical mixed-DPI transitions and native submenu navigation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'menu-placement.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed menu placement checks across '+$samples.Count+' synthetic monitor/button samples. No app or popup was shown.')
