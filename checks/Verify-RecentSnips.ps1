# Signed-library pure model checks. No app entrypoint, Windows Recent reading,
# Shell/COM calls, background worker, shown form, capture, input or preferences.
param([string]$ReleaseFolder='release-0.6.4-signed',[string]$ExpectedVersion='0.6.4.0',[Parameter(Mandatory=$true)][string]$ExpectedSha256)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSEdition -eq 'Core' -or $PSVersionTable.PSVersion.Major -ne 5){throw 'Use Windows PowerShell 5.'}
if([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA'){throw 'Use powershell.exe -STA -NoProfile -File.'}
if($ReleaseFolder -notmatch '\Arelease-\d+\.\d+\.\d+-signed\z'){throw 'Choose a versioned signed release folder.'}
if($ExpectedSha256 -notmatch '\A[0-9A-Fa-f]{64}\z'){throw 'An exact signed app SHA256 is required.'}
$project=Split-Path $PSScriptRoot -Parent
$app=(Resolve-Path -LiteralPath (Join-Path (Join-Path $project $ReleaseFolder) 'Snip Studio.exe')).Path
$signature=Get-AuthenticodeSignature -LiteralPath $app
if($signature.Status -ne 'Valid' -or $null -eq $signature.TimeStamperCertificate -or $signature.SignerCertificate.Subject -notlike '*Automation Forge LLC*'){throw 'A Valid timestamped Automation Forge signed app is required.'}
if([Diagnostics.FileVersionInfo]::GetVersionInfo($app).FileVersion -ne $ExpectedVersion){throw 'Unexpected application version.'}
$actualSha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
if($actualSha256 -ine $ExpectedSha256){throw 'The signed app does not match the expected SHA256.'}
if(@([AppDomain]::CurrentDomain.GetAssemblies()|Where-Object {$_.GetName().Name -eq 'Snip Studio'}).Count -ne 0){throw 'Use a fresh process.'}
Add-Type -AssemblyName System.Drawing
$assembly=[Reflection.Assembly]::LoadFrom($app)
$flags=[Reflection.BindingFlags]'Instance,Static,Public,NonPublic'
$type=$assembly.GetType('SnipStudio.RecentSnipService',$true)
$taskbar=$assembly.GetType('SnipStudio.TaskbarJumpList',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Recent([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Call-Recent([string]$Name,[object[]]$Arguments){
    for($index=0;$index -lt $Arguments.Length;$index++){if($null -ne $Arguments[$index]){$Arguments[$index]=$Arguments[$index].PSObject.BaseObject}}
    return ,$type.GetMethod($Name,$flags).Invoke($null,$Arguments)
}
function Property-Recent($Object,[string]$Name){return ,$Object.GetType().GetProperty($Name,$flags).GetValue($Object,$null)}
function Model-Recent([string[]]$Paths,[int]$Limit,[Threading.CancellationToken]$Token=[Threading.CancellationToken]::None){
    $arguments=New-Object object[] 3;$arguments[0]=$Paths;$arguments[1]=$Limit;$arguments[2]=$Token
    return Call-Recent 'CreateRecentResult' $arguments
}
function Link-Recent([string]$Target,[string]$Arguments,[string]$Executable){
    $values=New-Object object[] 4;$values[0]=$Target;$values[1]=$Arguments;$values[2]=$Executable
    $accepted=$type.GetMethod('TryGetOwnedLinkImagePath',$flags).Invoke($null,$values)
    return [pscustomobject]@{Accepted=[bool]$accepted;Path=$values[3]}
}
function Canceled-Recent($ErrorRecord){
    $exception=$ErrorRecord.Exception
    while($null -ne $exception){if($exception -is [OperationCanceledException]){return $true};$exception=$exception.InnerException};return $false
}
# PowerShell's property adapter can turn a disposed Width getter into null.
# A real method call exposes the GDI+ disposed-object exception reliably.
function Disposed-Recent([Drawing.Bitmap]$Bitmap){try{$null=$Bitmap.GetPixel(0,0);return $false}catch{return $true}}

$none=[Threading.CancellationToken]::None
Confirm-Recent (-not $type.GetField('workerRunning',$flags).GetValue($null) -and $type.GetField('pending',$flags).GetValue($null).Count -eq 0) 'Loading the service does not start workers or native work'
Confirm-Recent ((Call-Recent 'LimitItems' ([object[]]@([int]0))) -eq 1 -and (Call-Recent 'LimitItems' ([object[]]@([int]999))) -eq 32) 'Recent requests never ask Windows for an unbounded zero-count list'
Confirm-Recent ((Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]-1))) -eq 16 -and (Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]128))) -eq 128 -and (Call-Recent 'LimitThumbnailEdge' ([object[]]@([int]99999))) -eq 640) 'Thumbnail allocation requests remain bounded from 16 through 640 pixels'
$empty=Model-Recent @() 32
Confirm-Recent ((Property-Recent $empty 'IsAvailable') -and (Property-Recent $empty 'Items').Count -eq 0 -and (Property-Recent $empty 'Message') -like '*Windows recent-history settings*') 'An empty list is available and explains Windows history control'
$paths=[string[]]@('C:\synthetic snips\0007.PNG','c:\SYNTHETIC SNIPS\0007.png','C:\synthetic snips\0008.jpg','\\synthetic-server\share\0009.tiff')
$result=Model-Recent $paths 32
$items=Property-Recent $result 'Items'
Confirm-Recent ($items.Count -eq 3 -and (Property-Recent $items[0] 'Path') -ceq $paths[0] -and (Property-Recent $items[1] 'Path') -ceq $paths[2]) 'Accepted paths preserve order, case and leading zeros while duplicates collapse case-insensitively'
Confirm-Recent ((Property-Recent $items[0] 'DisplayName') -ceq '0007.PNG' -and (Property-Recent $items[2] 'DisplayName') -ceq '0009.tiff') 'Display names come from accepted paths without reading files'
Confirm-Recent ($items.IsReadOnly) 'Consumers cannot mutate the service result list'
$invalid=[string[]]@('relative.png','C:\synthetic\run.exe','C:\synthetic\note.txt','C:\synthetic\image.png:stream','C:\synthetic\*.png','\\?\C:\synthetic\image.png',"C:\synthetic\bad`nname.png")
Confirm-Recent ((Property-Recent (Model-Recent $invalid 32) 'Items').Count -eq 0) 'Relative, unsupported, device, stream and injected paths are rejected'
$many=New-Object 'System.Collections.Generic.List[string]'
for($i=0;$i -lt 80;$i++){$many.Add(('C:\synthetic\{0:D4}.png' -f $i))}
Confirm-Recent ((Property-Recent (Model-Recent $many.ToArray() 999) 'Items').Count -eq 32 -and (Property-Recent (Model-Recent $many.ToArray() 3) 'Items').Count -eq 3) 'Both source scanning and returned rows obey the requested bound'
$rejected=New-Object 'System.Collections.Generic.List[string]'
for($i=0;$i -lt 32;$i++){$rejected.Add(('C:\synthetic\{0:D4}.txt' -f $i))}
$rejected.Add('C:\synthetic\after-limit.png')
Confirm-Recent ((Property-Recent (Model-Recent $rejected.ToArray() 32) 'Items').Count -eq 0) 'Rejected candidates cannot make enumeration scan beyond the first 32 source entries'
$exe='C:\synthetic app\Snip Studio.exe'
foreach($path in @('C:\synthetic snips\0007.PNG','\\synthetic-server\share\0009.tiff')){
    $quoted=$taskbar.GetMethod('QuoteArgument',$flags).Invoke($null,[object[]]@($path))
    $link=Link-Recent $exe $quoted 'c:\SYNTHETIC APP\Snip Studio.exe'
    Confirm-Recent ($link.Accepted -and $link.Path -ceq $path) 'Owned image links round-trip strict Windows quoting without resolving or launching a target'
}
foreach($arguments in @('"C:\synthetic\a.png" --capture','"C:\synthetic\a.png" "C:\synthetic\b.png"','C:\synthetic\a.png','"C:\synthetic\run.exe"','"relative.png"','"C:\synthetic\a.png" ')){
    Confirm-Recent (-not (Link-Recent $exe $arguments $exe).Accepted) 'Malformed quoting, extra actions and unsupported arguments cannot become recent image paths'
}
foreach($target in @('C:\foreign app\Snip Studio.exe','Snip Studio.exe','C:Snip Studio.exe','C:\synthetic app\not-an-app.png','\\?\C:\synthetic app\Snip Studio.exe')){
    Confirm-Recent (-not (Link-Recent $target '"C:\synthetic\a.png"' $exe).Accepted) 'Foreign, relative, device and non-executable link targets are rejected'
}

