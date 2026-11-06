# Actual signed-library layout with synthetic work areas. No editor is shown,
# native monitor queried, app entrypoint run, capture/input/clipboard performed,
# or preferences loaded/saved. Hidden child-control handles are not excluded.
param([string]$ReleaseFolder='release-0.5.4-signed',[string]$ExpectedVersion='0.5.4.0')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
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
$formType=$assembly.GetType('SnipStudio.MainForm',$true)
$canvasType=$assembly.GetType('SnipStudio.EditorCanvas',$true)
$toolType=$assembly.GetType('SnipStudio.EditorTool',$true)
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$ctor=$formType.GetConstructor($flags,$null,[type[]]@([string],[bool]),$null)
$layout=$formType.GetMethod('LayoutChrome',$flags)
$select=$formType.GetMethod('SelectTool',$flags)
$resize=$formType.GetMethod('ResizeForSnip',$flags)
$scale=$formType.GetField('LayoutScaleOverride',$flags)
$workOverride=$formType.GetField('LayoutWorkingAreaOverride',$flags)
$origin=$canvasType.GetProperty('ImageOrigin',$flags)
if($null -eq $workOverride){throw 'The synthetic working-area layout seam is missing.'}
$checks=New-Object 'System.Collections.Generic.List[string]'
$samples=New-Object 'System.Collections.Generic.List[object]'
function Confirm-Side([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Field($Object,[string]$Name){return $Object.GetType().GetField($Name,$flags).GetValue($Object)}
function Arrange-Side {
    $form.PerformLayout();$chrome.PerformLayout();$null=$layout.Invoke($form,[object[]]@());$workspace.PerformLayout()
}
function Seed-Recognition {
    $fake=[SnipStudio.TextRecognitionResult]::new();$fake.Message='Synthetic recognition; no OCR was run.'
    $canvasType.GetField('recognizedText',$flags).SetValue($canvas,$fake)
    $canvasType.GetField('recognizedBase',$flags).SetValue($canvas,$canvas.ImageIdentity)
    $canvasType.GetField('recognizedRevision',$flags).SetValue($canvas,[long]$canvas.DocumentRevision)
}
function Center-Side {
    return [SnipStudio.ViewportTransform]::ImagePointAt([Drawing.PointF]::new($canvas.ClientSize.Width/2.0,$canvas.ClientSize.Height/2.0),$origin.GetValue($canvas,$null),$canvas.Zoom)
}
$image=[Drawing.Bitmap]::new(1600,1200)
$small=[Drawing.Bitmap]::new(160,50)
try{
    foreach($dock in @('Left','Right')){
        foreach($factor in @([single]1,[single]1.5)){
            foreach($tool in @('Pen','Text')){
                $form=$null
                try{
                    $form=$ctor.Invoke([object[]]@($null,$true))
                    $canvas=Field $form 'canvas';$prefs=Field $form 'preferences'
                    $chrome=Field $form 'chromeRoot';$workspace=Field $form 'workspace';$sidebar=Field $form 'annotationToolbar'
                    $controls=Field $form 'annotationControls'
                    $prefs.AnnotationToolbarPosition=$dock;$prefs.ResizeToSnip=$false
                    $scale.SetValue($form,$factor)
                    $work=[Drawing.Rectangle]::new(1920,40,1800,1200)
                    $workOverride.SetValue($form,$work)
                    $form.Bounds=[Drawing.Rectangle]::new(1980,700,900,260)
                    Arrange-Side
                    $label=$dock+' '+$tool+' scale '+$factor
                    Confirm-Side ($form.Height -eq 260 -and -not $sidebar.Visible) ($label+' preserves compact empty-window height')
                    $canvas.SetImage($image);Seed-Recognition
                    $null=$select.Invoke($form,[object[]]@([Enum]::Parse($toolType,$tool)))
                    Arrange-Side
                    $revision=$canvas.DocumentRevision;$identity=$canvas.ImageIdentity
                    Confirm-Side ($form.Height -gt 260) ($label+' grows on image open even when resize-to-snip is off')
                    Confirm-Side ($sidebar.ClientSize.Height -ge $sidebar.AutoScrollMinSize.Height) ($label+' fits the full measured toolbar without vertical overflow')
                    $style=if($tool -eq 'Text'){Field $form 'textSize'}else{Field $form 'width'}
                    foreach($control in $controls){
                        if(($control -eq (Field $form 'width') -or $control -eq (Field $form 'textSize')) -and $control -ne $style){continue}
                        Confirm-Side ($control.Top -ge 0 -and $control.Bottom -le $sidebar.ClientSize.Height) ($label+' displays '+$control.GetType().Name+' / '+$control.AccessibleName)
                    }
                    Confirm-Side ($form.Top -ge $work.Top -and $form.Bottom -le $work.Bottom) ($label+' keeps the grown window within the owner work area')
                    $before=$form.Bounds;Arrange-Side
                    Confirm-Side ($before -eq $form.Bounds) ($label+' repeated layout does not keep growing the window')
                    $canvas.ZoomAt([single]0.73,[Drawing.Point]::new(300,180));$beforeZoom=$canvas.Zoom;$beforeCenter=Center-Side
                    $null=$select.Invoke($form,[object[]]@([Enum]::Parse($toolType,'Highlighter')))
                    Arrange-Side
                    $afterCenter=Center-Side
                    Confirm-Side ($canvas.Zoom -eq $beforeZoom -and [Math]::Abs($beforeCenter.X-$afterCenter.X) -lt 0.1 -and [Math]::Abs($beforeCenter.Y-$afterCenter.Y) -lt 0.1) ($label+' context-row growth preserves exact zoom and centered image point')
                    Confirm-Side ($sidebar.ClientSize.Height -ge $sidebar.AutoScrollMinSize.Height) ($label+' still fits all tools after the context row opens')
                    Confirm-Side ($canvas.DocumentRevision -eq $revision -and [Object]::ReferenceEquals($identity,$canvas.ImageIdentity)) ($label+' layout growth preserves image and edit history')
                    $null=$select.Invoke($form,[object[]]@([Enum]::Parse($toolType,'Pen')))
                    $prefs.ResizeToSnip=$true;$canvas.SetImage($small);Seed-Recognition
                    $null=$select.Invoke($form,[object[]]@([Enum]::Parse($toolType,'Highlighter')))
                    $null=$resize.Invoke($form,[object[]]@());Arrange-Side
                    Confirm-Side ($sidebar.ClientSize.Height -ge $sidebar.AutoScrollMinSize.Height -and $form.Height -ge $form.MinimumSize.Height) ($label+' resize-to-snip retains full-toolbar minimum for a short image')
                    Confirm-Side (-not $form.Visible -and -not $form.IsHandleCreated) ($label+' remains unshown without a native editor-form handle')
                    $samples.Add([ordered]@{Dock=$dock;Tool=$tool;Scale=$factor;Window=$form.Bounds.ToString();SidebarHeight=$sidebar.ClientSize.Height;RequiredHeight=$sidebar.AutoScrollMinSize.Height;MinimumHeight=$form.MinimumSize.Height})
                }finally{if($null -ne $form){$form.Dispose()}}
            }
        }
    }
    foreach($dock in @('Left','Right')){
        $form=$null
        try{
            $form=$ctor.Invoke([object[]]@($null,$true));$canvas=Field $form 'canvas';$prefs=Field $form 'preferences'
            $chrome=Field $form 'chromeRoot';$workspace=Field $form 'workspace';$sidebar=Field $form 'annotationToolbar'
            $prefs.AnnotationToolbarPosition=$dock;$prefs.ResizeToSnip=$false
            $work=[Drawing.Rectangle]::new(-1600,80,1600,340);$workOverride.SetValue($form,$work)
            $form.Bounds=[Drawing.Rectangle]::new(-1500,250,900,260);$canvas.SetImage($image);Seed-Recognition;Arrange-Side
            Confirm-Side ($form.Height -eq $work.Height -and $form.Top -eq $work.Top) ($dock+' uses available height on a short monitor')
            Confirm-Side ($sidebar.AutoScroll -and $sidebar.AutoScrollMinSize.Height -gt $sidebar.ClientSize.Height) ($dock+' retains scrolling when all tools cannot fit the physical work area')
            Confirm-Side (-not $form.Visible -and -not $form.IsHandleCreated) ($dock+' short-monitor fixture remains unshown without a native editor-form handle')
        }finally{if($null -ne $form){$form.Dispose()}}
    }
    Confirm-Side ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'No editor window was opened'
    $output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data');[IO.Directory]::CreateDirectory($output)|Out-Null
    [ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();Samples=$samples.ToArray();Scope='Actual layout and ResizeForSnip methods on unshown layout-test editors with synthetic working areas and scales. No native monitor lookup, app entrypoint, capture/input, preference load/save or visible UI.';Unverified='Physical monitor/DPI rendering, native window opening/positioning and actual sidebar interaction.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'side-toolbar-height.json') -Encoding UTF8
    Write-Output ('PASS: '+$checks.Count+' signed side-toolbar height checks across '+$samples.Count+' docking/tool/scale samples and two short-monitor cases.')
}finally{$image.Dispose();$small.Dispose()}
