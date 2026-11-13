Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

Var PreflightCheckOnly
Var PreflightProbeOnly
Var PreflightCloseApproved
Var InstalledCloseResult
Var InstalledCloseDetail
Var InstalledClosePath
Var InstalledCloseScanCount
Var InstalledProbeExitCode

!ifndef RELEASE_DIR
  !define RELEASE_DIR "${__FILEDIR__}\..\release-0.6.3-signed"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "${__FILEDIR__}\output"
!endif
!ifndef PACKAGE_KB
  !define PACKAGE_KB 600
!endif
!ifndef TRANSLATION_INSTALL_INCLUDE
  !error "Build through Build-Installer.ps1 so the audited translation payload is enumerated."
!endif
!ifndef TRANSLATION_UNINSTALL_INCLUDE
  !error "The exact-file translation removal manifest is required."
!endif

Name "Snip Studio"
OutFile "${OUTPUT_DIR}\SnipStudio-0.6.3-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Automation Forge\Snip Studio"
InstallDirRegKey HKCU "Software\Automation Forge\Snip Studio" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 16
ShowInstDetails show
ShowUninstDetails show
BrandingText "Automation Forge"
VIProductVersion "0.6.3.0"
VIAddVersionKey /LANG=1033 "ProductName" "Snip Studio"
VIAddVersionKey /LANG=1033 "ProductVersion" "0.6.3.0"
VIAddVersionKey /LANG=1033 "FileVersion" "0.6.3.0"
VIAddVersionKey /LANG=1033 "FileDescription" "Snip Studio Setup"
VIAddVersionKey /LANG=1033 "CompanyName" "Automation Forge LLC"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Automation Forge LLC"

!define MUI_ICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_UNICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install Snip Studio"
!define MUI_WELCOMEPAGE_TEXT "Capture, annotate, copy text, and record your screen.$\r$\n$\r$\nThis installer installs Snip Studio for your Windows account. It does not start the app. Capture shortcuts at Windows sign-in are optional and off unless you select them.$\r$\n$\r$\nYour screenshots and preferences stay in their current locations."
!define MUI_FINISHPAGE_TITLE "Snip Studio is installed"
!define MUI_FINISHPAGE_TEXT "Open Snip Studio from the Start menu when you are ready.$\r$\n$\r$\nManage automatic saves and folder locations in Settings."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

Function .onInit
  SetShellVarContext current
  StrCpy $PreflightCheckOnly 0
  StrCpy $PreflightProbeOnly 0
  StrCpy $PreflightCloseApproved 0
  ${GetParameters} $0
  ClearErrors
  ${GetOptions} $0 "/CHECKREADY" $1
  ${IfNot} ${Errors}
    ; This diagnostic switch always exits here, even without /S. It runs the
    ; exact same resource-use check as installation without opening the wizard
    ; or modifying application files, preferences, shortcuts, or registry keys.
    StrCpy $PreflightCheckOnly 1
    Call CheckInstalledApplication
    SetErrorLevel 0
    Quit
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/CHECKBROKER" $1
  ${IfNot} ${Errors}
    ; Read-only native diagnostic. It shares discovery and the capability probe
    ; with interactive updates, never grants consent and always exits before UI
    ; or an installation section. /CHECKREADY above retains precedence.
    StrCpy $PreflightProbeOnly 1
    Call RequestInstalledApplicationExit
    SetErrorLevel $InstalledProbeExitCode
    Quit
  ${EndIf}
  ClearErrors
  ${GetOptions} $0 "/CHECKCLOSE" $1
  ${IfNot} ${Errors}
    ; Interactive close-only acceptance check. Use the exact normal file-use
    ; query and Yes/No/Cancel prompt; no close approval is granted by this mode.
    ; Both completion and refusal exit before wizard or installation sections.
    IfSilent init_check_close_silent
    Call CheckInstalledApplication
    SetErrorLevel 0
    Quit
init_check_close_silent:
    SetErrorLevel 3
    Quit
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_OK|MB_ICONSTOP "Snip Studio requires Windows 10 or Windows 11."
    Abort
  ${EndIf}
  ClearErrors
  ReadRegDWORD $0 HKLM "SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" "Release"
  ${If} ${Errors}
  ${OrIf} $0 < 528040
    MessageBox MB_OK|MB_ICONSTOP "Snip Studio requires Microsoft .NET Framework 4.8 or later. Install it through Windows, then run this installer again."
    Abort
  ${EndIf}
