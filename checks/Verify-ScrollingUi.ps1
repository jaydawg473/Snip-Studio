# Signed-library regression for the scrolling controller's first paint.
# No Show, CreateControl, Handle, DrawToBitmap, capture, input, registry changes,
# clipboard, preference Load/Save, helper executable, or application entrypoint.
param(
    [string]$ReleaseFolder = 'release-0.5.1-signed',
    [string]$ExpectedVersion = '0.5.1.0'
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5) { throw 'Use Windows PowerShell 5.' }
if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Run powershell.exe -STA -NoProfile -File.' }
$project = Split-Path $PSScriptRoot -Parent
$app = Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe'
$signature = Get-AuthenticodeSignature -LiteralPath $app
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*' -or $null -eq $signature.TimeStamperCertificate) {
    throw 'A valid timestamped Automation Forge LLC application signature is required.'
}
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion) { throw ('Expected application version ' + $ExpectedVersion + '.') }
if (@([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Snip Studio' }).Count -ne 0) { throw 'Run this check in a fresh process.' }
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly = [Reflection.Assembly]::LoadFrom($app)
$instanceFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$staticFlags = [Reflection.BindingFlags]'Static,Public,NonPublic'
$controlType = $assembly.GetType('SnipStudio.ScrollingCaptureControlForm', $true)
$previewType = $assembly.GetType('SnipStudio.ScrollingCapturePreviewForm', $true)
$buttonType = $assembly.GetType('SnipStudio.StudioButton', $true)
$iconsType = $assembly.GetType('SnipStudio.IconArtwork', $true)
$controlCtor = $controlType.GetConstructor($instanceFlags, $null, [type[]]@([Drawing.Rectangle]), $null)
$previewCtor = $previewType.GetConstructor($instanceFlags, $null, [type[]]@([Drawing.Bitmap], [int], [string]), $null)
$paintMethod = $buttonType.GetMethod('OnPaint', $instanceFlags)
$glyphProperty = $buttonType.GetProperty('Glyph', $instanceFlags)
$createIcon = $iconsType.GetMethod('CreateToolIcon', $staticFlags)
$getSvg = $iconsType.GetMethod('GetToolSvg', $staticFlags)
$checks = New-Object 'System.Collections.Generic.List[string]'
$painted = New-Object 'System.Collections.Generic.List[object]'
function Confirm-ScrollingUi([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw ('FAILED: ' + $Label) }
    $checks.Add($Label)
}
function Read-ScrollingField($Object, [string]$Name) { return $Object.GetType().GetField($Name, $instanceFlags).GetValue($Object) }
function Get-ScrollingDescendants($Control) {
    foreach ($child in $Control.Controls) { $child; Get-ScrollingDescendants $child }
}
function Paint-ScrollingButtons($Form, [string]$Label) {
    $buttons = @(Get-ScrollingDescendants $Form | Where-Object { $_.GetType() -eq $buttonType })
    Confirm-ScrollingUi ($buttons.Count -gt 0) ($Label + ' exposes action buttons')
    foreach ($button in $buttons) {
        $caption = $button.Text
        $enabled = $button.Enabled
        try {
            foreach ($state in @($true, $false)) {
                $button.Enabled = $state
                $bitmap = [Drawing.Bitmap]::new([Math]::Max(1, $button.Width), [Math]::Max(1, $button.Height))
                $graphics = $null; $paint = $null
                try {
                    $graphics = [Drawing.Graphics]::FromImage($bitmap)
                    $paint = [Windows.Forms.PaintEventArgs]::new($graphics, [Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height))
                    # Invoke actual production painting. DrawToBitmap would create
                    # native control handles and is deliberately not used here.
                    try { $null = $paintMethod.Invoke($button, [object[]]@($paint)) }
                    catch { throw ('First-paint regression in ' + $Label + ' / ' + $caption + ': ' + $_.Exception.GetBaseException().Message) }
                    Confirm-ScrollingUi ($bitmap.GetPixel(1, 1).A -gt 0) ($Label + ' / ' + $caption + ' paints ' + $(if ($state) { 'enabled' } else { 'disabled' }))
                    $painted.Add([ordered]@{ Form = $Label; Caption = $caption; Glyph = $glyphProperty.GetValue($button, $null); Enabled = $state; Width = $bitmap.Width; Height = $bitmap.Height })
                } finally {
                    if ($null -ne $paint) { $paint.Dispose() }
                    if ($null -ne $graphics) { $graphics.Dispose() }
                    $bitmap.Dispose()
                }
            }
        } finally { $button.Enabled = $enabled }
    }
    foreach ($control in @($Form) + @(Get-ScrollingDescendants $Form)) {
        Confirm-ScrollingUi (-not $control.IsHandleCreated) ($Label + ' ' + $control.GetType().Name + ' remains without a native handle')
    }
    Confirm-ScrollingUi (-not $Form.Visible) ($Label + ' remains unshown')
}
$controller = $null; $cancelController = $null; $preview = $null; $image = $null
try {
    # Exercise the real controller first. Version 0.5.0 fails on the first
    # Finish paint with Unknown icon key: check, even though construction passes.
    $controller = $controlCtor.Invoke([object[]]@([Drawing.Rectangle]::new(100, 100, 600, 500)))
    Paint-ScrollingButtons $controller 'Scrolling controller'
    $finish = Read-ScrollingField $controller 'finish'
    Confirm-ScrollingUi ($finish.Text -ceq 'Finish') 'The controller retains the Finish action caption'
    Confirm-ScrollingUi (-not [string]::IsNullOrWhiteSpace($glyphProperty.GetValue($finish, $null))) 'The Finish action has a supported visible glyph'
    $null = $controlType.GetMethod('UpdateProgress', $instanceFlags).Invoke($controller, [object[]]@(3, 1400, 'Synthetic progress; no capture was run.'))
    Confirm-ScrollingUi ((Read-ScrollingField $controller 'progress').Text -match '^3 views') 'Progress accepts multiple captured views'
    $null = $controlType.GetMethod('SetFinishing', $instanceFlags).Invoke($controller, [object[]]@('Synthetic completion.'))
    Confirm-ScrollingUi (-not $finish.Enabled -and $finish.Text -match '^Finishing') 'Completion prevents a duplicate Finish submission'
    Paint-ScrollingButtons $controller 'Finishing controller'

    $cancelController = $controlCtor.Invoke([object[]]@([Drawing.Rectangle]::new(100, 100, 600, 500)))
    $null = $controlType.GetMethod('RequestCancel', $instanceFlags).Invoke($cancelController, [object[]]@())
    Confirm-ScrollingUi ([bool]$controlType.GetProperty('CancelIsRequested', $instanceFlags).GetValue($cancelController, $null)) 'Cancel records the discard request'
    Confirm-ScrollingUi (-not (Read-ScrollingField $cancelController 'finish').Enabled -and -not (Read-ScrollingField $cancelController 'cancel').Enabled) 'Cancellation disables capture actions'
    Paint-ScrollingButtons $cancelController 'Cancelling controller'

    $image = [Drawing.Bitmap]::new(320, 1800)
    $preview = $previewCtor.Invoke([object[]]@($image, 3, 'Synthetic preview; no scrolling application was accessed.'))
    Paint-ScrollingButtons $preview 'Scrolling preview'
    Confirm-ScrollingUi ($preview.AcceptButton.Text -ceq 'Use capture' -and $preview.AcceptButton.DialogResult -eq [Windows.Forms.DialogResult]::OK) 'Preview requires explicit Use capture acceptance'
    Confirm-ScrollingUi ($preview.CancelButton.DialogResult -eq [Windows.Forms.DialogResult]::Cancel) 'Preview offers explicit discard'
    $preview.Dispose(); $preview = $null
    Confirm-ScrollingUi ($image.Width -eq 320 -and $image.Height -eq 1800) 'Discarding the preview preserves its borrowed image'

    $partialCtor = $previewType.GetConstructor($instanceFlags, $null, [type[]]@([Drawing.Bitmap], [int], [string], [bool]), $null)
    Confirm-ScrollingUi ($null -ne $partialCtor) 'A stopped-early capture has an explicit preview state'
    $preview = $partialCtor.Invoke([object[]]@($image, 3, 'Synthetic early stop; the bottom is unconfirmed.', $true))
    $partialControls = @(Get-ScrollingDescendants $preview)
    Confirm-ScrollingUi (@($partialControls | Where-Object { $_.Text -ceq 'Review your partial capture' }).Count -eq 1) 'Early stops are labeled partial captures'
    Confirm-ScrollingUi (@($partialControls | Where-Object { $_.Text -like 'More content may remain.*' }).Count -eq 1) 'Partial preview explains that content may remain'
    Confirm-ScrollingUi ($preview.AcceptButton.DialogResult -eq [Windows.Forms.DialogResult]::OK -and $preview.CancelButton.DialogResult -eq [Windows.Forms.DialogResult]::Cancel) 'Partial capture remains reviewable and discardable'
    Paint-ScrollingButtons $preview 'Partial scrolling preview'
    $preview.Dispose(); $preview = $null
    Confirm-ScrollingUi ($image.Width -eq 320 -and $image.Height -eq 1800) 'Discarding a partial preview preserves its borrowed image'

    $keys = [string[]]$iconsType.GetField('Keys', $staticFlags).GetValue($null)
    Confirm-ScrollingUi ($keys.Length -gt 0 -and @($keys | Select-Object -Unique).Count -eq $keys.Length) 'The icon catalog has unique keys'
    foreach ($key in $keys) {
        $icon = $null
        try {
            $icon = $createIcon.Invoke($null, [object[]]@($key, 18, [Drawing.Color]::Black))
            Confirm-ScrollingUi ($icon.Width -eq 18 -and $icon.Height -eq 18) ('Catalog icon renders: ' + $key)
            Confirm-ScrollingUi ([string]$getSvg.Invoke($null, [object[]]@($key)) -match '<svg') ('Catalog SVG renders: ' + $key)
        } finally { if ($null -ne $icon) { $icon.Dispose() } }
    }
    $invalidRejected = $false
    try { $invalidIcon = $createIcon.Invoke($null, [object[]]@('__unsupported_regression_key__', 18, [Drawing.Color]::Black)); if ($null -ne $invalidIcon) { $invalidIcon.Dispose() } }
    catch { $invalidRejected = $_.Exception.GetBaseException() -is [ArgumentException] }
    Confirm-ScrollingUi $invalidRejected 'Unsupported icon keys remain observable failures rather than being silently hidden'
    Confirm-ScrollingUi ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'No application windows were opened'
    $output = Join-Path $PSScriptRoot ($ReleaseFolder + '-data-scrolling-ui')
    [IO.Directory]::CreateDirectory($output) | Out-Null
    [ordered]@{
        Version = $ExpectedVersion; SHA256 = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
        Timestamped = $true; CheckCount = $checks.Count; ButtonPaintCount = $painted.Count
        Scope = 'Signed library; real unshown scrolling controller/preview constructors and StudioButton OnPaint against in-memory bitmaps; full icon catalog.'
        NoVisibleWindows = $true; NoNativeFormHandles = $true; NoDesktopCapture = $true; NoClipboardAccess = $true; NoPreferenceLoadSave = $true; NoInjectedInput = $true
        Unverified = 'Live scrolling capture, target application wheel response, physical monitor rendering/DPI, and visible button interaction.'
        Checks = $checks.ToArray(); ButtonPaints = $painted.ToArray(); CatalogKeys = $keys
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'scrolling-ui-results.json') -Encoding UTF8
    Write-Output ('PASS: ' + $checks.Count + ' signed scrolling UI checks; ' + $painted.Count + ' actual button paints. No windows were shown.')
} finally {
    if ($null -ne $controller) { $controller.Dispose() }
    if ($null -ne $cancelController) { $cancelController.Dispose() }
    if ($null -ne $preview) { $preview.Dispose() }
    if ($null -ne $image) { $image.Dispose() }
}
