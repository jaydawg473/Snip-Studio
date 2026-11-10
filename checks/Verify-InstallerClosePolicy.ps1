# Source-policy regression only. This never runs Setup, loads the app, probes a
# broker, requests a close, accesses user preferences, or compiles any helper.
param([string]$NsisPath = '', [string]$ReportPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($NsisPath)) { $NsisPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\SnipStudio.nsi' }
$NsisPath = [IO.Path]::GetFullPath($NsisPath)
$source = [IO.File]::ReadAllText($NsisPath).Replace("`r`n","`n")

function Get-ClosePolicyEvaluation([string]$Text) {
    $close = [regex]::Match($Text, '(?s)!macro DefineInstalledApplicationClose Prefix\s*(.*?)!macroend').Groups[1].Value
    $preflight = [regex]::Match($Text, '(?s)!macro DefineInstalledApplicationPreflight Prefix ActionText\s*(.*?)!macroend').Groups[1].Value
    $install = [regex]::Match($Text, '(?s)Section "Snip Studio \(required\)" MainApplication(.*?)SectionEnd').Groups[1].Value
    $uninstall = [regex]::Match($Text, '(?s)Section "Uninstall"(.*?)SectionEnd').Groups[1].Value
    $init = [regex]::Match($Text, '(?s)Function \.onInit\s*(.*?)FunctionEnd').Groups[1].Value
    $uninit = [regex]::Match($Text, '(?s)Function un\.onInit\s*(.*?)FunctionEnd').Groups[1].Value
    $probeResult = [regex]::Match($close, '(?s)close_probe_only_result:(.*?)close_skip_process:').Groups[1].Value
    $setupCloseTest = [regex]::Match($init, '(?s)\$\{GetOptions\} \$0 "/CHECKCLOSE" \$1(.*?)\$\{EndIf\}').Groups[1].Value
    $uninstallCloseTest = [regex]::Match($uninit, '(?s)\$\{un\.GetOptions\} \$0 "/CHECKCLOSE" \$1(.*?)\$\{EndIf\}').Groups[1].Value
    $checks = @(
        @{Label='The dedicated close macro exists'; Pass=($close.Length -gt 0)},
        @{Label='The resource-query macro exists'; Pass=($preflight.Length -gt 0)},
        @{Label='Close helper defensively excludes CHECKREADY'; Pass=$close.Contains('StrCmp $PreflightCheckOnly 1 close_done')},
        @{Label='Close helper defensively excludes silent mode'; Pass=$close.Contains('IfSilent close_done')},
        @{Label='Close helper requires approval'; Pass=$close.Contains('StrCmp $PreflightCloseApproved 1 0 close_done')},
        @{Label='Resource query excludes CHECKREADY before consent'; Pass=($preflight.IndexOf('StrCmp $PreflightCheckOnly 1 preflight_cancel') -ge 0 -and $preflight.IndexOf('StrCmp $PreflightCheckOnly 1 preflight_cancel') -lt $preflight.IndexOf('MessageBox MB_YESNOCANCEL'))},
        @{Label='Resource query excludes silent mode before consent'; Pass=($preflight.IndexOf('IfSilent preflight_cancel') -ge 0 -and $preflight.IndexOf('IfSilent preflight_cancel') -lt $preflight.IndexOf('MessageBox MB_YESNOCANCEL'))},
        @{Label='Consent has Yes No Cancel with No selected by default'; Pass=($preflight.Contains('MB_YESNOCANCEL|MB_ICONQUESTION|MB_DEFBUTTON2') -and $preflight.Contains('/SD IDNO IDYES preflight_request_close'))},
        @{Label='No or Cancel reaches the unchanged cancellation path'; Pass=([regex]::IsMatch($preflight, '/SD IDNO IDYES preflight_request_close\s+Goto preflight_cancel'))},
        @{Label='Only the consent target approves a close'; Pass=([regex]::IsMatch($preflight, 'preflight_request_close:\s+StrCpy \$PreflightCloseApproved 1\s+Call \$\{Prefix\}RequestInstalledApplicationExit'))},
        @{Label='Every completed close request rechecks file use'; Pass=([regex]::IsMatch($preflight, 'Call \$\{Prefix\}RequestInstalledApplicationExit\s+(?:;[^\r\n]*\s+)*Goto preflight_retry'))},
        @{Label='Pending close offers a default-Cancel Retry'; Pass=($preflight.Contains('preflight_close_pending:') -and $preflight.Contains('/SD IDCANCEL IDRETRY preflight_retry'))},
        @{Label='Discovery is bounded and limited to message-only brokers'; Pass=($close.Contains('IntCmp $InstalledCloseScanCount 512 close_find_limit 0 close_find_limit') -and $close.Contains('FindWindowExW(p -3,'))},
        @{Label='Discovery filters the known broker identity'; Pass=$close.Contains('StrCmp $1 "AutomationForge.SnipStudio.ShortcutHost.v1." 0 close_find_broker')},
        @{Label='The target path is expanded from this installation'; Pass=$close.Contains('GetFullPathName $InstalledClosePath "$INSTDIR\Snip Studio.exe"')},
        @{Label='The actual executable is checked before any probe'; Pass=($close.IndexOf('QueryFullProcessImageNameW') -ge 0 -and $close.IndexOf('StrCmp $1 $InstalledClosePath 0 close_skip_process') -ge 0 -and $close.IndexOf('StrCmp $1 $InstalledClosePath 0 close_skip_process') -lt $close.IndexOf('RegisterWindowMessageW'))},
        @{Label='Process access excludes termination and memory writes'; Pass=$close.Contains('OpenProcess(i 0x101000, i 0, i r2)')},
        @{Label='The fixed app protocol is used'; Pass=$close.Contains('RegisterWindowMessageW(w "AutomationForge.SnipStudio.RequestInstallerExit.v1")')},
        @{Label='Probe and exit use separate fixed protocol arguments'; Pass=($close.Contains('p 0x5355, p 1, i 0x23, i 1000') -and $close.Contains('p 0x5355, p 0, i 0x23, i 1000'))},
        @{Label='An uncertain probe does not trigger legacy shutdown'; Pass=($close.Contains('StrCmp $R7 0 close_probe_failed') -and [regex]::IsMatch($close, '(?s)close_probe_failed:.*?Goto close_done\s+close_cooperative:'))},
        @{Label='Cooperative exit pins HWND PID and process liveness again'; Pass=([regex]::IsMatch($close, '(?s)close_cooperative:.*?GetWindowThreadProcessId.*?StrCmp \$R6 \$2 0 close_done.*?GetExitCodeProcess.*?StrCmp \$R6 259 0 close_done.*?SendMessageTimeoutW'))},
        @{Label='Legacy identity includes process creation time'; Pass=($close.Contains('GetProcessTimes(p r3, p r7, p R6, p R7, p R8)') -and $close.Contains('*(i r2, i r8, i r9) p.R0'))},
        @{Label='Legacy RM registers only one validated process and no files/services'; Pass=$close.Contains('RmRegisterResources(i R2, i 0, p 0, i 1, p R0, i 0, p 0)')},
        @{Label='Exactly one normal, non-forced RM shutdown call exists'; Pass=([regex]::Matches($Text, '::RmShutdown\(').Count -eq 1 -and $close.Contains('RmShutdown(i R2, i 0, p 0)'))},
        @{Label='The query session cannot shut down its file lockers'; Pass=(-not $preflight.Contains('::RmShutdown('))},
        @{Label='No termination/global input/WM_QUIT API is used'; Pass=(-not [regex]::IsMatch($close, '::(?:TerminateProcess|TerminateThread|ExitProcess|PostThreadMessage\w*|SendInput|keybd_event|mouse_event)\('))},
        @{Label='Polling is bounded and never kills on timeout'; Pass=($close.Contains('WaitForSingleObject(p r3, i 0)') -and $close.Contains('IntCmp $6 60 close_done 0 close_done') -and $close.Contains('Sleep 250'))},
        @{Label='Native handles, buffers and RM session have cleanup'; Pass=($close.Contains('RmEndSession(i R2)') -and $close.Contains('CloseHandle(p r3)') -and $close.Contains('System::Free $R1') -and $close.Contains('System::Free $R0') -and $close.Contains('System::Free $7'))},
        @{Label='System DLL paths retain NSIS3.04-compatible unquoted syntax'; Pass=(-not [regex]::IsMatch($close, 'System::Call\s+''"\$SYSDIR'))},
        @{Label='Setup and Uninstall both receive the close helper'; Pass=($Text.Contains('!insertmacro DefineInstalledApplicationClose ""') -and $Text.Contains('!insertmacro DefineInstalledApplicationClose "un."'))},
        @{Label='Setup queries before the first file replacement'; Pass=($install.IndexOf('Call CheckInstalledApplication') -ge 0 -and $install.IndexOf('Call CheckInstalledApplication') -lt $install.IndexOf('File "${RELEASE_DIR}\Snip Studio.exe"'))},
        @{Label='Uninstall queries before the deletion manifest'; Pass=($uninstall.IndexOf('Call un.CheckInstalledApplication') -ge 0 -and $uninstall.IndexOf('Call un.CheckInstalledApplication') -lt $uninstall.IndexOf('!include "${TRANSLATION_UNINSTALL_INCLUDE}"'))},
        @{Label='Both initializers explicitly start without probe mode or close approval'; Pass=($init.Contains('StrCpy $PreflightProbeOnly 0') -and $uninit.Contains('StrCpy $PreflightProbeOnly 0') -and $init.Contains('StrCpy $PreflightCloseApproved 0') -and $uninit.Contains('StrCpy $PreflightCloseApproved 0'))},
        @{Label='CHECKREADY retains priority over CHECKBROKER in both initializers'; Pass=($init.IndexOf('"/CHECKREADY"') -ge 0 -and $init.IndexOf('"/CHECKREADY"') -lt $init.IndexOf('"/CHECKBROKER"') -and $uninit.IndexOf('"/CHECKREADY"') -ge 0 -and $uninit.IndexOf('"/CHECKREADY"') -lt $uninit.IndexOf('"/CHECKBROKER"'))},
        @{Label='Setup broker diagnostics always exit before wizard or installation'; Pass=[regex]::IsMatch($init,'(?s)"/CHECKBROKER".*?StrCpy \$PreflightProbeOnly 1\s+Call RequestInstalledApplicationExit\s+SetErrorLevel \$InstalledProbeExitCode\s+Quit\s+\$\{EndIf\}')},
        @{Label='Uninstall broker diagnostics always exit before removal'; Pass=[regex]::IsMatch($uninit,'(?s)"/CHECKBROKER".*?StrCpy \$PreflightProbeOnly 1\s+Call un\.RequestInstalledApplicationExit\s+SetErrorLevel \$InstalledProbeExitCode\s+Quit\s+\$\{EndIf\}')},
        @{Label='Only the interactive consent branch can grant close approval'; Pass=([regex]::Matches($Text,'(?m)^\s*StrCpy \$PreflightCloseApproved 1(?:\s|$)').Count -eq 1)},
        @{Label='Probe-only entry preserves CHECKREADY and normal silent/approval guards'; Pass=[regex]::IsMatch($close,'StrCmp \$PreflightCheckOnly 1 close_done\s+StrCmp \$PreflightProbeOnly 1 close_discovery\s+IfSilent close_done\s+StrCmp \$PreflightCloseApproved 1 0 close_done\s+close_discovery:')},
        @{Label='Every successful probe-mode delivery branches before normal close routing'; Pass=($close.IndexOf('StrCmp $PreflightProbeOnly 1 close_probe_only_result') -gt $close.IndexOf('p 0x5355, p 1, i 0x23, i 1000') -and $close.IndexOf('StrCmp $PreflightProbeOnly 1 close_probe_only_result') -lt $close.IndexOf('StrCmp $R8 0x5355 close_cooperative'))},
        @{Label='Read-only probe completion cannot enter a native close or wait path'; Pass=($probeResult.Length -gt 0 -and $probeResult.Contains('StrCpy $InstalledProbeExitCode 0') -and $probeResult.Contains('StrCmp $R8 21333 close_probe_only_ack close_done') -and -not [regex]::IsMatch($probeResult,'close_cooperative|close_legacy|::|MessageBox|\bCall\b|\bSleep\b|WaitForSingleObject'))},
        @{Label='Cooperative request independently refuses probe mode and missing consent'; Pass=[regex]::IsMatch($close,'close_cooperative:\s+StrCmp \$PreflightProbeOnly 1 close_done\s+StrCmp \$PreflightCloseApproved 1 0 close_done')},
        @{Label='Legacy shutdown independently refuses probe mode and missing consent'; Pass=[regex]::IsMatch($close,'close_legacy:\s+StrCmp \$PreflightProbeOnly 1 close_done\s+StrCmp \$PreflightCloseApproved 1 0 close_done')},
        @{Label='Process-exit wait independently refuses probe mode and missing consent'; Pass=[regex]::IsMatch($close,'close_wait:\s+StrCmp \$PreflightProbeOnly 1 close_done\s+StrCmp \$PreflightCloseApproved 1 0 close_done')},
        @{Label='Probe diagnostic has distinct ACK and phase codes'; Pass=(@([regex]::Matches($close,'(?m)^\s*StrCpy \$InstalledProbeExitCode (0|2[0-9])(?:\s|$)') | ForEach-Object {$_.Groups[1].Value} | Sort-Object -Unique).Count -eq 11)},
        @{Label='Both read-only diagnostics take precedence over interactive close checks'; Pass=($init.IndexOf('"/CHECKBROKER"') -ge 0 -and $init.IndexOf('"/CHECKBROKER"') -lt $init.IndexOf('"/CHECKCLOSE"') -and $uninit.IndexOf('"/CHECKBROKER"') -ge 0 -and $uninit.IndexOf('"/CHECKBROKER"') -lt $uninit.IndexOf('"/CHECKCLOSE"'))},
        @{Label='Setup close check refuses silent mode before querying or prompting'; Pass=($setupCloseTest.IndexOf('IfSilent init_check_close_silent') -ge 0 -and $setupCloseTest.IndexOf('IfSilent init_check_close_silent') -lt $setupCloseTest.IndexOf('Call CheckInstalledApplication') -and [regex]::IsMatch($setupCloseTest,'init_check_close_silent:\s+SetErrorLevel 3\s+Quit'))},
        @{Label='Uninstall close check refuses silent mode before querying or prompting'; Pass=($uninstallCloseTest.IndexOf('IfSilent un_init_check_close_silent') -ge 0 -and $uninstallCloseTest.IndexOf('IfSilent un_init_check_close_silent') -lt $uninstallCloseTest.IndexOf('Call un.CheckInstalledApplication') -and [regex]::IsMatch($uninstallCloseTest,'un_init_check_close_silent:\s+SetErrorLevel 3\s+Quit'))},
        @{Label='Setup close check reuses the ordinary query and exits before installation'; Pass=[regex]::IsMatch($setupCloseTest,'IfSilent init_check_close_silent\s+Call CheckInstalledApplication\s+SetErrorLevel 0\s+Quit')},
        @{Label='Uninstall close check reuses the ordinary query and exits before removal'; Pass=[regex]::IsMatch($uninstallCloseTest,'IfSilent un_init_check_close_silent\s+Call un\.CheckInstalledApplication\s+SetErrorLevel 0\s+Quit')},
        @{Label='Close-only modes cannot grant approval or enter probe/check-only modes'; Pass=($setupCloseTest.Length -gt 0 -and $uninstallCloseTest.Length -gt 0 -and -not [regex]::IsMatch(($setupCloseTest+$uninstallCloseTest),'PreflightCloseApproved|PreflightProbeOnly|PreflightCheckOnly'))},
        @{Label='Close-only modes contain no direct shutdown or installer mutation'; Pass=(-not [regex]::IsMatch(($setupCloseTest+$uninstallCloseTest),'(?im)^\s*(?:File|Write\w*|Delete\w*|CopyFiles|SetOutPath|Create\w*|RMDir|Exec\w*|MessageBox|System::Call)\b|RequestInstalledApplicationExit'))}
    )
    $issues = @($checks | Where-Object { -not $_.Pass } | ForEach-Object { $_.Label })
    return [pscustomobject]@{Count=$checks.Count;Issues=$issues;Labels=@($checks | ForEach-Object { $_.Label })}
}

$baseline = Get-ClosePolicyEvaluation $source
if ($baseline.Issues.Count -ne 0) { throw ('Installer close policy failed: ' + ($baseline.Issues -join '; ')) }
$mutations = @(
    @{Name='Missing diagnostic guard'; From='StrCmp $PreflightCheckOnly 1 close_done'; To='; removed diagnostic guard'},
    @{Name='Missing silent guard'; From='IfSilent close_done'; To='; removed silent guard'},
    @{Name='Missing approval guard'; From='StrCmp $PreflightCloseApproved 1 0 close_done'; To='; removed approval guard'},
    @{Name='Missing installed executable path comparison'; From='StrCmp $1 $InstalledClosePath 0 close_skip_process'; To='; removed installed executable path comparison'},
    @{Name='Forced legacy shutdown'; From='RmShutdown(i R2, i 0, p 0)'; To='RmShutdown(i R2, i 1, p 0)'},
    @{Name='Legacy session targets file resources'; From='RmRegisterResources(i R2, i 0, p 0, i 1, p R0, i 0, p 0)'; To='RmRegisterResources(i R2, i 1, p R0, i 0, p 0, i 0, p 0)'},
    @{Name='Close selected by default'; From='/SD IDNO IDYES preflight_request_close'; To='/SD IDYES IDYES preflight_request_close'},
    @{Name='Setup broker diagnostic falls into installation'; From="    SetErrorLevel `$InstalledProbeExitCode`n    Quit"; To='    SetErrorLevel $InstalledProbeExitCode'},
    @{Name='Uninstall broker diagnostic falls into removal'; From="Call un.RequestInstalledApplicationExit`n    SetErrorLevel `$InstalledProbeExitCode`n    Quit"; To="Call un.RequestInstalledApplicationExit`n    SetErrorLevel `$InstalledProbeExitCode"},
    @{Name='Probe mode grants close approval'; From='StrCpy $PreflightProbeOnly 1'; To="StrCpy `$PreflightProbeOnly 1`n    StrCpy `$PreflightCloseApproved 1"},
    @{Name='Probe mode can follow ordinary close routing'; From='StrCmp $PreflightProbeOnly 1 close_probe_only_result'; To='; removed probe completion barrier'},
    @{Name='Cooperative request loses its independent diagnostic guard'; From="close_cooperative:`n  StrCmp `$PreflightProbeOnly 1 close_done"; To='close_cooperative:'},
    @{Name='Legacy shutdown loses its independent diagnostic guard'; From="close_legacy:`n  StrCmp `$PreflightProbeOnly 1 close_done"; To='close_legacy:'},
    @{Name='Wait loses its independent diagnostic guard'; From="close_wait:`n  StrCmp `$PreflightProbeOnly 1 close_done"; To='close_wait:'},
    @{Name='Discovery becomes unbounded'; From='IntCmp $InstalledCloseScanCount 512 close_find_limit 0 close_find_limit'; To='; removed discovery bound'},
    @{Name='Setup close check permits silent requests'; From='IfSilent init_check_close_silent'; To='; removed close-check silent refusal'},
    @{Name='Uninstall close check permits silent requests'; From='IfSilent un_init_check_close_silent'; To='; removed uninstall close-check silent refusal'},
    @{Name='Close check directly calls the shutdown helper'; From="IfSilent init_check_close_silent`n    Call CheckInstalledApplication"; To="IfSilent init_check_close_silent`n    Call RequestInstalledApplicationExit"},
    @{Name='Close-check flag grants approval'; From='IfSilent init_check_close_silent'; To="StrCpy `$PreflightCloseApproved 1`n    IfSilent init_check_close_silent"},
    @{Name='Setup close check falls into installation'; From="init_check_close_silent:`n    SetErrorLevel 3`n    Quit"; To="init_check_close_silent:`n    SetErrorLevel 3"},
    @{Name='Uninstall close check falls into removal'; From="un_init_check_close_silent:`n    SetErrorLevel 3`n    Quit"; To="un_init_check_close_silent:`n    SetErrorLevel 3"},
    @{Name='Close check mutates an installation directory'; From="IfSilent init_check_close_silent`n    Call CheckInstalledApplication"; To="IfSilent init_check_close_silent`n    SetOutPath `$INSTDIR`n    Call CheckInstalledApplication"}
)
foreach ($mutation in $mutations) {
    if (-not $source.Contains($mutation.From)) { throw ('Mutation did not address a real source clause: ' + $mutation.Name) }
    $evaluation = Get-ClosePolicyEvaluation ($source.Replace($mutation.From, $mutation.To))
    if ($evaluation.Issues.Count -eq 0) { throw ('Policy regression failed to reject: ' + $mutation.Name) }
}
$result = [ordered]@{
    CheckedUtc=[DateTime]::UtcNow.ToString('o'); SourcePath=$NsisPath
    SHA256=(Get-FileHash -LiteralPath $NsisPath -Algorithm SHA256).Hash
    CheckCount=$baseline.Count+$mutations.Count;PolicyClauses=$baseline.Labels
    MutationGuards=@($mutations | ForEach-Object { $_.Name })
    Scope='NSIS source-policy invariants and in-memory negative mutations only; no native request, probe, process discovery, installer/app execution or registry change.'
    Unverified='NSIS compilation, visible consent/save dialogs, real cooperative/legacy shutdown and actual installation.'
}
if (-not [string]::IsNullOrWhiteSpace($ReportPath)) { $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
Write-Output ('PASS: ' + $result.CheckCount + ' installer close source-policy checks; no installer, application or native API was executed.')