FunctionEnd

; Both helpers are instantiated for Setup and Uninstall. Closing is limited
; to an exact installed executable, and is reachable only after interactive
; consent. The resource-use session below is never used for shutdown: a file
; could also be held by Explorer, an editor, or an unrelated Node process.
!macro DefineInstalledApplicationClose Prefix
Function ${Prefix}RequestInstalledApplicationExit
  System::Store "s"
  StrCpy $InstalledCloseResult 0
  StrCpy $InstalledProbeExitCode 20 ; No matching broker caption found.
  StrCpy $InstalledCloseDetail "A running Snip Studio from this installation could not be identified. No other program was closed."
  StrCpy $3 0 ; Owned process handle.
  StrCpy $7 0 ; Owned GetProcessTimes buffer.
  StrCpy $R0 0 ; Owned RM_UNIQUE_PROCESS buffer.
  StrCpy $R1 0 ; Owned Restart Manager key buffer.
  StrCpy $R3 0 ; Whether the separate process-only RM session started.
  StrCmp $PreflightCheckOnly 1 close_done
  StrCmp $PreflightProbeOnly 1 close_discovery
  IfSilent close_done
  StrCmp $PreflightCloseApproved 1 0 close_done
close_discovery:
  GetFullPathName $InstalledClosePath "$INSTDIR\Snip Studio.exe"
  StrCpy $InstalledCloseScanCount 0
  StrCpy $0 0

close_find_broker:
  ; Message-only brokers are on this desktop. Bound discovery rather than
  ; enumerating/closing arbitrary same-named executables or other sessions.
  IntOp $InstalledCloseScanCount $InstalledCloseScanCount + 1
  IntCmp $InstalledCloseScanCount 512 close_find_limit 0 close_find_limit
  System::Call '$SYSDIR\user32.dll::FindWindowExW(p -3, p r0, p 0, p 0) p.r0'
  StrCmp $0 0 close_done
  StrCmp $0 "error" close_find_error
  System::Call '$SYSDIR\user32.dll::GetWindowTextW(p r0, w .r1, i ${NSIS_MAX_STRLEN}) i.r4'
  StrLen $4 "AutomationForge.SnipStudio.ShortcutHost.v1."
  StrCpy $1 $1 $4
  StrCmp $1 "AutomationForge.SnipStudio.ShortcutHost.v1." 0 close_find_broker
  StrCpy $InstalledProbeExitCode 22 ; Caption matched, PID unavailable.
  System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 r2) i.r4'
  StrCmp $4 0 close_find_broker
  StrCmp $4 "error" close_find_broker
  StrCmp $2 0 close_find_broker
  StrCpy $InstalledProbeExitCode 23 ; Cannot open/query the identified process.
  ; QUERY_LIMITED_INFORMATION | SYNCHRONIZE. No terminate or write access.
  System::Call '$SYSDIR\kernel32.dll::OpenProcess(i 0x101000, i 0, i r2) p.r3'
  StrCmp $3 0 close_find_broker
  StrCmp $3 "error" close_find_broker
  System::Call '$SYSDIR\kernel32.dll::QueryFullProcessImageNameW(p r3, i 0, w .r1, *i ${NSIS_MAX_STRLEN} r6) i.r4'
  StrCmp $4 1 0 close_skip_process
  StrCpy $InstalledProbeExitCode 24 ; Queried executable differs from INSTDIR.
  ; NSIS StrCmp is case-insensitive, like ordinary Windows executable paths.
  StrCmp $1 $InstalledClosePath 0 close_skip_process
  StrCpy $InstalledProbeExitCode 25 ; Process exited or broker ownership changed.
  System::Call '$SYSDIR\kernel32.dll::GetExitCodeProcess(p r3, *i 0 r5) i.r4'
  StrCmp $4 1 0 close_done
  StrCmp $5 259 0 close_done ; STILL_ACTIVE
  System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 R6) i.R7'
  StrCmp $R7 0 close_done
  StrCmp $R7 "error" close_done
  StrCmp $R6 $2 0 close_done

  ; Probe support before sending a close request. The probe does not queue
  ; an exit. An unresponsive/uncertain probe never falls back to another close.
  StrCpy $InstalledProbeExitCode 26 ; The fixed message could not be registered.
  System::Call '$SYSDIR\user32.dll::RegisterWindowMessageW(w "AutomationForge.SnipStudio.RequestInstallerExit.v1") i.R4'
  StrCmp $R4 0 close_done
  StrCmp $R4 "error" close_done
  StrCpy $InstalledProbeExitCode 27 ; Capability delivery failed or timed out.
  System::Call '$SYSDIR\user32.dll::SendMessageTimeoutW(p r0, i R4, p 0x5355, p 1, i 0x23, i 1000, *p 0 R8) p.R7'
  StrCmp $R7 0 close_probe_failed
  StrCmp $R7 "error" close_probe_failed
  StrCpy $InstalledProbeExitCode 28 ; Delivered, but unsupported/nonmatching ACK.
  StrCmp $PreflightProbeOnly 1 close_probe_only_result
  StrCmp $R8 0x5355 close_cooperative
  StrCmp $R8 21333 close_cooperative
  Goto close_legacy

