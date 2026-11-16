# Signed-library synthetic checks only. No Show/ShowDialog, shell/history reads,
# user image files, application entrypoint, capture, input, or source compilation.
param([string]$ReleaseFolder='release-0.6.4-signed',[string]$ExpectedVersion='0.6.4.0',[string]$ExpectedSha256='')
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
$actualSha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
if($ExpectedSha256 -and $actualSha256 -cne $ExpectedSha256){throw 'The signed app does not match the expected SHA256.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$assembly=[Reflection.Assembly]::LoadFrom($app)
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$formType=$assembly.GetType('SnipStudio.RecentSnipsForm',$true)
$serviceType=$assembly.GetType('SnipStudio.RecentSnipService',$true)
$layoutType=$assembly.GetType('SnipStudio.RecentSnipsLayout',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-RecentUi([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Field-RecentUi($Object,[string]$Name){return $Object.GetType().GetField($Name,$instance).GetValue($Object)}
function Call-RecentUi($Object,[string]$Name,[object[]]$Arguments){
    for($i=0;$i -lt $Arguments.Length;$i++){if($null -ne $Arguments[$i]){$Arguments[$i]=$Arguments[$i].PSObject.BaseObject}}
    return $Object.GetType().GetMethod($Name,$instance).Invoke($Object.PSObject.BaseObject,$Arguments)
}
function Pump-RecentUi([scriptblock]$Until,[int]$Timeout=1500){
    $watch=[Diagnostics.Stopwatch]::StartNew()
    do{[Windows.Forms.Application]::DoEvents();if(& $Until){return};[Threading.Thread]::Sleep(5)}while($watch.ElapsedMilliseconds -lt $Timeout)
    throw ('Synthetic UI continuation did not complete within its bound: '+$Until.ToString())
}
function New-RecentUiBitmap([Drawing.Color]$Color){
    $bitmap=[Drawing.Bitmap]::new(24,24)
    $graphics=[Drawing.Graphics]::FromImage($bitmap)
    try{$graphics.Clear($Color)}finally{$graphics.Dispose()}
    return $bitmap
}
function Is-RecentUiBitmapDisposed([Drawing.Bitmap]$Bitmap){
    # PowerShell's adapted property getter can turn a disposed-image exception
    # into a null value. Reflection preserves the native getter's exception.
    try{$null=[Drawing.Bitmap].GetProperty('Width').GetValue($Bitmap,$null);return $false}catch{return $true}
}

# Measure the exact geometry used by the window, including the small-work-area
# case that previously let a stacked high-DPI list overlap the buttons.
foreach($sample in @(@(940,610,1.0),@(640,411,1.0),@(900,600,2.0),@(1240,900,1.5),@(1920,1200,2.0))){
    $size=[Drawing.Size]::new([int]$sample[0],[int]$sample[1])
    $layout=$layoutType.GetMethod('Measure',$static).Invoke($null,[object[]]@($size,[single]$sample[2]))
    $bounds=[Drawing.Rectangle]::new([Drawing.Point]::Empty,$size)
    $rectangles=@{}
    foreach($name in @('Help','Status','List','Filename','Path','Preview','Refresh','Open','Close')){
        $rectangle=$layoutType.GetField($name,$instance).GetValue($layout);$rectangles[$name]=$rectangle
        Confirm-RecentUi ($rectangle.Width -gt 0 -and $rectangle.Height -gt 0 -and $bounds.Contains($rectangle)) ($name+' stays in bounds at '+($sample -join '/'))
    }
    Confirm-RecentUi (-not $rectangles.List.IntersectsWith($rectangles.Preview) -and -not $rectangles.List.IntersectsWith($rectangles.Open) -and -not $rectangles.Preview.IntersectsWith($rectangles.Path)) ('List, preview, location and actions remain separate at '+($sample -join '/'))
}

$constructor=@($formType.GetConstructors($instance)|Where-Object {$_.GetParameters().Count -eq 3})[0]
if($null -eq $constructor){throw 'The synthetic async-provider constructor is missing.'}
$parameters=$constructor.GetParameters()
$recentTaskType=$parameters[0].ParameterType.GetGenericArguments()[1]
$resultType=$recentTaskType.GetGenericArguments()[0]
$recentCompletionType=[Threading.Tasks.TaskCompletionSource[object]].GetGenericTypeDefinition().MakeGenericType([type[]]@($resultType))
$script:recentCompletion=[Activator]::CreateInstance($recentCompletionType)
$syntheticPaths=[string[]]@('C:\SyntheticRecentSnips\First.png','C:\SyntheticRecentSnips\Second.png','C:\SyntheticRecentSnips\Unavailable.png')
$result=$serviceType.GetMethod('CreateRecentResult',$static).Invoke($null,[object[]]@($syntheticPaths,32,[Threading.CancellationToken]::None))
$recentCompletionType.GetMethod('SetResult').Invoke($script:recentCompletion,[object[]]@($result))|Out-Null
$script:recentReadCount=0
$script:previewRequests=New-Object 'System.Collections.Generic.List[object]'
$script:openedPaths=New-Object 'System.Collections.Generic.List[string]'
$script:nullPreview=[Threading.Tasks.TaskCompletionSource[Drawing.Bitmap]]::new()
$script:nullPreview.SetResult($null)
$readRecent=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($token) $script:recentReadCount++;return $script:recentCompletion.Task},$parameters[0].ParameterType)
$readPreview=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{
    param($imagePath,$edge,$token)
    if($edge -eq 128){return $script:nullPreview.Task}
    $completion=[Threading.Tasks.TaskCompletionSource[Drawing.Bitmap]]::new()
    $script:previewRequests.Add([pscustomobject]@{Path=$imagePath;Edge=$edge;Token=$token;Completion=$completion})
    return $completion.Task
},$parameters[1].ParameterType)
$openImage=[Management.Automation.LanguagePrimitives]::ConvertTo([scriptblock]{param($imagePath) $script:openedPaths.Add($imagePath)},$parameters[2].ParameterType)
$previousContext=[Threading.SynchronizationContext]::Current
$context=[Windows.Forms.WindowsFormsSynchronizationContext]::new()
[Threading.SynchronizationContext]::SetSynchronizationContext($context)
$gallery=$null;$red=$null;$blue=$null;$late=$null
try{
    $gallery=$constructor.Invoke([object[]]@($readRecent,$readPreview,$openImage))
    Confirm-RecentUi (-not $gallery.Visible -and $script:recentReadCount -eq 0 -and $script:previewRequests.Count -eq 0) 'Unshown construction does not read Windows history or previews'
    $task=Call-RecentUi $gallery 'RefreshRecentSnipsAsync' ([object[]]@())
    Pump-RecentUi {$task.IsCompleted}
    $task.GetAwaiter().GetResult()|Out-Null
    $list=Field-RecentUi $gallery 'recentList';$preview=Field-RecentUi $gallery 'preview'
    Confirm-RecentUi ($list.Items.Count -eq 3 -and $script:recentReadCount -eq 1 -and $script:openedPaths.Count -eq 0) 'Synthetic refresh lists rows without invoking the editor callback'
    Confirm-RecentUi ($script:previewRequests.Count -eq 1 -and $script:previewRequests[0].Edge -eq 640) 'Initial selection requests a bounded large preview'
    $first=$script:previewRequests[0]
    $list.SelectedIndex=1
    Confirm-RecentUi ($script:previewRequests.Count -eq 2 -and $first.Token.IsCancellationRequested) 'Keyboard-style selection cancels the previous preview'
    $second=$script:previewRequests[1]
    $red=New-RecentUiBitmap ([Drawing.Color]::Red);$first.Completion.SetResult($red)
    Pump-RecentUi {Is-RecentUiBitmapDisposed $red}
    Confirm-RecentUi (Is-RecentUiBitmapDisposed $red) 'A stale bitmap returned after cancellation is disposed'
    $blue=New-RecentUiBitmap ([Drawing.Color]::Blue);$second.Completion.SetResult($blue)
    Pump-RecentUi {[Object]::ReferenceEquals((Field-RecentUi $preview 'Image'),$blue)}
    Confirm-RecentUi ($script:openedPaths.Count -eq 0 -and [Object]::ReferenceEquals((Field-RecentUi $preview 'Image'),$blue)) 'Selection updates only the large preview, with no editor callback'
    Call-RecentUi $gallery 'PreviewHoveredIndex' ([object[]]@(2))|Out-Null
    Pump-RecentUi {$list.SelectedIndex -eq 2 -and $script:previewRequests.Count -eq 3}
    Confirm-RecentUi ($script:openedPaths.Count -eq 0 -and $script:previewRequests[2].Path -ceq $syntheticPaths[2]) 'Hover selects a large preview without opening an image'
    $unavailable=$script:previewRequests[2];$unavailable.Completion.SetResult($null)
    Pump-RecentUi {(Field-RecentUi $preview 'Message') -like 'Preview unavailable*'}
    Confirm-RecentUi ((Field-RecentUi $gallery 'open').Enabled) 'Unavailable thumbnails retain an explicit Open attempt for moved or offline files'
    Call-RecentUi $gallery 'PreviewHoveredIndex' ([object[]]@(0))|Out-Null
    $key=[Windows.Forms.KeyEventArgs]::new([Windows.Forms.Keys]::Down)
    $list.GetType().GetMethod('OnKeyDown',$instance).Invoke($list,[object[]]@($key))|Out-Null
    $list.SelectedIndex=1
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($watch.ElapsedMilliseconds -lt 220){[Windows.Forms.Application]::DoEvents();[Threading.Thread]::Sleep(5)}
    Confirm-RecentUi ($list.SelectedIndex -eq 1) 'Keyboard navigation cancels a pending mouse-hover change'
    Call-RecentUi $gallery 'OpenSelectedSnip' ([object[]]@())|Out-Null
    Confirm-RecentUi ($script:openedPaths.Count -eq 1 -and $script:openedPaths[0] -ceq $syntheticPaths[1]) 'Only explicit Open invokes the editor callback with the exact selected path'
    $pending=$script:previewRequests[$script:previewRequests.Count-1]
    $gallery.Dispose()
    Confirm-RecentUi ($pending.Token.IsCancellationRequested -and (Is-RecentUiBitmapDisposed $blue)) 'Disposal cancels pending work and disposes the displayed preview'
    $late=New-RecentUiBitmap ([Drawing.Color]::Green);$pending.Completion.SetResult($late)
    Pump-RecentUi {Is-RecentUiBitmapDisposed $late}
    Confirm-RecentUi (Is-RecentUiBitmapDisposed $late) 'A bitmap arriving after the unshown gallery is disposed is also disposed'
    Confirm-RecentUi ($script:openedPaths.Count -eq 1) 'Cancellation and disposal do not invoke the editor callback'
}finally{
    if($null -ne $gallery){$gallery.Dispose()}
    foreach($request in $script:previewRequests){if(-not $request.Completion.Task.IsCompleted){$request.Completion.SetResult($null)}}
    [Windows.Forms.Application]::DoEvents()
    [Threading.SynchronizationContext]::SetSynchronizationContext($previousContext)
    $context.Dispose()
    foreach($bitmap in @($red,$blue,$late)){if($null -ne $bitmap){$bitmap.Dispose()}}
}
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=$actualSha256;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;WindowsRecentHistoryRead=$false;ShellOrCOMExecuted=$false;UserImagesRead=$false;PhysicalInputSent=$false;DesktopCaptured=$false;Scope='Unshown synthetic gallery providers, bounded test-process message pumping and pure layout. No live Shell, user images, app entrypoint or preferences.';Unverified='Displayed hover/open and focus, native history/thumbnail integration and installed upgrade.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'recent-snips-ui.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed Recent gallery component checks.')
