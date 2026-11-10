# Signed-library checks only. Do not call the application entrypoint or any
# Jump List initialization/recording, COM, registry, window, input or capture API.
param([string]$ReleaseFolder='release-0.5.6-signed',[string]$ExpectedVersion='0.5.6.0')
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
$instance=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$static=[Reflection.BindingFlags]'Static,Public,NonPublic'
$requestType=$assembly.GetType('SnipStudio.ShortcutStartupRequest',$true)
$hostType=$assembly.GetType('SnipStudio.ShortcutHost',$true)
$contextType=$assembly.GetType('SnipStudio.StudioApplicationContext',$true)
$formType=$assembly.GetType('SnipStudio.MainForm',$true)
$serviceType=$assembly.GetType('SnipStudio.TaskbarJumpList',$true)
$checks=New-Object 'System.Collections.Generic.List[string]'
function Confirm-Taskbar([bool]$Condition,[string]$Label){if(-not $Condition){throw ('FAILED: '+$Label)};$checks.Add($Label)}
function Get-TaskbarProperty($Target,[string]$Name){return $Target.GetType().GetProperty($Name,$instance).GetValue($Target,$null)}
function Parse-TaskbarArguments([string[]]$Values){
    $arguments=New-Object object[] 2;$arguments[0]=$Values;$arguments[1]=$null
    $accepted=[bool]$requestType.GetMethod('TryFromArguments',$static).Invoke($null,$arguments)
    return [pscustomobject]@{Accepted=$accepted;Request=$arguments[1]}
}
function Parse-TaskbarBody([string]$Body){
    $arguments=New-Object object[] 2;$arguments[0]=$Body;$arguments[1]=$null
    $accepted=[bool]$requestType.GetMethod('TryParseBody',$static).Invoke($null,$arguments)
    return [pscustomobject]@{Accepted=$accepted;Request=$arguments[1]}
}
function Decode-TaskbarPacket([byte[]]$Bytes){
    $arguments=New-Object object[] 4;$arguments[0]=$Bytes
    $accepted=[bool]$hostType.GetMethod('TryDecodeRequest',$static).Invoke($null,$arguments)
    return [pscustomobject]@{Accepted=$accepted;Id=$arguments[1];Body=$arguments[2];Request=$arguments[3]}
}
function Invoke-TaskbarStatic([string]$Name,[type[]]$Types,[object[]]$Values){
    $method=$serviceType.GetMethod($Name,$static,$null,$Types,$null)
    if($null -eq $method){throw ('Missing pure taskbar policy: '+$Name)}
    return $method.Invoke($null,$Values)
}
function Set-TaskbarField($Object,[string]$Name,$Value){$Object.GetType().GetField($Name,$instance).SetValue($Object,$Value)}
function Get-TaskbarField($Object,[string]$Name){return ,$Object.GetType().GetField($Name,$instance).GetValue($Object)}
function Test-TaskbarException($ErrorRecord,[type]$ExpectedType){
    $exception=$ErrorRecord.Exception
    while($null -ne $exception){
        if($ExpectedType.IsInstanceOfType($exception)){return $true}
        $exception=$exception.InnerException
    }
    return $false
}

# Four fixed tasks must survive cold-start parsing, forwarding serialization and
# strict UTF-16 transport without changing their requested delay.
$packetId='0123456789abcdef0123456789abcdef'
$bodies=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach($seconds in @(0,3,5,10)){
    $parsed=Parse-TaskbarArguments @('--capture','--delay',[string]$seconds)
    Confirm-Taskbar ($parsed.Accepted -and (Get-TaskbarProperty $parsed.Request 'Kind').ToString() -ceq 'Capture' -and
        (Get-TaskbarProperty $parsed.Request 'CaptureDelaySeconds') -eq $seconds) ('Cold-start arguments preserve the explicit '+$seconds+'-second capture task')
    $body=$requestType.GetMethod('ToBody',$instance).Invoke($parsed.Request,@())
    Confirm-Taskbar ($body -ceq ('CAPTURE'+"`n"+[string]$seconds) -and $bodies.Add($body)) ('The '+$seconds+'-second task has a distinct canonical forwarding body')
    $roundtrip=Parse-TaskbarBody $body
    $decoded=Decode-TaskbarPacket ([Text.Encoding]::Unicode.GetBytes($packetId+"`n"+$body+"`0"))
    Confirm-Taskbar ($roundtrip.Accepted -and $decoded.Accepted -and $decoded.Id -ceq $packetId -and $decoded.Body -ceq $body -and
        (Get-TaskbarProperty $roundtrip.Request 'CaptureDelaySeconds') -eq $seconds -and
        (Get-TaskbarProperty $decoded.Request 'CaptureDelaySeconds') -eq $seconds) ('IPC body and strict packet decoding retain the '+$seconds+'-second task')
}
$legacy=Parse-TaskbarArguments @('--capture')
Confirm-Taskbar ($legacy.Accepted -and $null -eq (Get-TaskbarProperty $legacy.Request 'CaptureDelaySeconds') -and
    $requestType.GetMethod('ToBody',$instance).Invoke($legacy.Request,@()) -ceq 'CAPTURE') 'Legacy hotkey capture retains a null override and its exact legacy body'