close_find_error:
  StrCpy $InstalledProbeExitCode 21
  Goto close_done

close_find_limit:
  StrCpy $InstalledProbeExitCode 29
  Goto close_done

close_probe_only_result:
  ; This is the only post-probe diagnostic path. It never enters a close,
  ; foreground, legacy shutdown or wait path, regardless of the ACK value.
  StrCmp $R8 0x5355 close_probe_only_ack
  StrCmp $R8 21333 close_probe_only_ack close_done
close_probe_only_ack:
  StrCpy $InstalledProbeExitCode 0
  Goto close_done

close_skip_process:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
  StrCpy $3 0
  Goto close_find_broker

close_probe_failed:
  StrCpy $InstalledCloseResult 3
  StrCpy $InstalledCloseDetail "Snip Studio did not respond to the close capability check. Complete any open dialog, or choose Exit Snip Studio from its tray menu."
  Goto close_done

close_cooperative:
  StrCmp $PreflightProbeOnly 1 close_done
  StrCmp $PreflightCloseApproved 1 0 close_done
  ; Hold the process handle across both requests, and pin the broker HWND's
  ; process again so a destroyed/reused window cannot receive the close.
  System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 R6) i.R7'
  StrCmp $R7 0 close_done
  StrCmp $R7 "error" close_done
  StrCmp $R6 $2 0 close_done
  System::Call '$SYSDIR\kernel32.dll::GetExitCodeProcess(p r3, *i 0 R6) i.R7'
  StrCmp $R7 1 0 close_done
  StrCmp $R6 259 0 close_done
  System::Call '$SYSDIR\user32.dll::AllowSetForegroundWindow(i r2) i.R7'
  System::Call '$SYSDIR\user32.dll::SendMessageTimeoutW(p r0, i R4, p 0x5355, p 0, i 0x23, i 1000, *p 0 R8) p.R7'
  StrCpy $InstalledCloseResult 3
  StrCpy $InstalledCloseDetail "The close request could not be confirmed. Complete any Snip Studio save or recording prompts before trying again."
  StrCmp $R7 0 close_done
  StrCmp $R7 "error" close_done
  StrCmp $R8 0x5355 close_cooperative_accepted
  StrCmp $R8 21333 close_cooperative_accepted close_done
close_cooperative_accepted:
  StrCpy $InstalledCloseResult 1
  StrCpy $InstalledCloseDetail "Snip Studio is still open. Complete its save or recording prompts. If you canceled closing, Setup will not change its files."
  Goto close_wait