$cts=[Threading.CancellationTokenSource]::new()
try{
    $cts.Cancel();$caught=$false
    try{$null=Model-Recent $paths 32 $cts.Token}catch{$caught=Canceled-Recent $_}
    Confirm-Recent $caught 'Canceled model enumeration stops without producing a replacement list'
    $task=Call-Recent 'GetRecentAsync' ([object[]]@([int]32,$cts.Token))
    $thumbnailTask=Call-Recent 'LoadThumbnailAsync' ([object[]]@('C:\synthetic\never-read.png',[int]128,$cts.Token))
    Confirm-Recent ($task.IsCanceled -and $thumbnailTask.IsCanceled -and -not $type.GetField('workerRunning',$flags).GetValue($null)) 'Pre-canceled public requests finish without starting native work'
}finally{$cts.Dispose()}

# Exercise the actual request's ownership/cancellation code synchronously with
# synthetic delegates. No request is enqueued and no Shell operation exists.
$requestType=$type.GetNestedType('Request`1',[Reflection.BindingFlags]::NonPublic).MakeGenericType([Drawing.Bitmap])
$requestConstructor=$requestType.GetConstructors($flags)[0]
$execute=$requestType.GetMethod('Execute',$flags)
$owned=[Drawing.Bitmap]::new(2,2)
$operation=[Func[Drawing.Bitmap]]{$owned}
$request=$requestConstructor.Invoke([object[]]@($operation,$null,$none))
try{
    $null=$execute.Invoke($request,@());$task=Property-Recent $request 'Task'
    Confirm-Recent ($task.IsCompleted -and -not $task.IsCanceled -and [Object]::ReferenceEquals($task.Result,$owned) -and -not (Disposed-Recent $owned)) 'A successful thumbnail transfers exactly one live caller-owned bitmap'
}finally{$request.Dispose();$owned.Dispose()}
$cts=[Threading.CancellationTokenSource]::new();$owned=[Drawing.Bitmap]::new(2,2)
$operation=[Func[Drawing.Bitmap]]{$cts.Cancel();return $owned}
$request=$requestConstructor.Invoke([object[]]@($operation,$null,$cts.Token))
try{
    $null=$execute.Invoke($request,@());$task=Property-Recent $request 'Task'
    Confirm-Recent ($task.IsCanceled -and (Disposed-Recent $owned)) 'A bitmap produced after cancellation is disposed instead of leaked to an abandoned consumer'
}finally{$request.Dispose();$cts.Dispose();$owned.Dispose()}