Confirm-Taskbar ($null -eq (Get-TaskbarProperty (Parse-TaskbarBody 'CAPTURE').Request 'CaptureDelaySeconds')) 'A legacy forwarded capture does not become an immediate task override'
$upper=Parse-TaskbarArguments @('--CAPTURE','--DELAY','3')
Confirm-Taskbar ($upper.Accepted -and (Get-TaskbarProperty $upper.Request 'CaptureDelaySeconds') -eq 3) 'Command switches remain case-insensitive without accepting alternate delay syntax'
foreach($value in @('-1','1','2','4','6','9','11','2147483647','03','+3','3.0',' 3','3 ',"3`n",'',"3`0",$null)){
    $invalid=Parse-TaskbarArguments @('--capture','--delay',$value)
    Confirm-Taskbar (-not $invalid.Accepted -and $null -eq $invalid.Request) 'An unsupported or noncanonical delay cannot create a capture request'
}
$invalidArguments=@(
    [pscustomobject]@{Values=[string[]]@('--capture','--delay')},
    [pscustomobject]@{Values=[string[]]@('--capture','--delay','3','--delay','5')},
    [pscustomobject]@{Values=[string[]]@('--capture','--delay','3','C:\synthetic\part.png')},
    [pscustomobject]@{Values=[string[]]@('--background','--delay','3')},
    [pscustomobject]@{Values=[string[]]@('--delay','3','--capture')},
    [pscustomobject]@{Values=[string[]]@('--capture','--delay=3')},
    [pscustomobject]@{Values=[string[]]@('--exit')})
foreach($case in $invalidArguments){Confirm-Taskbar (-not (Parse-TaskbarArguments $case.Values).Accepted) 'Extra actions, missing values and unknown switches do not fall back to capture'}
foreach($body in @("CAPTURE`n", "CAPTURE`n03", "CAPTURE`n-1", "CAPTURE`n3`n5", "CAPTURE`n3`r", "CAPTURE`n 3", 'CAPTURE\n3', "capture`n3")){
    $bad=Parse-TaskbarBody $body
    Confirm-Taskbar (-not $bad.Accepted -and $null -eq $bad.Request) 'Forwarding refuses malformed, injected or noncanonical timed captures'
}
foreach($path in @('C:\synthetic snips\0007.PNG','\\synthetic-server\share\0007.tiff')){
    $opened=Parse-TaskbarArguments @($path)
    Confirm-Taskbar ($opened.Accepted -and (Get-TaskbarProperty $opened.Request 'Kind').ToString() -ceq 'Open' -and
        $null -eq (Get-TaskbarProperty $opened.Request 'CaptureDelaySeconds')) 'Recent-image opening remains separate from capture task arguments'
}