close_legacy:
  StrCmp $PreflightProbeOnly 1 close_done
  StrCmp $PreflightCloseApproved 1 0 close_done
  ; Older releases do not understand the app's graceful-exit protocol. Use
  ; a NEW RM session containing only this validated PID and creation time.
  ; Never register files/services here, and never use RmForceShutdown.
  StrCpy $InstalledCloseResult 3
  StrCpy $InstalledCloseDetail "This older Snip Studio could not finish closing automatically. Complete its save prompts, then use Exit Snip Studio from its tray menu if needed."
  System::Alloc 32
  Pop $7
  StrCmp $7 0 close_done
  StrCmp $7 "error" close_done
  IntOp $R6 $7 + 8
  IntOp $R7 $7 + 16
  IntOp $R8 $7 + 24
  System::Call '$SYSDIR\kernel32.dll::GetProcessTimes(p r3, p r7, p R6, p R7, p R8) i.r4'
  StrCmp $4 1 0 close_done
  System::Call '*$7(i .r8, i .r9)'
  ; RM_UNIQUE_PROCESS is DWORD PID + FILETIME (two DWORDs): 12 bytes.
  System::Call '*(i r2, i r8, i r9) p.R0'
  StrCmp $R0 0 close_done
  StrCmp $R0 "error" close_done
  System::Alloc 66
  Pop $R1
  StrCmp $R1 0 close_done
  StrCmp $R1 "error" close_done
  System::Call '$SYSDIR\Rstrtmgr.dll::RmStartSession(*i 0 R2, i 0, p R1) i.R5'
  StrCmp $R5 0 0 close_done
  StrCpy $R3 1
  System::Call '$SYSDIR\Rstrtmgr.dll::RmRegisterResources(i R2, i 0, p 0, i 1, p R0, i 0, p 0) i.R5'
  StrCmp $R5 0 0 close_done
  System::Call '$SYSDIR\kernel32.dll::GetExitCodeProcess(p r3, *i 0 R6) i.R7'
  StrCmp $R7 1 0 close_done
  StrCmp $R6 259 0 close_done
  System::Call '$SYSDIR\user32.dll::AllowSetForegroundWindow(i r2) i.R7'
  ; Flags zero requests ordinary, cancellable shutdown. It is not a kill.
  System::Call '$SYSDIR\Rstrtmgr.dll::RmShutdown(i R2, i 0, p 0) i.R5'
  StrCmp $R5 0 0 close_done
  StrCpy $InstalledCloseResult 2

close_wait:
  StrCmp $PreflightProbeOnly 1 close_done
  StrCmp $PreflightCloseApproved 1 0 close_done
  ; Poll for at most 15 seconds after the request/API call returns. Windows
  ; controls any preceding RmShutdown duration. The final file-use query is
  ; authoritative even after the process exits.
  StrCpy $6 0
close_wait_loop:
  System::Call '$SYSDIR\kernel32.dll::WaitForSingleObject(p r3, i 0) i.r4'
  StrCmp $4 0 close_done
  StrCmp $4 258 0 close_done
  IntCmp $6 60 close_done 0 close_done
  Sleep 250
  IntOp $6 $6 + 1
  Goto close_wait_loop

close_done:
  ${If} $R3 == 1
    System::Call '$SYSDIR\Rstrtmgr.dll::RmEndSession(i R2) i.R5'
  ${EndIf}
  ${If} $R1 != 0
  ${AndIf} $R1 != "error"
    System::Free $R1
  ${EndIf}
  ${If} $R0 != 0
  ${AndIf} $R0 != "error"
    System::Free $R0
  ${EndIf}
  ${If} $7 != 0
  ${AndIf} $7 != "error"
    System::Free $7
  ${EndIf}
  ${If} $3 != 0
  ${AndIf} $3 != "error"
    System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
  ${EndIf}
  System::Store "l"
FunctionEnd
!macroend
!insertmacro DefineInstalledApplicationClose ""
!insertmacro DefineInstalledApplicationClose "un."

; Query only executables in this installation. A portable copy elsewhere must
; not block an update. This resource session only identifies use. Keep this
; check before every install/uninstall file or registry mutation.
!macro DefineInstalledApplicationPreflight Prefix ActionText
Function ${Prefix}CheckInstalledApplication
  System::Store "s"
  StrCpy $PreflightCloseApproved 0
preflight_retry:
  IfFileExists "$INSTDIR\Snip Studio.exe" preflight_has_resource
  IfFileExists "$INSTDIR\translation\runtime\node.exe" 0 preflight_ready
