# Signed-library, unshown color-control checks only. No application entrypoint,
# dialog display, focus command, close command, desktop, clipboard or preferences.
param(
    [ValidatePattern('\Arelease-\d+\.\d+\.\d+-signed\z')][string]$ReleaseFolder='release-0.6.2-signed',
    [ValidatePattern('\A\d+\.\d+\.\d+\.0\z')][string]$ExpectedVersion='0.6.2.0'
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed application is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -cne $ExpectedVersion){throw 'Unexpected application version.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$panelType=$assembly.GetType('SnipStudio.ColorPickerPanel',$true)
$dialogType=$assembly.GetType('SnipStudio.ColorPaletteDialog',$true)
$popupType=$assembly.GetType('SnipStudio.ShapeStylePopup',$true)
$styleType=$assembly.GetType('SnipStudio.ShapeStyle',$true)
$colorSequence=[Collections.Generic.IEnumerable[Drawing.Color]]
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Color([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Invoke-Color([Reflection.MethodBase]$Method,$Target,[object[]]$Arguments){
    if($null -eq $Method){throw 'A required color contract is absent.'}
    for($index=0;$index -lt $Arguments.Length;$index++){if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}}
    if($null -ne $Target){$Target=$Target.PSObject.BaseObject}
    if($Method -is [Reflection.ConstructorInfo]){return $Method.Invoke($Arguments)}
    return $Method.Invoke($Target,$Arguments)
}
function Property-Color($Object,[string]$Name){return $Object.GetType().GetProperty($Name,$instance).GetValue($Object.PSObject.BaseObject,$null)}
function Set-ColorProperty($Object,[string]$Name,$Value){$null=Invoke-Color ($Object.GetType().GetProperty($Name,$instance).GetSetMethod($true)) $Object ([object[]]@($Value))}
function Field-Color($Object,[string]$Name){return $Object.GetType().GetField($Name,$instance).GetValue($Object.PSObject.BaseObject)}
function Controls-Color([Windows.Forms.Control]$Root){foreach($control in $Root.Controls){$control;Controls-Color $control}}
function Find-ColorControl($Root,[type]$Type,[string]$AccessiblePattern){
    $found=@(Controls-Color $Root|Where-Object {$Type.IsAssignableFrom($_.GetType()) -and $_.AccessibleName -match $AccessiblePattern})
    if($found.Count -ne 1){throw ('Expected one '+$Type.Name+' control matching '+$AccessiblePattern)}
    return $found[0]
}
function Hex-Color([Drawing.Color]$Color){return '#'+$Color.R.ToString('X2',[Globalization.CultureInfo]::InvariantCulture)+$Color.G.ToString('X2',[Globalization.CultureInfo]::InvariantCulture)+$Color.B.ToString('X2',[Globalization.CultureInfo]::InvariantCulture)}
function Add-ColorEvent($Object,[string]$Name,[scriptblock]$Handler){
    $event=$Object.GetType().GetEvent($Name,$instance)
    if($null -eq $event){throw ('Missing color event '+$Name)}
    $callback=$Handler -as $event.EventHandlerType
    if($null -eq $callback){throw ('Could not bind color event '+$Name)}
    $null=Invoke-Color ($event.GetAddMethod($true)) $Object ([object[]]@($callback))
    return [pscustomobject]@{Event=$event;Callback=$callback}
}
function Remove-ColorEvent($Object,$Subscription){if($null -ne $Subscription){$null=Invoke-Color ($Subscription.Event.GetRemoveMethod($true)) $Object ([object[]]@($Subscription.Callback))}}

$opcodes=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){$opcode=$field.GetValue($null);$opcodes[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode}
function Read-ColorCalls([Reflection.MethodBase]$Method){
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $instructionOffset=$offset;$code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $opcode=$opcodes[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $opcode){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$opcode.OperandType.ToString();$length=0
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod'){$called=$Method.Module.ResolveMethod([BitConverter]::ToInt32($bytes,$offset));[pscustomobject]@{Offset=$instructionOffset;Method=$called;Name=$called.Name;Type=$called.DeclaringType.FullName;Opcode=$opcode.Name}}
        $offset+=$length
    }
}
function Local-ColorCalls([type]$Type){
    $types=New-Object 'System.Collections.Generic.List[type]';$types.Add($Type)
    for($index=0;$index -lt $types.Count;$index++){foreach($nested in $types[$index].GetNestedTypes([Reflection.BindingFlags]'Public,NonPublic')){$types.Add($nested)}}
    foreach($current in $types){
        foreach($method in @($current.GetMethods(($instance -bor $static -bor [Reflection.BindingFlags]::DeclaredOnly)))+@($current.GetConstructors($instance))){Read-ColorCalls $method}
    }
}

# Retained parser API accepts RGB hex only, with optional # and shorthand.
$parse=$dialogType.GetMethod('TryParseHex',$static)
foreach($case in @(
    @{Text='#0078D4';Valid=$true;Expected=[Drawing.Color]::FromArgb(0,120,212)},
    @{Text='abc';Valid=$true;Expected=[Drawing.Color]::FromArgb(170,187,204)},
    @{Text=' #123 ';Valid=$true;Expected=[Drawing.Color]::FromArgb(17,34,51)},
    @{Text='000000';Valid=$true;Expected=[Drawing.Color]::Black},
    @{Text='#FFFFFF';Valid=$true;Expected=[Drawing.Color]::White},
    @{Text='';Valid=$false;Expected=[Drawing.Color]::Black},
    @{Text='#12';Valid=$false;Expected=[Drawing.Color]::Black},
    @{Text='#12345678';Valid=$false;Expected=[Drawing.Color]::Black},
    @{Text='#GG0000';Valid=$false;Expected=[Drawing.Color]::Black}
)){
    [object[]]$arguments=@($case.Text,[Drawing.Color]::Black)
    $valid=[bool](Invoke-Color $parse $null $arguments)
    Confirm-Color ($valid -eq $case.Valid -and (-not $valid -or $arguments[1].ToArgb() -eq $case.Expected.ToArgb())) ('Retained RGB parser: '+$(if($case.Valid){$case.Text}else{'invalid input'}))
}

$panelConstructor=$panelType.GetConstructor($instance,$null,[type[]]@([Drawing.Color],$colorSequence,[bool],[single]),$null)
$dialogConstructor=$dialogType.GetConstructor($instance,$null,[type[]]@([Drawing.Color],$colorSequence),$null)
$shapeConstructor=$styleType.GetConstructor([type[]]@([Drawing.Color],[Drawing.Color],[single],[int],[int]))
$acceptedType=([Action[object]]).GetGenericTypeDefinition().MakeGenericType([type[]]@($styleType))
$oldPopupConstructor=$popupType.GetConstructor($instance,$null,[type[]]@($styleType,[bool],[bool],$acceptedType,[single],[Windows.Forms.IWin32Window]),$null)
$popupConstructor=$popupType.GetConstructor($instance,$null,[type[]]@($styleType,[bool],[bool],$acceptedType,[single],[Windows.Forms.IWin32Window],$colorSequence),$null)
Confirm-Color ($null -ne $panelConstructor -and $null -ne $dialogConstructor -and $null -ne $oldPopupConstructor -and $null -ne $popupConstructor) 'Shared panel, standalone dialog and both shape-popup constructor contracts remain available'
$expand=$panelType.GetMethod('SetSpectrumExpanded',$instance)
$expandCalls=@(Read-ColorCalls $expand)
Confirm-Color (@($expandCalls|Where-Object {$_.Name -in @('Focus','Select','Show','ShowDialog','Close','FocusColorInput')}).Count -eq 0) 'Changing inline expansion does not focus, show or close another window'

foreach($scale in @([single]1,[single]1.5,[single]2)){
    [Drawing.Color[]]$recent=@([Drawing.Color]::Red,[Drawing.Color]::Blue,[Drawing.Color]::Red)
    $recentBefore=@($recent|ForEach-Object {$_.ToArgb()})
    $panel=$null;$colorSubscription=$null;$heightSubscription=$null
    $events=@{Colors=0;Heights=0}
    try{
        $panel=Invoke-Color $panelConstructor $null ([object[]]@([Drawing.Color]::FromArgb(51,102,153),$recent,$true,$scale))
        $panel.Visible=$false
        $colorSubscription=Add-ColorEvent $panel 'ColorChanged' {param($sender,$eventArgs)$events.Colors++}.GetNewClosure()
        $heightSubscription=Add-ColorEvent $panel 'ContentHeightChanged' {param($sender,$eventArgs)$events.Heights++}.GetNewClosure()
        $hex=Find-ColorControl $panel ([Windows.Forms.TextBox]) 'hex'
        $red=Find-ColorControl $panel ([Windows.Forms.NumericUpDown]) '^Red'
        $green=Find-ColorControl $panel ([Windows.Forms.NumericUpDown]) '^Green'
        $blue=Find-ColorControl $panel ([Windows.Forms.NumericUpDown]) '^Blue'
        Confirm-Color ([bool](Property-Color $panel 'IsValidColor') -and (Property-Color $panel 'SelectedColor').ToArgb() -eq [Drawing.Color]::FromArgb(51,102,153).ToArgb()) ('Scale '+$scale+': initial color is valid and exact')
        $collapsed=[int](Property-Color $panel 'ContentHeight')
        $null=Invoke-Color $expand $panel ([object[]]@($true))
        Confirm-Color ([bool](Property-Color $panel 'SpectrumExpanded') -and [int](Property-Color $panel 'ContentHeight') -gt $collapsed -and $events.Heights -ge 1) ('Scale '+$scale+': spectrum expands in place and announces its extra height')
        Confirm-Color ([int]$red.Value -eq 51 -and [int]$green.Value -eq 102 -and [int]$blue.Value -eq 153) ('Scale '+$scale+': expanded RGB channels match the existing color')
        $red.Value=200;$green.Value=100;$blue.Value=50
        $rgb=[Drawing.Color](Property-Color $panel 'SelectedColor')
        Confirm-Color ($rgb.R -eq 200 -and $rgb.G -eq 100 -and $rgb.B -eq 50 -and $hex.Text -ceq (Hex-Color $rgb)) ('Scale '+$scale+': RGB changes synchronize the selected color and hex field')
        $brightness=Find-ColorControl $panel ([Windows.Forms.Control]) '^Color brightness$'
        # SetValue is the slider's managed change boundary; unlike assigning its
        # display-only Value property, it publishes ValueChanged. No input or
        # mouse/focus handler is invoked.
        $brightnessChange=$brightness.GetType().GetMethod('SetValue',$instance)
        $brightnessCalls=@(Read-ColorCalls $brightnessChange)
        Confirm-Color (@($brightnessCalls|Where-Object {$_.Name -in @('Focus','Show','ShowDialog','Close','CreateGraphics','SetFromMouse')}).Count -eq 0) ('Scale '+$scale+': the synthetic brightness boundary performs no window or input operation')
        $null=Invoke-Color $brightnessChange $brightness ([object[]]@([double]0.5))
        $dimmed=[Drawing.Color](Property-Color $panel 'SelectedColor')
        Confirm-Color ([Math]::Abs([int]$dimmed.R-128) -le 1 -and [Math]::Abs([int]$dimmed.G-64) -le 1 -and [Math]::Abs([int]$dimmed.B-32) -le 1 -and $hex.Text -ceq (Hex-Color $dimmed)) ('Scale '+$scale+': brightness changes preserve the chosen hue and synchronize hex')
        $hex.Text='#GG0000'
        Confirm-Color (-not [bool](Property-Color $panel 'IsValidColor') -and (Property-Color $panel 'SelectedColor').ToArgb() -eq $dimmed.ToArgb()) ('Scale '+$scale+': invalid hex preserves the last valid color')
        $null=Invoke-Color $expand $panel ([object[]]@($false))
        Confirm-Color (-not [bool](Property-Color $panel 'SpectrumExpanded') -and [int](Property-Color $panel 'ContentHeight') -eq $collapsed -and -not [bool](Property-Color $panel 'IsValidColor')) ('Scale '+$scale+': collapse restores the compact height without accepting invalid input')
        $hex.Text='#123'
        Confirm-Color ([bool](Property-Color $panel 'IsValidColor') -and (Property-Color $panel 'SelectedColor').ToArgb() -eq [Drawing.Color]::FromArgb(17,34,51).ToArgb() -and $events.Colors -ge 1) ('Scale '+$scale+': correcting hex restores a valid selection and publishes the change')
        Set-ColorProperty $panel 'SelectedColor' ([Drawing.Color]::Empty)
        Confirm-Color ((Property-Color $panel 'SelectedColor').IsEmpty -and [bool](Property-Color $panel 'IsValidColor')) ('Scale '+$scale+': shape channels can deliberately choose None')
        Confirm-Color (@(Controls-Color $panel|Where-Object {$_ -is [Windows.Forms.Button] -and $_.AccessibleName -ceq 'None'}).Count -eq 1) ('Scale '+$scale+': the shape palette exposes one explicit None swatch')
        Confirm-Color (-not $panel.Visible -and -not $panel.IsHandleCreated) ('Scale '+$scale+': panel checks remain unshown without a native window')
    }finally{
        if($null -ne $panel){Remove-ColorEvent $panel $colorSubscription;Remove-ColorEvent $panel $heightSubscription;$panel.Dispose()}
    }
    Confirm-Color (@(Compare-Object $recentBefore @($recent|ForEach-Object {$_.ToArgb()})).Count -eq 0) ('Scale '+$scale+': abandoning color edits does not change caller recent colors')
}

# The standalone dialog and shape popup must each host exactly the same panel.
$dialog=$null
try{
    [Drawing.Color[]]$recent=@([Drawing.Color]::Blue)
    $dialog=Invoke-Color $dialogConstructor $null ([object[]]@([Drawing.Color]::Red,$recent))
    $pickers=@(Controls-Color $dialog|Where-Object {$_.GetType() -eq $panelType})
    Confirm-Color ($pickers.Count -eq 1 -and $dialog.ShowInTaskbar) 'Standalone color selection uses one shared panel and remains taskbar reachable'
    $picker=$pickers[0];$hex=Find-ColorControl $picker ([Windows.Forms.TextBox]) 'hex'
    Confirm-Color (@(Controls-Color $picker|Where-Object {$_ -is [Windows.Forms.Button] -and $_.AccessibleName -ceq 'None'}).Count -eq 0) 'Standalone ink colors omit the shape-only None choice'
    $useColor=$dialog.AcceptButton
    $hex.Text='#BADXYZ'
    Confirm-Color (-not $useColor.Enabled -and -not [bool](Property-Color $picker 'IsValidColor') -and $dialog.DialogResult -eq [Windows.Forms.DialogResult]::None) 'Standalone invalid input disables acceptance without closing or choosing a color'
    $hex.Text='#0078D4'
    Confirm-Color ($useColor.Enabled -and [bool](Property-Color $picker 'IsValidColor')) 'Standalone acceptance becomes available after valid correction'
    Confirm-Color ($dialog.CancelButton.DialogResult -eq [Windows.Forms.DialogResult]::Cancel -and -not $dialog.Visible -and -not $dialog.IsHandleCreated) 'Standalone cancel is explicit and the fixture never opens a dialog'
}finally{if($null -ne $dialog){$dialog.Dispose()}}

$acceptDraft=$popupType.GetMethod('TryAcceptDraft',$instance)
$acceptDraftCalls=@(Read-ColorCalls $acceptDraft)
Confirm-Color (@($acceptDraftCalls|Where-Object {$_.Name -in @('Close','Show','ShowDialog','Focus','Invoke')}).Count -eq 0) 'The once-only draft acceptance gate performs no callback or window operation'
foreach($fillChannel in @($false,$true)){
    $popup=$null;$callbackState=@{Count=0}
    $callback={param($style)$callbackState.Count++}.GetNewClosure() -as $acceptedType
    try{
        $style=Invoke-Color $shapeConstructor $null ([object[]]@([Drawing.Color]::Red,[Drawing.Color]::Blue,[single]7.5,[int]43,[int]67))
        [Drawing.Color[]]$recent=@([Drawing.Color]::Blue,[Drawing.Color]::Green)
        $popup=Invoke-Color $popupConstructor $null ([object[]]@($style,$fillChannel,$true,$callback,[single]1,$null,$recent))
        $content=Field-Color $popup 'content'
        $pickers=@(Controls-Color $content|Where-Object {$_.GetType() -eq $panelType})
        Confirm-Color ($pickers.Count -eq 1) 'Shape styling uses the same shared color panel'
        $picker=$pickers[0];$hex=Find-ColorControl $picker ([Windows.Forms.TextBox]) 'hex'
        $opacity=Field-Color $popup 'opacity'
        $opacity.Value=37
        $hex.Text='#123456'
        $draft=Property-Color $popup 'DraftStyle'
        Confirm-Color ([Math]::Abs($draft.OutlineWidth-7.5) -lt .001 -and $(if($fillChannel){$draft.FillOpacity -eq 37 -and $draft.OutlineOpacity -eq 67}else{$draft.OutlineOpacity -eq 37 -and $draft.FillOpacity -eq 43})) 'Changing a color retains the fractional outline width and independent channel opacity'
        $apply=@(Controls-Color $content|Where-Object {$_ -is [Windows.Forms.Button] -and $_.Text -ceq 'Apply'})
        $hex.Text='#GG0000'
        Confirm-Color ($apply.Count -eq 1 -and -not $apply[0].Enabled) 'Invalid shape color text disables Apply while retaining its draft'
        [object[]]$invalidResult=@($null)
        Confirm-Color (-not [bool](Invoke-Color $acceptDraft $popup $invalidResult) -and $null -eq $invalidResult[0] -and -not [bool](Field-Color $popup 'applied')) 'Invalid input cannot pass the acceptance gate or consume its once-only state'
        $hex.Text='#654321'
        if(-not $fillChannel){$size=Field-Color $popup 'size';$size.Value=12}
        $null=Invoke-Color $expand $picker ([object[]]@($true))
        $null=Invoke-Color $expand $picker ([object[]]@($false))
        Set-ColorProperty $picker 'SelectedColor' ([Drawing.Color]::Empty)
        Confirm-Color (-not $opacity.Enabled -and $opacity.Value -eq 37) 'None disables opacity without losing the user-selected opacity value'
        Set-ColorProperty $picker 'SelectedColor' ([Drawing.Color]::FromArgb(10,20,30))
        $draft=Property-Color $popup 'DraftStyle'
        Confirm-Color ($opacity.Enabled -and $opacity.Value -eq 37 -and [Math]::Abs($draft.OutlineWidth-$(if($fillChannel){7.5}else{12})) -lt .001) 'Returning from None and expansion preserves the opacity and outline-size draft'
        Confirm-Color ($callbackState.Count -eq 0 -and -not [bool](Field-Color $popup 'applied') -and -not $popup.Visible) 'Preview changes do not accept a style or show another window'
        [object[]]$acceptedResult=@($null)
        Confirm-Color ([bool](Invoke-Color $acceptDraft $popup $acceptedResult) -and $null -ne $acceptedResult[0] -and $acceptedResult[0].SameAs($draft)) 'The acceptance gate returns the exact valid draft once'
        [object[]]$duplicateResult=@($null)
        Confirm-Color (-not [bool](Invoke-Color $acceptDraft $popup $duplicateResult) -and $null -eq $duplicateResult[0]) 'A repeated acceptance attempt cannot commit a second draft'
        Confirm-Color ($style.FillColor.ToArgb() -eq [Drawing.Color]::Red.ToArgb() -and $style.OutlineColor.ToArgb() -eq [Drawing.Color]::Blue.ToArgb() -and $style.OutlineWidth -eq 7.5 -and $style.FillOpacity -eq 43 -and $style.OutlineOpacity -eq 67) 'Editing and accepting a draft do not mutate the original immutable style'
    }finally{if($null -ne $popup){$popup.Dispose()}}
    Confirm-Color ($callbackState.Count -eq 0) 'The draft gate and disposal leave callback invocation to the actual Apply command'
}

$abandoned=$null;$abandonedState=@{Count=0}
$abandonedCallback={param($style)$abandonedState.Count++}.GetNewClosure() -as $acceptedType
try{
    $style=Invoke-Color $shapeConstructor $null ([object[]]@([Drawing.Color]::Red,[Drawing.Color]::Blue,[single]4,[int]100,[int]100))
    $abandoned=Invoke-Color $oldPopupConstructor $null ([object[]]@($style,$false,$true,$abandonedCallback,[single]1,$null))
    $picker=Field-Color $abandoned 'picker'
    Set-ColorProperty $picker 'SelectedColor' ([Drawing.Color]::Green)
    Confirm-Color ($abandonedState.Count -eq 0 -and -not [bool](Field-Color $abandoned 'applied') -and $style.OutlineColor.ToArgb() -eq [Drawing.Color]::Blue.ToArgb()) 'The retained constructor supports cancelable preview without mutating the current style'
}finally{if($null -ne $abandoned){$abandoned.Dispose()}}
Confirm-Color ($abandonedState.Count -eq 0) 'Abandoning an unaccepted popup leaves the external style callback untouched'

$disabledFill=$null
try{
    $callback={param($style)} -as $acceptedType
    $disabledFill=Invoke-Color $oldPopupConstructor $null ([object[]]@($style,$true,$false,$callback,[single]1,$null))
    [object[]]$disabledResult=@($null)
    Confirm-Color (-not (Field-Color $disabledFill 'picker').Enabled -and -not [bool](Invoke-Color $acceptDraft $disabledFill $disabledResult) -and $null -eq $disabledResult[0]) 'Tools without a fill channel cannot accept a fill draft'
}finally{if($null -ne $disabledFill){$disabledFill.Dispose()}}

$fit=$dialogType.GetMethod('FitDialogBounds',$static)
Confirm-Color ($null -ne $fit) 'Standalone sizing has a pure work-area bounds policy'
foreach($work in @([Drawing.Rectangle]::new(0,0,1920,1040),[Drawing.Rectangle]::new(-1920,0,1920,1040),[Drawing.Rectangle]::new(0,-1200,1920,1160),[Drawing.Rectangle]::new(64,40,640,440))){
    foreach($desired in @([Drawing.Rectangle]::new(-5000,-5000,480,592),[Drawing.Rectangle]::new(6000,4000,960,1200),[Drawing.Rectangle]::new(100,100,300,240))){
        $arguments=if($fit.GetParameters()[0].Name -match 'work'){[object[]]@($work,$desired)}else{[object[]]@($desired,$work)}
        $bounds=[Drawing.Rectangle](Invoke-Color $fit $null $arguments)
        Confirm-Color ($bounds.Width -gt 0 -and $bounds.Height -gt 0 -and $bounds.Left -ge $work.Left -and $bounds.Top -ge $work.Top -and $bounds.Right -le $work.Right -and $bounds.Bottom -le $work.Bottom) 'Standalone color bounds remain inside synthetic monitor work areas'
    }
}
$panelCalls=@(Local-ColorCalls $panelType)
$popupCalls=@(Local-ColorCalls $popupType)
Confirm-Color (@($panelCalls+$popupCalls|Where-Object {$_.Name -eq 'ShowDialog' -or ($_.Name -eq '.ctor' -and $_.Type -in @('SnipStudio.ColorPaletteDialog','SnipStudio.HoneycombColorDialog'))}).Count -eq 0) 'Shared color controls and shape exact-color selection cannot open nested color dialogs'
$popupSource=[IO.File]::ReadAllText((Join-Path $project 'ShapeStylePopup.cs'))
Confirm-Color ($popupSource -match 'if\s*\(applied' -and $popupSource -match 'applied\s*=\s*true' -and @($popupCalls|Where-Object {$_.Name -eq 'Invoke' -and $_.Type -match '^System\.Action'}).Count -eq 1) 'Shape Apply retains a once-only acceptance guard and one callback site'
Confirm-Color ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'Color checks never open an application form'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{
    Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();
    Scope='Signed RGB parsing, synthetic placement, unshown shared color/shape draft controls, synchronization and callback/source metadata. No Apply/Cancel click, Show/ShowDialog/Close/Focus, app entrypoint, native input, screen capture, clipboard or preference operation.';
    Unverified='Live popup placement/scrolling, actual palette/hexagon pointer hit regions, keyboard focus, physical DPI, native modal activation, clicked Apply/Cancel and installed-version acceptance.'
}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'color-dialogs.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' unshown shared-color component/source checks. No dialog was opened.')