# Dedup is exercised against an already accepted request, without creating a
# broker HWND. Only managed host fields and in-process marshalled memory exist.
$fixture=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($hostType)
foreach($name in @('pending','received','receivedOrder')){
    $field=$hostType.GetField($name,$instance);$field.SetValue($fixture,[Activator]::CreateInstance($field.FieldType))
}
Set-TaskbarField $fixture 'uiThreadId' ([Threading.Thread]::CurrentThread.ManagedThreadId)
Set-TaskbarField $fixture 'dispatchPosted' $true
$acceptedRequest=(Parse-TaskbarBody "CAPTURE`n3").Request
$pending=Get-TaskbarField $fixture 'pending';$pending.Enqueue($acceptedRequest)
$received=Get-TaskbarField $fixture 'received';$received.Add($packetId,"CAPTURE`n3")
(Get-TaskbarField $fixture 'receivedOrder').Enqueue($packetId)
$copyType=$hostType.GetNestedType('CopyData',[Reflection.BindingFlags]'NonPublic')
function Accept-TaskbarDuplicate([string]$Body){
    $bytes=[Text.Encoding]::Unicode.GetBytes($packetId+"`n"+$Body+"`0")
    $buffer=[Runtime.InteropServices.Marshal]::AllocHGlobal($bytes.Length)
    $packetPointer=[IntPtr]::Zero
    try{
        [Runtime.InteropServices.Marshal]::Copy($bytes,0,$buffer,$bytes.Length)
        $packet=[Activator]::CreateInstance($copyType)
        $copyType.GetField('Magic',$instance).SetValue($packet,[IntPtr]::new(0x53534831))
        $copyType.GetField('ByteCount',$instance).SetValue($packet,$bytes.Length)
        $copyType.GetField('Data',$instance).SetValue($packet,$buffer)
        $packetPointer=[Runtime.InteropServices.Marshal]::AllocHGlobal([Runtime.InteropServices.Marshal]::SizeOf($packet))
        [Runtime.InteropServices.Marshal]::StructureToPtr($packet,$packetPointer,$false)
        return [bool]$hostType.GetMethod('AcceptCopyData',$instance).Invoke($fixture,[object[]]@($packetPointer))
    }finally{
        if($packetPointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::FreeHGlobal($packetPointer)}
        [Runtime.InteropServices.Marshal]::FreeHGlobal($buffer)
    }
}
Confirm-Taskbar ((Accept-TaskbarDuplicate "CAPTURE`n3") -and $pending.Count -eq 1) 'Retrying the same request ID and delay acknowledges without a second queued snip'
Confirm-Taskbar (-not (Accept-TaskbarDuplicate "CAPTURE`n5") -and $pending.Count -eq 1) 'Reusing an accepted request ID for a different delay is rejected'
$script:taskbarDelays=New-Object 'System.Collections.Generic.List[int]'
$script:legacyCaptures=0
$hostType.GetField('TaskCaptureRequested',$instance).SetValue($fixture,[Action[int]]{param($seconds)$script:taskbarDelays.Add($seconds)})
$hostType.GetField('CaptureRequested',$instance).SetValue($fixture,[Action]{$script:legacyCaptures++})
$dispatch=$hostType.GetMethod('DispatchOne',$instance)
$null=$dispatch.Invoke($fixture,@())
Confirm-Taskbar ($script:taskbarDelays.Count -eq 1 -and $script:taskbarDelays[0] -eq 3 -and $script:legacyCaptures -eq 0 -and $pending.Count -eq 0) 'Deferred task dispatch raises only the typed capture callback with its explicit delay'
$pending.Enqueue((Parse-TaskbarBody 'CAPTURE').Request)
$null=$dispatch.Invoke($fixture,@())
Confirm-Taskbar ($script:legacyCaptures -eq 1 -and $script:taskbarDelays.Count -eq 1) 'Legacy capture dispatch retains the original callback without an explicit override'
$pending.Enqueue((Parse-TaskbarBody "CAPTURE`n0").Request)
$null=$dispatch.Invoke($fixture,@())
Confirm-Taskbar ($script:taskbarDelays.Count -eq 2 -and $script:taskbarDelays[1] -eq 0 -and $script:legacyCaptures -eq 1) 'Immediate task dispatch preserves explicit zero instead of the remembered timer'