preflight_has_resource:
  StrCpy $0 0
  StrCpy $1 0
  StrCpy $2 0
  StrCpy $3 0
  StrCpy $4 14
  StrCpy $5 0
  StrCpy $6 0
  StrCpy $7 0
  StrCpy $8 "Windows could not allocate memory to check the installed application."
  StrCpy $9 0
  ; RmStartSession requires CCH_RM_SESSION_KEY + 1 WCHARs (33 * 2).
  System::Alloc 66
  Pop $1
  StrCmp $1 0 preflight_cleanup
  StrCpy $8 "Windows Restart Manager could not start its file-use check."
  ; NSIS 3.04 requires an unquoted module path. Quoted module names were
  ; introduced in 3.05; retain the absolute system path for DLL loading.
  System::Call '$SYSDIR\Rstrtmgr.dll::RmStartSession(*i 0 r0, i 0, p r1) i.r4'
  StrCmp $4 0 0 preflight_cleanup
  StrCpy $9 1
  StrCpy $4 14
  StrCpy $8 "Windows could not allocate memory to check the installed application."
  ; Use an owned, Unicode path buffer and a one-element LPCWSTR array.
  System::Call '*(&w${NSIS_MAX_STRLEN} "$INSTDIR\Snip Studio.exe") p.r2'
  StrCmp $2 0 preflight_cleanup
  StrCmp $2 "error" preflight_cleanup
  System::Call '*(p r2) p.r3'
  StrCmp $3 0 preflight_cleanup
  StrCmp $3 "error" preflight_cleanup
  IfFileExists "$INSTDIR\Snip Studio.exe" 0 preflight_translation
  StrCpy $8 "Windows Restart Manager could not register the installed application for its file-use check."
  System::Call '$SYSDIR\Rstrtmgr.dll::RmRegisterResources(i r0, i 1, p r3, i 0, p 0, i 0, p 0) i.r4'
  StrCmp $4 0 0 preflight_cleanup
preflight_translation:
  IfFileExists "$INSTDIR\translation\runtime\node.exe" 0 preflight_query
  ; Registration completes before reusing the same owned path buffer.
  System::Call '*$2(&w${NSIS_MAX_STRLEN} "$INSTDIR\translation\runtime\node.exe")'
  StrCpy $8 "Windows Restart Manager could not register the installed translation runtime for its file-use check."
  System::Call '$SYSDIR\Rstrtmgr.dll::RmRegisterResources(i r0, i 1, p r3, i 0, p 0, i 0, p 0) i.r4'
  StrCmp $4 0 0 preflight_cleanup
preflight_query:
  StrCpy $8 "Windows Restart Manager could not determine whether the installed application is in use."
  ; A zero-capacity query is enough: ERROR_MORE_DATA (234) or a positive
  ; required count means a process is using this exact installed executable.
  System::Call '$SYSDIR\Rstrtmgr.dll::RmGetList(i r0, *i 0 r5, *i 0 r6, p 0, *i 0 r7) i.r4'
preflight_cleanup:
  ${If} $9 == 1
    System::Call '$SYSDIR\Rstrtmgr.dll::RmEndSession(i r0) i.r9'
    ${If} $9 != 0
      StrCpy $4 $9
      StrCpy $5 0
      StrCpy $6 0
      StrCpy $8 "Windows Restart Manager could not complete its file-use check."
    ${EndIf}
  ${EndIf}
  ${If} $3 != 0
  ${AndIf} $3 != "error"
    System::Free $3
  ${EndIf}
  ${If} $2 != 0
  ${AndIf} $2 != "error"
    System::Free $2
  ${EndIf}
  ${If} $1 != 0
    System::Free $1
  ${EndIf}
  ${If} $4 == 0
  ${AndIf} $5 == 0
  ${AndIf} $6 == 0
    Goto preflight_ready
  ${EndIf}
  StrCmp $PreflightCheckOnly 1 preflight_cancel
  IfSilent preflight_cancel
  ${If} $4 == 234
  ${OrIf} $5 > 0
  ${OrIf} $6 > 0
    StrCmp $PreflightCloseApproved 1 preflight_close_pending
    MessageBox MB_YESNOCANCEL|MB_ICONQUESTION|MB_DEFBUTTON2 "Snip Studio files are still in use from this installation:$\r$\n$INSTDIR$\r$\n$\r$\nAllow Setup to close Snip Studio before ${ActionText}?$\r$\n$\r$\nSave prompts may appear. Any recording must finish saving. No or Cancel stops Setup without changing files; Setup never force-closes the program." /SD IDNO IDYES preflight_request_close
    Goto preflight_cancel
preflight_request_close:
    StrCpy $PreflightCloseApproved 1
    Call ${Prefix}RequestInstalledApplicationExit
    ; Always recheck the original file resources, even after an ACK or a
    ; successful normal shutdown. No bytes have been replaced at this point.
    Goto preflight_retry