$premultiplied=[byte[]]@(10,20,30,128,0,0,0,0)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]2,$premultiplied))
try{
    Confirm-Recent ($bitmap.Width -eq 1 -and $bitmap.Height -eq 2 -and $bitmap.GetPixel(0,0).A -eq 128 -and $bitmap.GetPixel(0,1).A -eq 0 -and $bitmap.HorizontalResolution -eq 96) 'Detached thumbnail pixels retain meaningful alpha, two-row orientation and explicit DPI'
    Confirm-Recent ($premultiplied[0] -eq 10 -and $premultiplied[3] -eq 128) 'Thumbnail conversion never changes caller input pixels'
}finally{$bitmap.Dispose()}
$legacy=[byte[]]@(30,20,10,0)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]1,$legacy))
try{Confirm-Recent ($bitmap.GetPixel(0,0).A -eq 255 -and $bitmap.GetPixel(0,0).R -eq 10) 'Legacy RGB thumbnails with unused alpha remain visible'}finally{$bitmap.Dispose()}
$straight=[byte[]]@(0,0,200,128)
$bitmap=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]1,[int]1,$straight))
try{Confirm-Recent ($bitmap.GetPixel(0,0).A -eq 128 -and [Math]::Abs($bitmap.GetPixel(0,0).R-200) -le 2) 'Detectable straight alpha converts to bounded premultiplied storage'}finally{$bitmap.Dispose()}
$caught=$false
try{$null=Call-Recent 'CreateThumbnailBitmap' ([object[]]@([int]641,[int]1,[byte[]]@(0,0,0,255)))}catch{$caught=$true}
Confirm-Recent $caught 'Out-of-bound native bitmap dimensions cannot allocate a larger managed thumbnail'
Confirm-Recent (-not $type.GetField('workerRunning',$flags).GetValue($null) -and $type.GetField('pending',$flags).GetValue($null).Count -eq 0) 'All model checks leave the worker stopped and its queue empty'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=$actualSha256;CheckCount=$checks.Count;Checks=$checks.ToArray();ApplicationEntrypointExecuted=$false;FormShown=$false;PhysicalInputSent=$false;DesktopCaptured=$false;WindowsRecentHistoryRead=$false;ShellOrCOMExecuted=$false;UserPreferencesReadOrWritten=$false;Scope='Signed-library pure recent-path policy, owned-link roundtrip and rejection, bounded DTOs and pixels, pre-cancellation and actual request bitmap ownership.';Unverified='Live Windows history and privacy configuration, native image-factory output and file-lock behavior, displayed Recent gallery, physical hover/open and installation.'}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'recent-snips-model.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed recent-snips model checks.')