# Read the signed call graph rather than execute any native Shell registration,
# MainForm capture, image load/save, preference or countdown UI.
$opcodeMap=@{}
foreach($field in [Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Static,Public')){
    $opcode=$field.GetValue($null);$opcodeMap[([int]$opcode.Value -band 65535).ToString([Globalization.CultureInfo]::InvariantCulture)]=$opcode
}
function Read-TaskbarInstructions([Reflection.MethodBase]$Method){
    $body=$Method.GetMethodBody();if($null -eq $body){return}
    $bytes=$body.GetILAsByteArray();$offset=0
    while($offset -lt $bytes.Length){
        $code=[int]$bytes[$offset];$offset++
        if($code -eq 254){$code=65024+[int]$bytes[$offset];$offset++}
        $op=$opcodeMap[$code.ToString([Globalization.CultureInfo]::InvariantCulture)]
        if($null -eq $op){throw ('Unknown IL opcode in '+$Method.Name)}
        $operand=$op.OperandType.ToString();$length=0
        switch($operand){'InlineNone'{$length=0};'ShortInlineBrTarget'{$length=1};'ShortInlineI'{$length=1};'ShortInlineVar'{$length=1};'InlineVar'{$length=2};'InlineI8'{$length=8};'InlineR'{$length=8};'InlineSwitch'{$length=4+4*[BitConverter]::ToInt32($bytes,$offset)};default{$length=4}}
        if($operand -eq 'InlineMethod' -or $operand -eq 'InlineField'){
            $token=[BitConverter]::ToInt32($bytes,$offset)
            $target=if($operand -eq 'InlineMethod'){$Method.Module.ResolveMethod($token)}else{$Method.Module.ResolveField($token)}
            [pscustomobject]@{Target=$target;Name=$target.Name;Type=$target.DeclaringType.FullName;Opcode=$op.Name}
        }
        $offset+=$length
    }
}
function Async-TaskbarMethod([Reflection.MethodInfo]$Method){
    $attribute=@($Method.GetCustomAttributes($false)|Where-Object {$_.GetType().FullName -ceq 'System.Runtime.CompilerServices.AsyncStateMachineAttribute'})
    if($attribute.Count -ne 1){throw ('Expected an async state machine for '+$Method.Name)}
    return $attribute[0].StateMachineType.GetMethod('MoveNext',$instance)
}
$nullableInt=([Nullable[int]])
$main=$assembly.EntryPoint
$mainInstructions=@(Read-TaskbarInstructions $main)
Confirm-Taskbar (@($mainInstructions|Where-Object {$_.Type -ceq 'SnipStudio.ShortcutStartupRequest' -and $_.Name -ceq 'TryFromArguments'}).Count -eq 1) 'Cold startup validates the same strict request grammar before launching any capture'
Confirm-Taskbar (@($mainInstructions|Where-Object {$_.Type -ceq 'SnipStudio.TaskbarJumpList' -and $_.Name -ceq 'Initialize'}).Count -eq 1) 'The primary startup path initializes native taskbar integration without calling the test seam'
Confirm-Taskbar (@($mainInstructions|Where-Object {$_.Type -ceq 'SnipStudio.StudioApplicationContext' -and $_.Name -ceq 'CaptureFromTaskbar'}).Count -eq 1) 'Cold startup routes timed tasks to the typed one-shot capture path'
$contextTask=$contextType.GetMethod('CaptureFromTaskbar',$instance,$null,[type[]]@([int]),$null)
$contextShared=$contextType.GetMethod('CaptureFromShortcut',$instance,$null,[type[]]@($nullableInt),$null)
Confirm-Taskbar (@(Read-TaskbarInstructions $contextTask|Where-Object {$_.Target -eq $contextShared}).Count -eq 1) 'Taskbar capture shares the ordinary existing-editor and hidden-editor lifetime path'
$formCapture=$formType.GetMethod('CaptureFromShortcut',$instance,$null,[type[]]@([bool],$nullableInt),$null)
$startCapture=$formType.GetMethod('StartCapture',$instance,$null,[type[]]@($nullableInt),$null)
$captureInstructions=@(Read-TaskbarInstructions $formCapture)
Confirm-Taskbar (@($captureInstructions|Where-Object {$_.Name -ceq 'CanStartBackgroundCapture'}).Count -eq 1 -and
    @($captureInstructions|Where-Object {$_.Name -ceq 'SetCaptureKind'}).Count -eq 1 -and
    @($captureInstructions|Where-Object {$_.Target -eq $startCapture}).Count -eq 1) 'Timed tasks retain busy/modal guards, select still capture and pass the delay to StartCapture'
$startInstructions=@(Read-TaskbarInstructions (Async-TaskbarMethod $startCapture))
Confirm-Taskbar (@($startInstructions|Where-Object {$_.Name -ceq 'WaitForCapture' -and $_.Type -ceq 'SnipStudio.CaptureCountdown'}).Count -eq 1) 'The one-shot task uses the existing countdown and cancellation path'
Confirm-Taskbar (@($startInstructions|Where-Object {$_.Opcode -in @('stfld','stsfld') -and $_.Name -in @('DelaySeconds','RememberCaptureDelay','sessionCaptureDelay')}).Count -eq 0) 'The task countdown does not write the saved timer or Remember setting'
$recentMethod=$serviceType.GetMethod('RecordRecentFile',$static)
$recentMethods=@(
    $formType.GetMethod('OpenImage',$instance,$null,[type[]]@([string]),$null),
    $formType.GetMethod('SaveImage',$instance,$null,[type[]]@([bool],[bool]),$null))
foreach($method in $recentMethods){
    Confirm-Taskbar (@(Read-TaskbarInstructions $method|Where-Object {$_.Target -eq $recentMethod}).Count -eq 1) ('Successful '+$method.Name+' has one recent-image registration point')
}
$originalMethod=$formType.GetMethod('ProcessCapturedOriginalAsync',$instance)
$originalInstructions=@(Read-TaskbarInstructions (Async-TaskbarMethod $originalMethod))
Confirm-Taskbar (@($originalInstructions|Where-Object {$_.Target -eq $recentMethod}).Count -eq 1) 'Autosaved originals register on the awaited completion path'
$encoderMethods=@($formType.GetMethods($static -bor $instance)|Where-Object {$_.Name -ceq 'EncodeCaptureAsync'})
foreach($encoderMethod in $encoderMethods){
    $encoderInstructions=@(Read-TaskbarInstructions (Async-TaskbarMethod $encoderMethod))
    Confirm-Taskbar (@($encoderInstructions|Where-Object {$_.Target -eq $recentMethod}).Count -eq 0) 'The actual encoder async body does not register shell recent files'
}

# Pure service models have no native initialization, filesystem enumeration or
# Shell effects, even when the synthetic image paths do not exist.
Confirm-Taskbar ($serviceType.GetField('ApplicationId',$static).GetRawConstantValue() -ceq 'AutomationForge.SnipStudio') 'The taskbar identity is stable across program updates'
Confirm-Taskbar ($serviceType.GetField('ImageProgId',$static).GetRawConstantValue() -ceq 'AutomationForge.SnipStudio.Image') 'The optional image handler has its own versionless ProgID'
$tasks=@(Invoke-TaskbarStatic 'GetCaptureTasks' ([type[]]@()) ([object[]]@()))
Confirm-Taskbar ($tasks.Count -eq 4) 'The native task model contains exactly the immediate and three timed capture actions'
$taskTitles=@('Take a new screenshot','New screenshot in 3 seconds','New screenshot in 5 seconds','New screenshot in 10 seconds')
$taskDelays=@(0,3,5,10)
for($index=0;$index -lt $tasks.Count;$index++){
    $task=$tasks[$index]
    $taskArguments=Get-TaskbarProperty $task 'Arguments'
    $taskRequest=Parse-TaskbarArguments ([string[]]$taskArguments.Split(' '))
    Confirm-Taskbar ((Get-TaskbarField $task 'Title') -ceq $taskTitles[$index] -and
        (Get-TaskbarField $task 'DelaySeconds') -eq $taskDelays[$index] -and $taskRequest.Accepted -and
        (Get-TaskbarProperty $taskRequest.Request 'CaptureDelaySeconds') -eq $taskDelays[$index]) ('Native task '+$index+' accurately labels and launches its requested countdown')
}
foreach($seconds in @(-1,1,4,11,[int]::MaxValue)){
    $rejected=$false
    try{Invoke-TaskbarStatic 'BuildCaptureArguments' ([type[]]@([int])) ([object[]]@($seconds))|Out-Null}
    catch {$rejected=Test-TaskbarException $_ ([ArgumentOutOfRangeException])}
    Confirm-Taskbar $rejected 'Task construction cannot emit an unsupported capture delay'
}
function Normalize-TaskbarRecent([string]$Path){
    $arguments=New-Object object[] 2;$arguments[0]=$Path;$arguments[1]=$null
    $accepted=[bool]$serviceType.GetMethod('TryNormalizeRecentFile',$static).Invoke($null,$arguments)
    return [pscustomobject]@{Accepted=$accepted;Path=$arguments[1]}
}
foreach($extension in @('PNG','jpg','jpeg','bmp','gif','tif','tiff')){
    $path='C:\synthetic snips\..\snips\0007.'+$extension
    $normalized=Normalize-TaskbarRecent $path
    $request=Parse-TaskbarArguments @($normalized.Path)
    Confirm-Taskbar ($normalized.Accepted -and $normalized.Path -ceq [IO.Path]::GetFullPath($path) -and $request.Accepted) ('A saved '+$extension+' file is eligible and can reopen through the image command parser')
}
$unc=Normalize-TaskbarRecent '\\synthetic-server\share\0007.png'
Confirm-Taskbar ($unc.Accepted -and (Parse-TaskbarArguments @($unc.Path)).Accepted) 'A complete UNC image remains eligible without requiring a drive letter'
$unicodePath='C:\synthetic\'+[char]0x00FC+[char]0x4E2D+'.png'
$unicode=Normalize-TaskbarRecent $unicodePath
Confirm-Taskbar ($unicode.Accepted -and $unicode.Path -ceq $unicodePath -and (Parse-TaskbarArguments @($unicode.Path)).Accepted) 'A non-ASCII saved filename preserves its exact characters'
foreach($path in @('part.png','C:part.png','\part.png','C:\synthetic\part.pdf','C:\synthetic\part.mp4',
    'C:\synthetic\part.exe','C:\synthetic\part.png:other','C:\synthetic\*.png','C:\synthetic\?.png',
    '\\?\C:\synthetic\part.png','\\.\pipe\part.png','\\server\part.png',"C:\synthetic\part`n.png",'C:\synthetic\part".png','')){
    $invalid=Normalize-TaskbarRecent $path
    Confirm-Taskbar (-not $invalid.Accepted -and $null -eq $invalid.Path) 'A recent entry cannot contain a device/relative/unsupported/injected image path'
}
$quoteCases=@(
    [pscustomobject]@{Value='';Expected='""'},
    [pscustomobject]@{Value='plain';Expected='"plain"'},
    [pscustomobject]@{Value='C:\synthetic snips\0007.png';Expected='"C:\synthetic snips\0007.png"'},
    [pscustomobject]@{Value='C:\snips\';Expected='"C:\snips\\"'},
    [pscustomobject]@{Value='word"word';Expected='"word\"word"'},
    [pscustomobject]@{Value='x\"y';Expected='"x\\\"y"'},
    [pscustomobject]@{Value='C:\snips\notes & parts.png';Expected='"C:\snips\notes & parts.png"'})
foreach($case in $quoteCases){
    $quoted=Invoke-TaskbarStatic 'QuoteArgument' ([type[]]@([string])) ([object[]]@($case.Value))
    Confirm-Taskbar ($quoted -ceq $case.Expected) 'Shell-link arguments preserve spaces, quotes, trailing backslashes and metacharacters without a command interpreter'
}
$command=Invoke-TaskbarStatic 'BuildOpenCommand' ([type[]]@([string])) ([object[]]@('C:\Program Files\Snip Studio\Snip Studio.exe'))
Confirm-Taskbar ($command -ceq '"C:\Program Files\Snip Studio\Snip Studio.exe" "%1"') 'The nondefault image handler quotes both the application and its single file argument'
foreach($path in @('snip.exe','C:snip.exe','\snip.exe','C:\synthetic\snip.cmd','C:\synthetic\snip.exe:other',"C:\synthetic\snip`n.exe")){
    $rejected=$false
    try{Invoke-TaskbarStatic 'BuildOpenCommand' ([type[]]@([string])) ([object[]]@($path))|Out-Null}
    catch {$rejected=Test-TaskbarException $_ ([ArgumentException])}
    Confirm-Taskbar $rejected 'Handler construction refuses a relative, executable-type or control-character injection'
}

# Inspect native ABI metadata only. Never instantiate COM classes or clear an
# initialized native PROPVARIANT as part of these checks.
$variant=$serviceType.GetNestedType('PropVariant',[Reflection.BindingFlags]'NonPublic')
$counted=$serviceType.GetNestedType('CountedPointer',[Reflection.BindingFlags]'NonPublic')
$recentLink=$serviceType.GetNestedType('RecentLinkInfo',[Reflection.BindingFlags]'NonPublic')
$variantSize=[int]$serviceType.GetProperty('NativeVariantSize',$static).GetValue($null,$null)
$expectedVariant=if([IntPtr]::Size -eq 8){24}else{16}
Confirm-Taskbar ($variantSize -eq $expectedVariant -and [Runtime.InteropServices.Marshal]::SizeOf([type]$variant) -eq $expectedVariant) 'PROPVARIANT reserves the full native union size for this process architecture'
Confirm-Taskbar ([Runtime.InteropServices.Marshal]::OffsetOf([type]$variant,'Kind').ToInt64() -eq 0 -and
    [Runtime.InteropServices.Marshal]::OffsetOf([type]$variant,'Pointer').ToInt64() -eq 8 -and
    [Runtime.InteropServices.Marshal]::OffsetOf([type]$variant,'Counted').ToInt64() -eq 8) 'PROPVARIANT type and pointer/count union offsets match the Windows ABI'
Confirm-Taskbar ([Runtime.InteropServices.Marshal]::SizeOf([type]$counted) -eq 2*[IntPtr]::Size -and
    [Runtime.InteropServices.Marshal]::SizeOf([type]$recentLink) -eq 2*[IntPtr]::Size) 'Counted-pointer and SHARD_APPIDINFOLINK buffers include native pointer alignment'
$destinationList=$serviceType.GetNestedType('ICustomDestinationList',[Reflection.BindingFlags]'NonPublic')
$destinationMethods=@($destinationList.GetMethods()|Sort-Object MetadataToken|ForEach-Object Name)
Confirm-Taskbar ($destinationList.GUID.ToString() -ceq '6332debf-87b5-4670-90c0-5e57b408a49e' -and
    ($destinationMethods -join ',') -ceq 'SetAppID,BeginList,AppendCategory,AppendKnownCategory,AddUserTasks,CommitList,GetRemovedDestinations,DeleteList,AbortList') 'The destination-list interface preserves the documented COM IID and complete vtable order'
$objectCollection=$serviceType.GetNestedType('IObjectCollection',[Reflection.BindingFlags]'NonPublic')
Confirm-Taskbar ((@($objectCollection.GetMethods()|Sort-Object MetadataToken|ForEach-Object Name) -join ',') -ceq 'GetCount,GetAt,AddObject,AddFromArray,RemoveObjectAt,Clear') 'The object collection includes the inherited object-array slots before its collection slots'
Confirm-Taskbar (-not [bool]$serviceType.GetField('identityReady',$static).GetValue($null) -and
    -not [bool]$serviceType.GetField('workerRunning',$static).GetValue($null) -and
    -not [bool]$serviceType.GetField('shellReady',$static).GetValue($null) -and
    $serviceType.GetField('pendingFiles',$static).GetValue($null).Count -eq 0) 'Pure model/ABI tests did not initialize Shell identity, start a worker or queue a recent file'

Confirm-Taskbar ([Windows.Forms.Application]::OpenForms.Count -eq 0) 'The checks opened no application forms'
$output=Join-Path $PSScriptRoot $ReleaseFolder.Replace('-signed','-data')
[IO.Directory]::CreateDirectory($output)|Out-Null
[ordered]@{Version=$ExpectedVersion;SHA256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash;CheckCount=$checks.Count;Checks=$checks.ToArray();NoApplicationEntrypoint=$true;NoNativeBrokerOrShell=$true;NoRegistryOrFileAssociations=$true;NoVisibleWindows=$true;Scope='Signed strict task startup/body/packet policies, managed retry/dispatch fixtures and call metadata; pure task/link/path/ABI policies.';Unverified='Actual taskbar Jump List appearance, shell association/Recent population, Windows privacy behavior, pinned-shortcut migration, process launch/IPC and live capture countdown.'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'taskbar-jump-list.json') -Encoding UTF8
Write-Output ('PASS: '+$checks.Count+' signed taskbar Jump List component checks; no native Shell, registry, application window or capture was used.')