preflight_close_pending:
    StrCpy $PreflightCloseApproved 0
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$InstalledCloseDetail$\r$\n$\r$\nThe files are still in use. Choose Retry after closing Snip Studio, or Cancel to leave this installation unchanged. Other programs holding these files are never closed automatically." /SD IDCANCEL IDRETRY preflight_retry
  ${Else}
    StrCpy $7 "Windows returned status code $4."
    ${If} $4 == "error"
      StrCpy $7 "The installer could not call the Windows file-use API. This is an installer call failure, not a confirmed file lock."
    ${EndIf}
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "$8$\r$\n$\r$\n$7$\r$\n$\r$\nChoose Retry to check again. If this continues, choose Cancel and report this message.$\r$\n$\r$\nNo installation files have been changed." /SD IDCANCEL IDRETRY preflight_retry
  ${EndIf}
preflight_cancel:
  ${If} $4 == 234
  ${OrIf} $5 > 0
  ${OrIf} $6 > 0
    SetErrorLevel 2
  ${Else}
    SetErrorLevel 3
  ${EndIf}
  Quit
preflight_ready:
  System::Store "l"
FunctionEnd
!macroend
!insertmacro DefineInstalledApplicationPreflight "" "updating this installation"
!insertmacro DefineInstalledApplicationPreflight "un." "removing this installation"

Section "Snip Studio (required)" MainApplication
  SectionIn RO
  SetShellVarContext current
  Call CheckInstalledApplication
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${RELEASE_DIR}\Snip Studio.exe"
  File "${RELEASE_DIR}\Snip Studio.exe.config"
  File "${RELEASE_DIR}\README.txt"
  File "${RELEASE_DIR}\VALIDATION.txt"
  File "${RELEASE_DIR}\Signature Verification.json"
  File "${RELEASE_DIR}\SHA256SUMS.txt"
  SetOutPath "$INSTDIR\licenses"
  File "${RELEASE_DIR}\licenses\NOTICES.txt"
  File "${RELEASE_DIR}\licenses\FluentEmoji-LICENSE.txt"
  File "${RELEASE_DIR}\licenses\FluentEmoji-ATTRIBUTION.txt"
  File "${RELEASE_DIR}\licenses\FluentEmoji-manifest.json"
  SetOutPath "$INSTDIR"
  !include "${TRANSLATION_INSTALL_INCLUDE}"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Automation Forge\Snip Studio"
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Snip Studio.lnk" "$INSTDIR\Snip Studio.exe" "" "$INSTDIR\Snip Studio.exe" 0
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Uninstall Snip Studio.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\Automation Forge\Snip Studio" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayName" "Snip Studio"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayVersion" "0.6.3"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "Publisher" "Automation Forge LLC"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayIcon" "$INSTDIR\Snip Studio.exe,0"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "URLInfoAbout" "https://automation-forge.com/"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "NoRepair" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "EstimatedSize" ${PACKAGE_KB}
SectionEnd

Section /o "Desktop shortcut" DesktopShortcut
  SetShellVarContext current
  CreateShortcut "$DESKTOP\Snip Studio.lnk" "$INSTDIR\Snip Studio.exe" "" "$INSTDIR\Snip Studio.exe" 0
SectionEnd

Section /o "Start Snip Studio at Windows sign-in" StartupShortcuts
  SetShellVarContext current
  ; Explicit opt-in only. Do not launch the program during installation.
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Snip Studio - Automation Forge" '$\"$INSTDIR\Snip Studio.exe$\" --background'
SectionEnd

