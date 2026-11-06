# Source-policy regression only. This never runs Setup, loads the app, probes a
# broker, requests a close, accesses user preferences, or compiles any helper.
param([string]$NsisPath = '', [string]$ReportPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($NsisPath)) { $NsisPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'installer\SnipStudio.nsi' }
$NsisPath = [IO.Path]::GetFullPath($NsisPath)
$source = [IO.File]::ReadAllText($NsisPath)

function Get-ClosePolicyEvaluation([string]$Text) {
    $close = [regex]::Match($Text, '(?s)!macro DefineInstalledApplicationClose Prefix\s*(.*?)!macroend').Groups[1].Value
    $preflight = [regex]::Match($Text, '(?s)!macro DefineInstalledApplicationPreflight Prefix ActionText\s*(.*?)!macroend').Groups[1].Value
    $install = [regex]::Match($Text, '(?s)Section "Snip Studio \(required\)" MainApplication(.*?)SectionEnd').Groups[1].Value
    $uninstall = [regex]::Match($Text, '(?s)Section "Uninstall"(.*?)SectionEnd').Groups[1].Value
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
        @{Label='Discovery is bounded and limited to message-only brokers'; Pass=($close.Contains('IntCmp $InstalledCloseScanCount 512 close_done 0 close_done') -and $close.Contains('FindWindowExW(p -3,'))},
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
        @{Label='Uninstall queries before the deletion manifest'; Pass=($uninstall.IndexOf('Call un.CheckInstalledApplication') -ge 0 -and $uninstall.IndexOf('Call un.CheckInstalledApplication') -lt $uninstall.IndexOf('!include "${TRANSLATION_UNINSTALL_INCLUDE}"'))}
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
    @{Name='Close selected by default'; From='/SD IDNO IDYES preflight_request_close'; To='/SD IDYES IDYES preflight_request_close'}
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