LangString DESC_MainApplication ${LANG_ENGLISH} "Install the application, help files, Start menu shortcut, and uninstaller."
LangString DESC_DesktopShortcut ${LANG_ENGLISH} "Add a shortcut to your desktop. This is optional."
LangString DESC_StartupShortcuts ${LANG_ENGLISH} "Start Snip Studio when you sign in, using your saved background preferences. With default preferences only the small shortcut listener starts. This optional component is unchecked by default and does not launch the app now."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${MainApplication} $(DESC_MainApplication)
  !insertmacro MUI_DESCRIPTION_TEXT ${DesktopShortcut} $(DESC_DesktopShortcut)
  !insertmacro MUI_DESCRIPTION_TEXT ${StartupShortcuts} $(DESC_StartupShortcuts)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetShellVarContext current
  StrCpy $PreflightCheckOnly 0
  StrCpy $PreflightProbeOnly 0
  StrCpy $PreflightCloseApproved 0
  ${un.GetParameters} $0
  ClearErrors
  ${un.GetOptions} $0 "/CHECKREADY" $1
  ${IfNot} ${Errors}
    StrCpy $PreflightCheckOnly 1
    Call un.CheckInstalledApplication
    SetErrorLevel 0
    Quit
  ${EndIf}
  ClearErrors
  ${un.GetOptions} $0 "/CHECKBROKER" $1
  ${IfNot} ${Errors}
    StrCpy $PreflightProbeOnly 1
    Call un.RequestInstalledApplicationExit
    SetErrorLevel $InstalledProbeExitCode
    Quit
  ${EndIf}
  ClearErrors
  ${un.GetOptions} $0 "/CHECKCLOSE" $1
  ${IfNot} ${Errors}
    IfSilent un_init_check_close_silent
    Call un.CheckInstalledApplication
    SetErrorLevel 0
    Quit
un_init_check_close_silent:
    SetErrorLevel 3
    Quit
  ${EndIf}
  ClearErrors
FunctionEnd

Function un.RemoveImageFileRegistration
  ; Startup registers only a non-default Open with handler. Remove it only if
  ; this exact executable still owns the command; retain a portable copy's entry.
  ReadRegStr $0 HKCU "Software\Classes\AutomationForge.SnipStudio.Image\shell\open\command" ""
  ${If} $0 == '$\"$INSTDIR\Snip Studio.exe$\" $\"%1$\"'
    DeleteRegValue HKCU "Software\Classes\.png\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.jpg\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.jpeg\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.bmp\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.gif\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.tif\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegValue HKCU "Software\Classes\.tiff\OpenWithProgids" "AutomationForge.SnipStudio.Image"
    DeleteRegKey HKCU "Software\Classes\AutomationForge.SnipStudio.Image"
    System::Call '$SYSDIR\shell32.dll::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
  ${EndIf}
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Call un.CheckInstalledApplication
  Call un.RemoveImageFileRegistration
  ; This generated list deletes only files shipped in this exact bundle and
  ; removes only empty known subdirectories. User additions are retained.
  !include "${TRANSLATION_UNINSTALL_INCLUDE}"
  ; Remove only this installation's opt-in startup command. Another portable
  ; copy may own the same product preference and must keep its startup entry.
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Snip Studio - Automation Forge"
  ${If} $0 == '$\"$INSTDIR\Snip Studio.exe$\" --background'
    DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Snip Studio - Automation Forge"
  ${EndIf}
  Delete "$INSTDIR\Snip Studio.exe"
  Delete "$INSTDIR\Snip Studio.exe.config"
  Delete "$INSTDIR\README.txt"
  Delete "$INSTDIR\VALIDATION.txt"
  Delete "$INSTDIR\Signature Verification.json"
  Delete "$INSTDIR\SHA256SUMS.txt"
  Delete "$INSTDIR\licenses\NOTICES.txt"
  Delete "$INSTDIR\licenses\FluentEmoji-LICENSE.txt"
  Delete "$INSTDIR\licenses\FluentEmoji-ATTRIBUTION.txt"
  Delete "$INSTDIR\licenses\FluentEmoji-manifest.json"
  RMDir "$INSTDIR\licenses"
  Delete "$INSTDIR\Uninstall.exe"
  Delete "$DESKTOP\Snip Studio.lnk"
  Delete "$SMPROGRAMS\Automation Forge\Snip Studio\Snip Studio.lnk"
  Delete "$SMPROGRAMS\Automation Forge\Snip Studio\Uninstall Snip Studio.lnk"
  RMDir "$SMPROGRAMS\Automation Forge\Snip Studio"
  RMDir "$SMPROGRAMS\Automation Forge"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio"
  DeleteRegValue HKCU "Software\Automation Forge\Snip Studio" "InstallLocation"
  DeleteRegKey /ifempty HKCU "Software\Automation Forge\Snip Studio"
  DeleteRegKey /ifempty HKCU "Software\Automation Forge"
  RMDir "$INSTDIR"
  ; No recursive deletion: user preferences, snips, recordings, working copies,
  ; and any additional files in the install folder are deliberately retained.
SectionEnd
