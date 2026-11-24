; Compile through the signed preview packaging gate. The generated payload
; includes enumerate the same verified payload; no wildcard copying/removal.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"
!include "FileFunc.nsh"

!ifndef APP_VERSION
!define APP_VERSION "0.7.0-preview.30"
!endif
!ifndef APP_VERSION_NUMERIC
!define APP_VERSION_NUMERIC "0.7.0.30"
!endif
!ifdef SNIP_STUDIO_STABLE
  !define APP_NAME "Snip Studio"
  !define APP_EXE "Snip Studio.exe"
  !define APP_KEY "Software\Automation Forge\Snip Studio"
  !define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio"
  !define APP_SETUP_NAME "SnipStudio"
  !define APP_RELEASE_URL "https://github.com/jaydawg473/Snip-Studio"
  !define APP_STARTUP_VALUE "Snip Studio - Automation Forge"
  !define PREVIEW_EXIT_MESSAGE "AutomationForge.SnipStudio.RequestInstallerExit.v1"
  !define PREVIEW_DEPLOYMENT_MUTEX "Global\AutomationForge.SnipStudio.Deployment.v1"
  !define PREVIEW_TRANSACTION_FOLDER ".SnipStudio-transaction"
  !ifndef LEGACY_OVERLAP_INCLUDE
    !error "The stable installer requires an authenticated predecessor overlap catalog."
  !endif
!else
  !define APP_NAME "Snip Studio WinUI 3 Preview"
  !define APP_EXE "SnipStudio.WinUI3.Preview.exe"
  !define APP_KEY "Software\Automation Forge\Snip Studio WinUI 3 Preview"
  !define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio.WinUI3.Preview"
  !define APP_SETUP_NAME "SnipStudio-WinUI3"
  !define APP_RELEASE_URL "https://github.com/jaydawg473/Snip-Studio-WinUI3"
  !define APP_STARTUP_VALUE "Snip Studio WinUI3 Preview - Automation Forge"
  !define PREVIEW_EXIT_MESSAGE "AutomationForge.SnipStudio.WinUI3.RequestInstallerExit.v1"
  !define PREVIEW_DEPLOYMENT_MUTEX "Global\AutomationForge.SnipStudio.WinUI3.Deployment.v1"
  !define PREVIEW_TRANSACTION_FOLDER ".SnipStudio-WinUI3-transaction"
!endif
!define PREVIEW_WINDOW_CLASS "WinUIDesktopWin32WindowClass"
!define PREVIEW_EXIT_COOKIE 21333
!define PREVIEW_CLOSE_TIMEOUT_MS 30000

Var PreviewClosePath
Var PreviewCloseWindow
Var PreviewClosePid
Var PreviewCloseProcess
Var PreviewCloseLegacyScan
Var PreviewLegacyCloseAllowed
Var PreviewCloseObserved
Var PreviewCloseProbeFailed
Var PreviewCloseStarted
Var PreviewCloseMessage
Var PreviewCloseDetail
Var PreviewInstallRoot
Var PreviewTransactionRoot
Var PreviewStageRoot
Var PreviewBackupRoot
Var PreviewRollbackFailed
Var PreviewStageCleanupFailed
Var PreviewHoldExecutable
Var PreviewRetireReleaseFiles
Var PreviewDestinationOwned
!ifdef SNIP_STUDIO_STABLE
Var StableLegacyUpgrade
Var StableLegacyBrokerCaption
Var StableOriginalMoved
Var StableUninstallerSHA256
Var StableRegistryBackup
Var StableRegistryCaptured
Var StableRegistryWritten
Var StableRegistrationActive
Var StableRegistrationRecovered
Var StableStartLinkCreated
Var StableRemoveLinkCreated
Var StableStartLinkSHA256
Var StableRemoveLinkSHA256
!endif

!ifndef PAYLOAD_DIR
  !error "The frozen signed self-contained payload directory is required."
!endif
!ifndef OUTPUT_DIR
  !error "A separate preview installer output directory is required."
!endif
!ifndef PACKAGE_KB
  !error "The verified payload size is required."
!endif
!ifndef PAYLOAD_INSTALL_INCLUDE
  !error "The exact-file install include is required."
!endif
!ifndef PAYLOAD_UNINSTALL_INCLUDE
  !error "The exact-file removal include is required."
!endif
!ifndef PAYLOAD_PREFLIGHT_INCLUDE
  !error "The exact-file in-use preflight include is required."
!endif
!ifndef RETIRED_PAYLOAD_INCLUDE
  !error "The verified prior-release retirement include is required, even when empty."
!endif
!ifndef APP_SHA256
  !error "The selected signed application hash is required."
!endif
!ifndef PREVIOUS_APP_SHA256
  !error "The verified prior preview launcher hash is required."
!endif
!ifndef PAYLOAD_MANIFEST_SHA256
  !error "The selected payload manifest hash is required."
!endif
!ifndef SOURCE_FREEZE_SHA256
  !error "The selected compilation source freeze hash is required."
!endif
!ifndef UNINSTALL_SIGN_HOST
  !error "A verified timestamp-signed PowerShell host is required."
!endif
!ifndef UNINSTALL_SIGN_SCRIPT
  !error "The verified timestamp-signed preview uninstaller adapter is required."
!endif
!ifndef UNINSTALL_SIGN_TEMP_ROOT
  !error "The bounded compiler temporary directory is required."
!endif

; NSIS 3.13 supplies a complete PE to this hook, then embeds the signed bytes.
; The adapter must fail closed and retain the signed full image plus receipt.
!uninstfinalize '"${UNINSTALL_SIGN_HOST}" -NoProfile -NonInteractive -File "${UNINSTALL_SIGN_SCRIPT}" -ArtifactPath "%1" -ReleaseVersion "${APP_VERSION}" -ExpectedFileVersion "${APP_VERSION_NUMERIC}" -ApplicationPath "${PAYLOAD_DIR}\${APP_EXE}" -ExpectedApplicationSHA256 "${APP_SHA256}" -ExpectedPayloadManifestSHA256 "${PAYLOAD_MANIFEST_SHA256}" -ExpectedSourceFreezeSHA256 "${SOURCE_FREEZE_SHA256}" -ExpectedTemporaryRoot "${UNINSTALL_SIGN_TEMP_ROOT}"' = 0

Name "${APP_NAME}"
OutFile "${OUTPUT_DIR}\${APP_SETUP_NAME}-${APP_VERSION}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Automation Forge\${APP_NAME}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
CRCCheck force
SetCompressor zlib
ShowInstDetails show
ShowUninstDetails show
BrandingText "Automation Forge"
VIProductVersion "${APP_VERSION_NUMERIC}"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION_NUMERIC}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=1033 "CompanyName" "Automation Forge LLC"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Automation Forge LLC"

!define MUI_ICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_UNICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install ${APP_NAME}"
!ifdef SNIP_STUDIO_STABLE
  !define MUI_WELCOMEPAGE_TEXT "Install ${APP_NAME} ${APP_VERSION} for your Windows account.$\r$\n$\r$\nIf you already have Snip Studio, use its existing installation folder. Open the application from the Start menu after installation."
!else
  !define MUI_WELCOMEPAGE_TEXT "Install the WinUI 3 preview for your Windows account.$\r$\n$\r$\nPreview version: ${APP_VERSION}.$\r$\n$\r$\nOpen the application from the Start menu after installation."
!endif
!define MUI_FINISHPAGE_TITLE "${APP_NAME} is installed"
!define MUI_FINISHPAGE_TEXT "Open ${APP_NAME} from the Start menu when you are ready."
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!define MUI_PAGE_CUSTOMFUNCTION_LEAVE CheckPreviewDestination
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!macro DefinePreviewDeploymentLock Prefix
Function ${Prefix}AcquirePreviewDeploymentLock
  Push $0
  Push $1
  System::Call '$SYSDIR\kernel32.dll::CreateMutexW(p 0, i 0, w "${PREVIEW_DEPLOYMENT_MUTEX}") p.r0 ?e'
  Pop $1
  StrCmp $0 0 deployment_lock_failed
  StrCmp $0 "error" deployment_lock_failed
  StrCmp $1 183 0 deployment_lock_ok ; ERROR_ALREADY_EXISTS
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r0) i'
deployment_lock_failed:
  DetailPrint "The installation lock is unavailable (Windows error $1)."
  Pop $1
  Pop $0
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Close other ${APP_NAME} installers and uninstallers on this PC, including in other signed-in Windows sessions, then try again. If none are open, ask your Windows administrator to check permissions."
  SetErrorLevel 2
  Abort
deployment_lock_ok:
  ; Retain the handle until the wizard exits. The OS also closes it on failure.
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro DefinePreviewDeploymentLock ""
!insertmacro DefinePreviewDeploymentLock "un."

!macro DefinePreviewPathCheck Prefix
Function ${Prefix}IsSafePreviewPath
  Exch $0
  Push $1
  Push $2
  Push $3
  ; Check each existing component before creating, moving or deleting files.
preview_path_component:
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r1 ?e'
  Pop $2
  StrCmp $1 "error" preview_path_unsafe
  StrCmp $1 -1 preview_path_missing
  IntOp $2 $1 & 0x400 ; FILE_ATTRIBUTE_REPARSE_POINT
  StrCmp $2 0 preview_path_parent preview_path_unsafe
preview_path_missing:
  StrCmp $2 2 preview_path_parent ; ERROR_FILE_NOT_FOUND
  StrCmp $2 3 0 preview_path_unsafe ; ERROR_PATH_NOT_FOUND
preview_path_parent:
  !if "${Prefix}" == ""
    ${GetParent} "$0" $3
  !else
    ${un.GetParent} "$0" $3
  !endif
  StrCmp $3 "" preview_path_safe
  StrCmp $3 $0 preview_path_safe
  StrCpy $0 $3
  StrLen $1 $0
  StrCmp $1 2 0 preview_path_component
  StrCpy $1 $0 1 1
  StrCmp $1 ":" 0 preview_path_component
  StrCpy $0 "$0\"
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r1'
  StrCmp $1 "error" preview_path_unsafe
  StrCmp $1 -1 preview_path_unsafe
  IntOp $2 $1 & 0x400
  StrCmp $2 0 preview_path_safe preview_path_unsafe
preview_path_unsafe:
  StrCpy $0 0
  Goto preview_path_return
preview_path_safe:
  StrCpy $0 1
preview_path_return:
  Pop $3
  Pop $2
  Pop $1
  Exch $0
FunctionEnd
!macroend
!insertmacro DefinePreviewPathCheck ""
!insertmacro DefinePreviewPathCheck "un."

Function .onInit
  SetShellVarContext current
  SetRegView 64
  ${IfNot} ${AtLeastWin10}
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "${APP_NAME} requires Windows 10 or Windows 11."
    SetErrorLevel 2
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastBuild} 19041
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "${APP_NAME} requires Windows build 19041 or later."
    SetErrorLevel 2
    Abort
  ${EndIf}
  !ifdef SNIP_STUDIO_STABLE
    ${IfNot} ${IsNativeAMD64}
      IfSilent +2
        MessageBox MB_OK|MB_ICONEXCLAMATION "This Snip Studio release requires an x64 Windows PC. The existing installation has been kept."
      SetErrorLevel 2
      Abort
    ${EndIf}
  !else
  ${IfNot} ${RunningX64}
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "This ${APP_NAME} installer contains the x64 application."
    SetErrorLevel 2
    Abort
  ${EndIf}
  !endif
  Call AcquirePreviewDeploymentLock
FunctionEnd

; A selected folder must be empty or contain this preview's own application.
; This avoids placing preview runtime files into another application's folder.
Function CheckPreviewDestination
  Push $0
  Push $1
  StrCpy $PreviewDestinationOwned 0
  ClearErrors
  GetFullPathName $INSTDIR "$INSTDIR"
  IfErrors destination_unsafe
  StrCpy $0 $INSTDIR 2 1
  StrCmp $0 ":\" 0 destination_unsafe
  StrLen $0 $INSTDIR
  IntCmp $0 3 destination_unsafe destination_unsafe 0
  Push "$INSTDIR"
  Call IsSafePreviewPath
  Pop $0
  StrCmp $0 1 destination_recovery_check
destination_unsafe:
  Pop $1
  Pop $0
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Choose an accessible local installation folder without directory links."
  SetErrorLevel 2
  Abort
destination_recovery_check:
  !ifdef SNIP_STUDIO_STABLE
    ; An existing registration must keep its original install path. This
    ; preserves startup commands and prevents two stable installs sharing it.
    ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"
    StrCmp $0 "" destination_registered_path_done
    StrCpy $1 $0 2 1
    StrCmp $1 ":\" 0 destination_registered_path_mismatch
    ClearErrors
    GetFullPathName $0 "$0"
    IfErrors destination_registered_path_mismatch
    StrCmp $0 $INSTDIR destination_registered_path_done
  destination_registered_path_mismatch:
    Pop $1
    Pop $0
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "Use the existing registered Snip Studio folder to upgrade this installation."
    SetErrorLevel 2
    Abort
  destination_registered_path_done:
  !endif
  StrCpy $0 "$INSTDIR\${PREVIEW_TRANSACTION_FOLDER}"
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r0 ?e'
  Pop $1
  StrCmp $0 -1 0 destination_recovery_exists
  StrCmp $1 2 destination_check_contents
  StrCmp $1 3 destination_check_contents
destination_recovery_exists:
  Pop $1
  Pop $0
  DetailPrint "An earlier Snip Studio transaction requires review at $INSTDIR\${PREVIEW_TRANSACTION_FOLDER}."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "An earlier Snip Studio installation may require recovery. Keep the files in $INSTDIR\${PREVIEW_TRANSACTION_FOLDER} and contact support before retrying."
  SetErrorLevel 2
  Abort
destination_check_contents:
  Call ReadPreviewDestinationState
  Pop $0
  StrCmp $0 "empty" destination_ok
  StrCmp $0 "occupied" 0 destination_unsafe
  Call VerifyOwnedPreviewLauncher
  Pop $0
  StrCmp $0 1 destination_owned
  Pop $1
  Pop $0
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Choose an empty folder or the registered folder containing a supported signed ${APP_NAME} release."
  SetErrorLevel 2
  Abort
destination_owned:
  StrCpy $PreviewDestinationOwned 1
destination_ok:
  Pop $1
  Pop $0
FunctionEnd

; A failed directory scan cannot establish emptiness. Only Windows' explicit
; missing-path/no-match results establish those states.
Function ReadPreviewDestinationState
  System::Store "s"
  StrCpy $0 "$INSTDIR"
  StrCpy $7 "unknown"
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r1 ?e'
  Pop $3
  StrCmp $1 "error" destination_state_done
  StrCmp $1 -1 0 destination_state_directory
  StrCmp $3 2 destination_state_empty
  StrCmp $3 3 destination_state_empty destination_state_done
destination_state_directory:
  IntOp $1 $1 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
  IntCmp $1 16 0 destination_state_done destination_state_done
  ; WIN32_FIND_DATAW has 592 bytes with cFileName starting at byte 44.
  System::Alloc 592
  Pop $1
  StrCmp $1 0 destination_state_done
  StrCmp $1 "error" destination_state_done
  StrCpy $0 "$INSTDIR\*"
  System::Call '$SYSDIR\kernel32.dll::FindFirstFileW(w r0, p r1) p.r2 ?e'
  Pop $3
  StrCmp $2 "error" destination_state_free
  StrCmp $2 0 destination_state_free
  StrCmp $2 -1 0 destination_state_entry
  ; The root was present. Only no matching entries proves an empty search.
  StrCmp $3 2 0 destination_state_free
  StrCpy $7 "empty"
  Goto destination_state_free
destination_state_entry:
  IntOp $4 $1 + 44
  System::Call '*$4(&w260 .r5)'
  StrCmp $5 "" destination_state_close
  StrCmp $5 "." destination_state_continue
  StrCmp $5 ".." destination_state_continue
  StrCpy $7 "occupied"
  Goto destination_state_close
destination_state_continue:
  System::Call '$SYSDIR\kernel32.dll::FindNextFileW(p r2, p r1) i.r6 ?e'
  Pop $3
  StrCmp $6 1 destination_state_entry
  StrCmp $6 0 0 destination_state_close
  StrCmp $3 18 0 destination_state_close ; ERROR_NO_MORE_FILES
  StrCpy $7 "empty"
destination_state_close:
  System::Call '$SYSDIR\kernel32.dll::FindClose(p r2) i'
destination_state_free:
  System::Free $1
  Goto destination_state_done
destination_state_empty:
  StrCpy $7 "empty"
destination_state_done:
  Push "$7"
  System::Store "l"
FunctionEnd

; Both registered location and exact reviewed launcher bytes are required for
; upgrading a nonempty folder. Unknown historical builds keep all their files.
Function VerifyOwnedPreviewLauncher
  System::Store "s"
  StrCpy $6 0
  !ifdef SNIP_STUDIO_STABLE
    StrCpy $StableLegacyUpgrade 0
  !endif
  ClearErrors
  ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"
  IfErrors destination_owner_done
  StrCpy $1 $0 2 1
  StrCmp $1 ":\" 0 destination_owner_done
  GetFullPathName $0 "$0"
  IfErrors destination_owner_done
  StrCmp $0 $INSTDIR 0 destination_owner_done
  Push "${APP_EXE}"
  Push "$INSTDIR"
  Call CheckPreviewRetiredPathLength
  Pop $1
  StrCmp $1 1 0 destination_owner_done
  StrCpy $0 "$INSTDIR\${APP_EXE}"
  Push "$0"
  Call IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 destination_owner_done
  ; GENERIC_READ, FILE_SHARE_READ, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT.
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r0, i 0x80000000, i 1, p 0, i 3, i 0x00200000, p 0) p.r2'
  StrCmp $2 -1 destination_owner_done
  StrCmp $2 0 destination_owner_done
  StrCmp $2 "error" destination_owner_done
  System::Alloc 8
  Pop $3
  StrCmp $3 0 destination_owner_close
  StrCmp $3 "error" destination_owner_close
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r2, i 9, p r3, i 8) i.r4'
  StrCmp $4 1 0 destination_owner_free
  System::Call '*$3(i .r4, i)'
  IntOp $4 $4 & 0x410 ; Directory and reparse points are not launcher files.
  StrCmp $4 0 0 destination_owner_free
  Push "$2"
  Call ReadPreviewRetiredSHA256
  Pop $5
  StrCmp $5 "" destination_owner_free
  StrCmp $5 "${APP_SHA256}" destination_owner_verified
  !ifdef SNIP_STUDIO_STABLE
    StrCmp $5 "${PREVIOUS_APP_SHA256}" destination_owner_verified_legacy
    StrCmp $5 "370991B1BEE29F8B3AAC29C95323438AD9B8BEA94B3859C008FE3AD38AF8CD5C" destination_owner_verified_legacy ; stable 0.6.23
    StrCmp $5 "291BFEBCA716AC8811D2CBE09F0FEBC7D3531C4303F0E3B797108E110001B81E" 0 destination_owner_free ; stable 0.6.21
  destination_owner_verified_legacy:
    StrCpy $StableLegacyUpgrade 1
  !else
    StrCmp $5 "${PREVIOUS_APP_SHA256}" destination_owner_verified
    StrCmp $5 "97F0E066B9274A85CE0DAC32034549E2A3605C1EF2ED0B9E153EBF93A724FEB7" destination_owner_verified ; preview.28
    StrCmp $5 "DA7BB6F5A47B5B70A619D05C61454DB04F3D286F8BB52FAC5F9BECABEAAA9134" destination_owner_verified ; preview.11
    StrCmp $5 "CFD202FEE2D21D0FCEFFCB85F3C53DF96A52D68C640DCA6A8AE9B660073B3A69" 0 destination_owner_free ; preview.4
  !endif
destination_owner_verified:
  StrCpy $6 1
destination_owner_free:
  System::Free $3
destination_owner_close:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r2) i'
destination_owner_done:
  Push "$6"
  System::Store "l"
FunctionEnd

!ifdef SNIP_STUDIO_STABLE
; Build the same caption as the original ShortcutHost using this Setup's
; account SID. The per-user setup does not impersonate another account.
Function ReadStableLegacyBrokerCaption
  System::Store "s"
  StrCpy $StableLegacyBrokerCaption ""
  System::Call '$SYSDIR\kernel32.dll::GetCurrentProcess() p.r0'
  System::Call '$SYSDIR\advapi32.dll::OpenProcessToken(p r0, i 0x0008, *p 0 r1) i.r2' ; TOKEN_QUERY
  StrCmp $2 1 0 stable_broker_caption_done
  System::Call '$SYSDIR\advapi32.dll::GetTokenInformation(p r1, i 1, p 0, i 0, *i 0 r2) i.r3' ; TokenUser
  IntCmpU $2 8 0 stable_broker_caption_close 0
  IntCmpU $2 4096 0 0 stable_broker_caption_close
  System::Alloc $2
  Pop $3
  StrCmp $3 0 stable_broker_caption_close
  StrCmp $3 "error" stable_broker_caption_close
  System::Call '$SYSDIR\advapi32.dll::GetTokenInformation(p r1, i 1, p r3, i r2, *i 0 r4) i.r5'
  StrCmp $5 1 0 stable_broker_caption_free
  System::Call '*$3(p .r4)' ; TOKEN_USER starts with a pointer to the SID.
  StrCmp $4 0 stable_broker_caption_free
  System::Call '$SYSDIR\advapi32.dll::ConvertSidToStringSidW(p r4, *p 0 r5) i.r6'
  StrCmp $6 1 0 stable_broker_caption_free
  StrCmp $5 0 stable_broker_caption_free
  System::Call '*$5(&w256 .r6)'
  System::Call '$SYSDIR\kernel32.dll::LocalFree(p r5) p'
  StrCmp $6 "" stable_broker_caption_free
  StrCpy $StableLegacyBrokerCaption "AutomationForge.SnipStudio.ShortcutHost.v1.$6"
stable_broker_caption_free:
  System::Free $3
stable_broker_caption_close:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r1) i'
stable_broker_caption_done:
  System::Store "l"
FunctionEnd
!endif

; The WinUI class and held process handle bind a matching HWND's PID to this
; installed EXE. A cooperative endpoint must acknowledge its own protocol;
; its caption, visibility and owner can change without changing its identity.
Function FindInstalledPreviewWindow
  System::Store "s"
  StrCpy $PreviewCloseWindow 0
  StrCpy $PreviewClosePid 0
  StrCpy $PreviewCloseProcess 0
  StrCpy $PreviewCloseObserved 0
  StrCpy $0 0
  StrCpy $8 0
  System::Call '$SYSDIR\user32.dll::GetDesktopWindow() p.r9'
  StrCmp $9 0 preview_find_uncertain
  StrCmp $9 "error" preview_find_uncertain
preview_find_next:
  System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
  IntOp $7 $7 - $PreviewCloseStarted
  IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_find_uncertain 0 preview_find_uncertain
  IntCmp $8 512 preview_find_uncertain 0 preview_find_uncertain
  IntOp $8 $8 + 1
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      StrCmp $StableLegacyBrokerCaption "" preview_find_uncertain
      StrCpy $1 $StableLegacyBrokerCaption
      ; Filter by the exact caption within HWND_MESSAGE. The bounded scan
      ; counts only matching candidates and never probes unrelated windows.
      System::Call '$SYSDIR\kernel32.dll::SetLastError(i 0)'
      System::Call '$SYSDIR\user32.dll::FindWindowExW(p -3, p r0, p 0, w r1) p.r0 ?e'
      Pop $4
      StrCmp $0 "error" preview_find_uncertain
      StrCmp $0 0 0 preview_find_pid
      StrCmp $4 0 preview_find_done
      StrCmp $4 2 preview_find_done ; ERROR_FILE_NOT_FOUND
      StrCmp $4 1168 preview_find_done preview_find_uncertain ; ERROR_NOT_FOUND
    ${Else}
  !endif
      System::Call '$SYSDIR\user32.dll::FindWindowExW(p r9, p r0, w "${PREVIEW_WINDOW_CLASS}", p 0) p.r0'
  !ifdef SNIP_STUDIO_STABLE
    ${EndIf}
  !endif
  StrCmp $0 0 preview_find_done
  StrCmp $0 "error" preview_find_uncertain
  ${If} $PreviewCloseLegacyScan == 1
    System::Call '$SYSDIR\user32.dll::GetWindow(p r0, i 4) p.r4' ; GW_OWNER
    StrCmp $4 0 0 preview_find_next
    System::Call '$SYSDIR\user32.dll::IsWindowVisible(p r0) i.r4'
    StrCmp $4 0 preview_find_next
    StrCmp $4 "error" preview_find_next
    ; Legacy Close is only for the visible editor or live recorder. Never
    ; send it to Settings, a video review, or an unchecked auxiliary window.
    System::Call '$SYSDIR\user32.dll::GetWindowTextW(p r0, w .r1, i ${NSIS_MAX_STRLEN}) i.r4'
    IntCmp $4 11 0 preview_find_next 0
    StrCpy $4 $1 11
    StrCmpS $4 "Snip Studio" preview_find_legacy_title
    StrCpy $4 $1 11 2 ; The dirty editor adds a bullet and space.
    StrCmpS $4 "Snip Studio" 0 preview_find_next
    StrCpy $1 $1 "" 2
preview_find_legacy_title:
    StrCmpS $1 "Snip Studio" preview_find_pid
    StrCpy $4 $1 1 11
    StrCmpS $4 " " 0 preview_find_next
    StrCpy $4 $1 1 13
    StrCmpS $4 " " 0 preview_find_next
    StrCpy $4 $1 "" 14
    StrCmpS $4 "Recording" preview_find_pid
    StrCmpS $4 "Record video" preview_find_pid
    ${GetFileExt} "$4" $5
    StrCmp $5 "png" preview_find_pid
    StrCmp $5 "jpg" preview_find_pid
    StrCmp $5 "jpeg" preview_find_pid
    StrCmp $5 "bmp" preview_find_pid
    StrCmp $5 "gif" preview_find_pid
    StrCmp $5 "tif" preview_find_pid
    StrCmp $5 "tiff" preview_find_pid
    StrCmp $5 "pdf" preview_find_pid preview_find_next
  ${EndIf}
preview_find_pid:
  System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 r2) i.r4'
  StrCmp $4 0 preview_find_next
  StrCmp $4 "error" preview_find_next
  StrCmp $2 0 preview_find_next
  ; PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE. No terminate access.
  System::Call '$SYSDIR\kernel32.dll::OpenProcess(i 0x101000, i 0, i r2) p.r3'
  StrCmp $3 0 preview_find_next
  StrCmp $3 "error" preview_find_next
  System::Call '$SYSDIR\kernel32.dll::QueryFullProcessImageNameW(p r3, i 0, w .r1, *i ${NSIS_MAX_STRLEN} r6) i.r4'
  StrCmp $4 1 0 preview_find_release
  StrCmp $1 $PreviewClosePath 0 preview_find_release
  StrCpy $PreviewCloseObserved 1
  Call CheckPreviewCloseTarget
  StrCmp $4 1 0 preview_find_release
  StrCmp $PreviewCloseLegacyScan 1 preview_find_accepted
  StrCpy $R4 $PreviewCloseMessage
  ; Probe only. A successful zero reply is unsupported, but a timeout is
  ; uncertain, so no later fallback can close that window or its process.
  System::Call '$SYSDIR\user32.dll::SendMessageTimeoutW(p r0, i R4, p ${PREVIEW_EXIT_COOKIE}, p 1, i 0x23, i 1000, *p 0 R8) p.R7'
  StrCmp $R7 0 preview_find_probe_failed
  StrCmp $R7 "error" preview_find_probe_failed
  StrCmp $R8 ${PREVIEW_EXIT_COOKIE} preview_find_accepted
  StrCmp $R8 0x5355 preview_find_accepted preview_find_release
preview_find_accepted:
  StrCpy $PreviewCloseWindow $0
  StrCpy $PreviewClosePid $2
  StrCpy $PreviewCloseProcess $3
  Goto preview_find_done
preview_find_probe_failed:
  StrCpy $PreviewCloseProbeFailed 1
preview_find_release:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
  Goto preview_find_next
preview_find_uncertain:
  StrCpy $PreviewCloseProbeFailed 1
preview_find_done:
  System::Store "l"
FunctionEnd

; Request normal closure at the start of installation, after the user chose
; Install. The installation path registration excludes portable copies. An
; old visible window uses its own close/save prompts. Newer previews can exit
; through the app's cooperative protocol even if hidden in the tray.
Function CloseInstalledPreviewForUpdate
  System::Store "s"
  ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"
  StrCmp $0 "" preview_close_done
  GetFullPathName $0 "$0\${APP_EXE}"
  GetFullPathName $PreviewClosePath "$INSTDIR\${APP_EXE}"
  StrCmp $0 $PreviewClosePath 0 preview_close_done
  IfFileExists "$PreviewClosePath" 0 preview_close_done
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      ; Legacy shutdown can open save/recording prompts. Silent Setup checks
      ; file availability and refuses a busy install without requesting exit.
      IfSilent preview_close_done
    ${EndIf}
  !endif
  StrCpy $PreviewLegacyCloseAllowed 0
  ClearErrors
  GetDLLVersion "$PreviewClosePath" $0 $1
  IfErrors preview_close_register
  ; Only known builds from before the cooperative hook are eligible for a
  ; standard Close. Later builds can have independently editable windows.
  IntCmpU $0 7 0 preview_close_register preview_close_register ; 0.7 in the high word.
  IntCmpU $1 1 0 preview_close_register 0
  IntCmpU $1 5 0 0 preview_close_register ; 0.1 through 0.5 in the low word.
  StrCpy $PreviewLegacyCloseAllowed 1
preview_close_register:
  System::Call '$SYSDIR\user32.dll::RegisterWindowMessageW(w "${PREVIEW_EXIT_MESSAGE}") i.r7'
  StrCpy $PreviewCloseMessage $7
  StrCmp $7 0 preview_close_done
  StrCmp $7 "error" preview_close_done
preview_close_retry:
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      Call ReadStableLegacyBrokerCaption
    ${EndIf}
  !endif
  System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
  StrCpy $PreviewCloseStarted $7
  StrCpy $PreviewCloseDetail "Save or cancel any open Settings, snip changes, or text reviews. Finish any active recordings. If Snip Studio is still running, choose Exit from its notification-area icon."
preview_close_find:
  StrCpy $PreviewCloseLegacyScan 0
  StrCpy $PreviewCloseProbeFailed 0
  Call FindInstalledPreviewWindow
  StrCmp $PreviewCloseWindow 0 0 preview_close_target
  StrCmp $PreviewCloseProbeFailed 1 preview_close_pending
  StrCmp $PreviewCloseObserved 0 preview_close_done
  IfSilent preview_close_pending
  StrCmp $PreviewLegacyCloseAllowed 1 0 preview_close_pending
  StrCpy $PreviewCloseLegacyScan 1
  Call FindInstalledPreviewWindow
  StrCmp $PreviewCloseWindow 0 preview_close_pending
preview_close_target:
  StrCpy $0 $PreviewCloseWindow
  StrCpy $2 $PreviewClosePid
  StrCpy $3 $PreviewCloseProcess
  StrCpy $R4 $PreviewCloseMessage
  System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
  IntOp $7 $7 - $PreviewCloseStarted
  IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_close_release 0 preview_close_release
  Call CheckPreviewCloseTarget
  StrCmp $4 1 0 preview_close_again
  StrCmp $PreviewCloseLegacyScan 0 preview_close_cooperative

  ; A legacy hidden window can only Exit through its tray menu. A silent
  ; update never asks an old preview to close because it may show a prompt.
  IfSilent preview_close_release
  System::Call '$SYSDIR\user32.dll::IsWindowVisible(p r0) i.r4'
  StrCmp $4 0 preview_close_release
  StrCmp $4 "error" preview_close_release
  System::Call '$SYSDIR\user32.dll::AllowSetForegroundWindow(i r2) i.r4'
  System::Call '$SYSDIR\user32.dll::IsIconic(p r0) i.r4'
  ${If} $4 == 1
    System::Call '$SYSDIR\user32.dll::ShowWindowAsync(p r0, i 9) i.r4' ; SW_RESTORE
  ${EndIf}
  Call CheckPreviewCloseTarget
  StrCmp $4 1 0 preview_close_again
  System::Call '$SYSDIR\user32.dll::SetForegroundWindow(p r0) i.r4'
  DetailPrint "Asking the installed Snip Studio window to close."
  ; WM_SYSCOMMAND / SC_CLOSE follows the normal title bar Close path.
  System::Call '$SYSDIR\user32.dll::PostMessageW(p r0, i 0x0112, p 0xF060, p 0) i.r4'
  StrCmp $4 1 0 preview_close_release
  StrCpy $5 0 ; Acknowledge closure of this legacy window, then find the next.
  Goto preview_close_wait

preview_close_cooperative:
  !ifdef SNIP_STUDIO_STABLE
    StrCmp $StableLegacyUpgrade 1 0 stable_close_deadline_ready
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Close the installed Snip Studio to continue updating? Save or finish any unfinished work if Snip Studio asks." /SD IDNO IDYES stable_exit_confirmed
    System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
    Goto preview_close_cancel
  stable_exit_confirmed:
    ; Time spent reviewing the prompt does not count toward shutdown.
    System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
    StrCpy $PreviewCloseStarted $7
  stable_close_deadline_ready:
  !endif
  StrCpy $6 0
  IfSilent 0 preview_close_send
  StrCpy $6 2 ; New previews receive an idle-only request during silent setup.
preview_close_send:
  ${If} $6 == 0
    System::Call '$SYSDIR\user32.dll::AllowSetForegroundWindow(i r2) i.r4'
    DetailPrint "Waiting for the installed Snip Studio to exit."
  ${EndIf}
  Call CheckPreviewCloseTarget
  StrCmp $4 1 0 preview_close_again
  System::Call '$SYSDIR\user32.dll::SendMessageTimeoutW(p r0, i R4, p ${PREVIEW_EXIT_COOKIE}, p r6, i 0x23, i 1000, *p 0 R8) p.R7'
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      ; SMTO_ERRORONEXIT can report failure while the broker is shutting down.
      ; Wait only on the already validated process, without sending again.
      StrCmp $R7 0 stable_close_wait_for_exit
      StrCmp $R7 "error" stable_close_wait_for_exit
    ${EndIf}
  !endif
  StrCmp $R7 0 preview_close_release
  StrCmp $R7 "error" preview_close_release
  StrCmp $R8 ${PREVIEW_EXIT_COOKIE} preview_close_accepted
  StrCmp $R8 0x5355 preview_close_accepted preview_close_release
preview_close_accepted:
  !ifdef SNIP_STUDIO_STABLE
  stable_close_wait_for_exit:
  !endif
  StrCpy $5 1 ; The cooperative request may have queued exit. Wait for the process.

preview_close_wait:
  System::Call '$SYSDIR\kernel32.dll::WaitForSingleObject(p r3, i 0) i.r4'
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      StrCmp $4 0 stable_close_exited
    ${EndIf}
  !endif
  StrCmp $4 0 preview_close_again
  StrCmp $4 258 0 preview_close_release
  ${If} $5 == 0
    System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 r6) i.r4'
    StrCmp $6 $2 0 preview_close_again
    ; Some old builds hide to the tray instead of closing. Do not wait on
    ; that HWND or repeatedly post Close after it is already hidden.
    System::Call '$SYSDIR\user32.dll::IsWindowVisible(p r0) i.r4'
    StrCmp $4 0 preview_close_again
  ${EndIf}
  System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
  IntOp $7 $7 - $PreviewCloseStarted
  IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_close_release 0 preview_close_release
  Sleep 200
  Goto preview_close_wait

  !ifdef SNIP_STUDIO_STABLE
  stable_close_exited:
    ; The held process has exited. File probes still run after this function.
    System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
    Goto preview_close_done
  !endif
preview_close_again:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
  System::Call '$SYSDIR\kernel32.dll::GetTickCount() i.r7'
  IntOp $7 $7 - $PreviewCloseStarted
  IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_close_pending 0 preview_close_pending
  Goto preview_close_find
preview_close_release:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i.r4'
preview_close_pending:
  IfSilent preview_close_cancel
  MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "The installed Snip Studio is still open.$\r$\n$\r$\n$PreviewCloseDetail$\r$\n$\r$\nChoose Retry after closing it, or Cancel to stop this update." /SD IDCANCEL IDRETRY preview_close_retry
preview_close_cancel:
  System::Store "l"
  Call DiscardPreviewStage
  StrCmp $PreviewStageCleanupFailed 0 preview_close_cancel_clean
  SetErrorLevel 2
  Abort
preview_close_cancel_clean:
  SetErrorLevel 3
  Abort
preview_close_done:
  System::Store "l"
FunctionEnd

; Confirm process lifetime and HWND ownership again before each request. Inputs
; are the installer-held process handle $3, its PID $2 and candidate HWND $0.
Function CheckPreviewCloseTarget
  System::Call '$SYSDIR\kernel32.dll::WaitForSingleObject(p r3, i 0) i.r4'
  StrCmp $4 258 0 preview_target_invalid
  System::Call '$SYSDIR\user32.dll::GetWindowThreadProcessId(p r0, *i 0 R6) i.r4'
  StrCmp $4 0 preview_target_invalid
  StrCmp $4 "error" preview_target_invalid
  StrCmp $R6 $2 0 preview_target_invalid
  StrCpy $4 1
  Return
preview_target_invalid:
  StrCpy $4 0
FunctionEnd

; File probes remain authoritative before any installed bytes change. A lock
; from a process outside this installation never triggers a close request.
!macro DefinePreviewFileCheck Prefix
Function ${Prefix}CheckInstalledFileAvailable
  Exch $0
  Push $1
  StrCpy $0 "$INSTDIR\$0"
retry_available:
  Push "$0"
  Call ${Prefix}IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 unsafe_file
  IfFileExists "$0" 0 available
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r0, i 0x40010000, i 7, p 0, i 3, i 0x80, p 0) p.r1'
  ${If} $1 == -1
  ${OrIf} $1 == 0
  ${OrIf} $1 == "error"
    DetailPrint "File cannot be updated: $0"
    IfSilent blocked_file
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION|MB_DEFBUTTON2 "An application file is in use or cannot be changed.$\r$\n$\r$\nClose ${APP_NAME}, check folder permissions, and retry.$\r$\n$\r$\n$0" /SD IDCANCEL IDRETRY retry_available
    Goto blocked_file
  ${EndIf}
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r1) i'
available:
  Pop $1
  Pop $0
  Return
unsafe_file:
  DetailPrint "Unsafe or inaccessible installation path: $0"
  IfSilent blocked_file
  MessageBox MB_OK|MB_ICONEXCLAMATION "The installation path is inaccessible or contains a link. Choose a local folder without links.$\r$\n$\r$\n$0"
blocked_file:
  Pop $1
  Pop $0
  !if "${Prefix}" == ""
    Call DiscardPreviewStage
  !endif
  SetErrorLevel 2
  Abort
FunctionEnd
!macroend
!insertmacro DefinePreviewFileCheck ""
!insertmacro DefinePreviewFileCheck "un."

; Generated includes invoke these macros with validated, escaped literals.
!macro PreviewCheckFile RelativePath
  !ifdef PREVIEW_SKIP_EXECUTABLE
    !if "${RelativePath}" != "${APP_EXE}"
      Push "${RelativePath}"
      Call ${PREVIEW_FILE_CHECK}
    !endif
  !else
  Push "${RelativePath}"
  Call ${PREVIEW_FILE_CHECK}
  !endif
!macroend

!macro PreviewInstallDirectory RelativeDirectory
  ClearErrors
  !if "${RelativeDirectory}" == ""
    SetOutPath "$INSTDIR"
  !else
    SetOutPath "$INSTDIR\${RelativeDirectory}"
  !endif
  IfErrors 0 +2
    Call AbortPreviewStaging
!macroend

!macro PreviewInstallFile FileName SourcePath
  ClearErrors
  File "/oname=${FileName}" "${SourcePath}"
  IfErrors 0 +2
    Call AbortPreviewStaging
!macroend

!macro PreviewRemoveFile RelativePath
  !ifdef PREVIEW_STAGE_CLEANUP
    Push "${RelativePath}"
    Call DeletePreviewStagedFile
  !else
    IfFileExists "$INSTDIR\${RelativePath}" 0 +6
    ClearErrors
    Delete "$INSTDIR\${RelativePath}"
    IfErrors 0 +3
      SetErrorLevel 2
      Abort "An application file could not be removed. Close the application and retry."
  !endif
!macroend

!macro PreviewRemoveDirectory RelativePath
  !ifdef PREVIEW_STAGE_CLEANUP
    Push "${RelativePath}"
    Call RemoveEmptyPreviewStageDirectory
  !else ifdef STABLE_RESTORE_EXISTING
    ; Preserve installation directories while recovering the previous files.
  !else
    RMDir "$INSTDIR\${RelativePath}"
  !endif
!macroend

; This include is derived from pinned previous release catalogs, excluding
; the new payload. It never enumerates or removes arbitrary installed files.
!macro PreviewRetiredFile RelativePath ApprovedHashes
  !ifdef PREVIEW_RETIRE_EXISTING
    Push "${RelativePath}"
    Push "${ApprovedHashes}"
    Call RetirePreviewInstalledFile
  !endif
  !ifdef PREVIEW_RESTORE_RETIRED
    Push "${RelativePath}"
    Call RestorePreviewRetiredFile
  !endif
  !ifdef PREVIEW_DISCARD_RETIRED
    Push "${RelativePath}"
    Call DeletePreviewRetiredBackup
  !endif
!macroend

!macro PreviewRetiredDirectory RelativePath
  !ifdef PREVIEW_REMOVE_RETIRED_DIRECTORIES
    Push "${RelativePath}"
    Call RemovePreviewRetiredDirectory
  !endif
!macroend

!ifdef SNIP_STUDIO_STABLE
; Old-only files and unknown contents stay in place. The overlap catalog lists
; only replacement targets proven to belong to a supported original release.
!macro StableLegacyOverlap RelativePath ApprovedHashes
  !if "${RelativePath}" != "${APP_EXE}"
    Push "${RelativePath}"
    Push "${ApprovedHashes}"
    Call RetirePreviewInstalledFile
  !endif
!macroend

!macro StableRemovePayloadFile RelativePath SHA256
  !ifdef PREVIEW_STAGE_CLEANUP
    !insertmacro PreviewRemoveFile "${RelativePath}"
  !else ifdef STABLE_RESTORE_EXISTING
    !if "${RelativePath}" != "${APP_EXE}"
      Push "${RelativePath}"
      Push "${SHA256}"
      Call RestorePreviewOriginalFile
    !endif
  !else
    Push "${RelativePath}"
    Push "${SHA256}"
    Call un.RemoveStablePayloadFile
  !endif
!macroend

; Share the exact list of values between snapshot, write and rollback.
!macro StableRegistrationValues Action
  !insertmacro ${Action} 1 "${APP_KEY}" "InstallLocation" Str "$INSTDIR"
  !insertmacro ${Action} 2 "${ARP_KEY}" "DisplayName" Str "${APP_NAME}"
  !insertmacro ${Action} 3 "${ARP_KEY}" "DisplayVersion" Str "${APP_VERSION}"
  !insertmacro ${Action} 4 "${ARP_KEY}" "Publisher" Str "Automation Forge LLC"
  !insertmacro ${Action} 5 "${ARP_KEY}" "InstallLocation" Str "$INSTDIR"
  !insertmacro ${Action} 6 "${ARP_KEY}" "DisplayIcon" Str "$INSTDIR\${APP_EXE},0"
  !insertmacro ${Action} 7 "${ARP_KEY}" "URLInfoAbout" Str "${APP_RELEASE_URL}"
  !insertmacro ${Action} 8 "${ARP_KEY}" "UninstallString" Str '$\"$INSTDIR\Uninstall.exe$\"'
  !insertmacro ${Action} 9 "${ARP_KEY}" "QuietUninstallString" Str '$\"$INSTDIR\Uninstall.exe$\" /S'
  !insertmacro ${Action} 10 "${ARP_KEY}" "NoModify" DWORD 1
  !insertmacro ${Action} 11 "${ARP_KEY}" "NoRepair" DWORD 1
  !insertmacro ${Action} 12 "${ARP_KEY}" "EstimatedSize" DWORD "${PACKAGE_KB}"
!macroend

!macro CaptureStableRegistrationValue Index Key Value Kind Data
  Push ${Index}
  Push "${Key}"
  Push "${Value}"
  Call CaptureStableRegistryValue
  Pop $0
  StrCmp $0 1 0 stable_capture_abort
  StrCpy $StableRegistryCaptured ${Index}
!macroend

!macro WriteStableRegistrationValue Index Key Value Kind Data
  ClearErrors
  WriteReg${Kind} HKCU "${Key}" "${Value}" "${Data}"
  IfErrors stable_write_registration_failed
  StrCpy $StableRegistryWritten ${Index}
!macroend

!macro RestoreStableRegistrationValue Index Key Value Kind Data
  ${If} $StableRegistryWritten >= ${Index}
    Push ${Index}
    Push "${Key}"
    Push "${Value}"
    Push "${Kind}"
    Push "${Data}"
    Call RestoreStableRegistryValue
    Pop $0
    ${If} $0 != 1
      StrCpy $StableRegistrationRecovered 0
      DetailPrint "Could not restore HKCU\${Key} (${Value})."
    ${EndIf}
  ${EndIf}
!macroend
!endif

Function CheckInstalledPreviewFiles
  !define PREVIEW_FILE_CHECK "CheckInstalledFileAvailable"
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  !undef PREVIEW_FILE_CHECK
  Push "Uninstall.exe"
  Call CheckInstalledFileAvailable
FunctionEnd

Function un.CheckInstalledPreviewFiles
  !define PREVIEW_FILE_CHECK "un.CheckInstalledFileAvailable"
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  !undef PREVIEW_FILE_CHECK
  ; NSIS manages its own running uninstaller image; do not probe that image.
FunctionEnd

Function CheckPreviewRetirementOwner
  Push $0
  StrCpy $PreviewRetireReleaseFiles 0
  StrCmp $PreviewDestinationOwned 0 retirement_owner_done
  Call VerifyOwnedPreviewLauncher
  Pop $0
  StrCmp $0 1 retirement_owner_verified
  Pop $0
  DetailPrint "The registered launcher changed or could not be verified after staging."
  Call DiscardPreviewStage
  SetErrorLevel 2
  Abort "Setup could not confirm ownership of the existing application. No installed files were replaced."
retirement_owner_verified:
  StrCpy $PreviewRetireReleaseFiles 1
retirement_owner_done:
  Pop $0
FunctionEnd

Function CheckPreviewRetiredPathLength
  System::Store "s"
  Pop $0 ; base path
  Pop $1 ; relative path
  StrLen $0 $0
  StrLen $1 $1
  IntOp $0 $0 + $1
  IntOp $0 $0 + 1
  StrCpy $1 0
  IntCmpU $0 ${NSIS_MAX_STRLEN} retirement_length_result 0 retirement_length_result
  StrCpy $1 1
retirement_length_result:
  Push $1
  System::Store "l"
FunctionEnd

; Reuse the usual link-safe cleanup once sure the composed path cannot be
; truncated. Only the new, exclusively created transaction is cleaned here.
Function DeletePreviewRetiredBackup
  Exch $0
  Push $1
  Push "$0"
  Push "$PreviewBackupRoot"
  Call CheckPreviewRetiredPathLength
  Pop $1
  StrCmp $1 1 0 delete_retired_done
  Push "$0"
  Call DeletePreviewStagedFile
delete_retired_done:
  Pop $1
  Pop $0
FunctionEnd

Function RemovePreviewRetiredDirectory
  Exch $0
  Push $1
  Push "$0"
  Push "$INSTDIR"
  Call CheckPreviewRetiredPathLength
  Pop $1
  StrCmp $1 1 0 remove_retired_directory_done
  Push "$0"
  Call RemoveEmptyPreviewStageDirectory
remove_retired_directory_done:
  Pop $1
  Pop $0
FunctionEnd

; Hash the complete ordinary byte stream from the same handle later used to
; move it. Bound the view for the 32-bit Setup process and fail closed if the
; file cannot be read. All calls are to OS DLLs; there is no hash utility.
!macro DefinePreviewSHA256 Prefix
Function ${Prefix}ReadPreviewRetiredSHA256
  System::Store "s"
  Pop $0
  StrCpy $1 ""
  System::Call '$SYSDIR\kernel32.dll::GetFileSize(p r0, *i 0 r2) i.r3'
  StrCmp $2 0 0 retirement_hash_return
  StrCmp $3 "error" retirement_hash_return
  StrCmp $3 0 retirement_hash_return
  IntCmpU $3 536870912 0 0 retirement_hash_return
  System::Call '$SYSDIR\kernel32.dll::CreateFileMappingW(p r0, p 0, i 2, i 0, i 0, p 0) p.r4'
  StrCmp $4 0 retirement_hash_return
  StrCmp $4 "error" retirement_hash_return
  System::Call '$SYSDIR\kernel32.dll::MapViewOfFile(p r4, i 4, i 0, i 0, p 0) p.r5'
  StrCmp $5 0 retirement_hash_close
  StrCmp $5 "error" retirement_hash_close
  System::Alloc 32
  Pop $6
  StrCmp $6 0 retirement_hash_unmap
  StrCmp $6 "error" retirement_hash_unmap
  ; 0x41 = BCRYPT_SHA256_ALG_HANDLE on the Windows 10+ baseline.
  System::Call '$SYSDIR\bcrypt.dll::BCryptHash(p 0x41, p 0, i 0, p r5, i r3, p r6, i 32) i.r7'
  StrCmp $7 0 0 retirement_hash_free
  ; CRYPT_STRING_HEXRAW | CRYPT_STRING_NOCRLF, 64 hex characters plus NUL.
  System::Call '$SYSDIR\crypt32.dll::CryptBinaryToStringW(p r6, i 32, i 0x4000000C, w .r8, *i 65) i.r7'
  StrCmp $7 1 0 retirement_hash_free
  StrLen $7 $8
  IntCmp $7 64 0 retirement_hash_free retirement_hash_free
  StrCpy $1 $8
retirement_hash_free:
  System::Free $6
retirement_hash_unmap:
  System::Call '$SYSDIR\kernel32.dll::UnmapViewOfFile(p r5) i'
retirement_hash_close:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r4) i'
retirement_hash_return:
  Push $1
  System::Store "l"
FunctionEnd
!macroend
!insertmacro DefinePreviewSHA256 ""
!ifdef SNIP_STUDIO_STABLE
!insertmacro DefinePreviewSHA256 "un."
!endif

Function RetirePreviewInstalledFile
  System::Store "s"
  !ifdef SNIP_STUDIO_STABLE
    StrCpy $StableOriginalMoved 0
  !endif
  Pop $R0 ; approved SHA-256 values separated by '|'
  Pop $0 ; trusted prior-release relative path
  ; Check the longer backup path before forming either name in NSIS strings.
  Push "$0"
  Push "$PreviewBackupRoot"
  Call CheckPreviewRetiredPathLength
  Pop $2
  StrCmp $2 1 0 retirement_file_kept
  StrCpy $1 "$PreviewInstallRoot\$0"
  IfFileExists "$1" 0 retirement_file_done
  StrCpy $2 "$PreviewBackupRoot\$0"
  Push "$1"
  Call IsSafePreviewPath
  Pop $3
  StrCmp $3 1 0 retirement_file_kept
  Push "$2"
  Call IsSafePreviewPath
  Pop $3
  StrCmp $3 1 0 retirement_file_kept
  ; GENERIC_READ | DELETE, FILE_SHARE_READ, OPEN_EXISTING, OPEN_REPARSE_POINT.
  ; Writes and competing renames are excluded while we read and move the file.
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r1, i 0x80010000, i 1, p 0, i 3, i 0x00200000, p 0) p.r3'
  StrCmp $3 -1 retirement_file_kept
  StrCmp $3 0 retirement_file_kept
  StrCmp $3 "error" retirement_file_kept
  ; Check the held object's attributes, then preserve files that have other
  ; NTFS streams. The one default stream has the 7-character name ::$DATA.
  System::Alloc 128
  Pop $4
  StrCmp $4 0 retirement_close_and_keep
  StrCmp $4 "error" retirement_close_and_keep
  StrCpy $6 -1
  StrCpy $7 -1
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 9, p r4, i 8) i.r5'
  StrCmp $5 1 0 retirement_free_stream_info
  System::Call '*$4(i .r5, i)'
  IntOp $5 $5 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
  StrCmp $5 0 0 retirement_free_stream_info
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 7, p r4, i 128) i.r5'
  StrCmp $5 1 0 retirement_free_stream_info
  System::Call '*$4(i .r6, i .r7)'
  !ifdef SNIP_STUDIO_STABLE
    StrCmp $6 0 0 retirement_free_stream_info
    StrCmp $7 14 0 retirement_free_stream_info
    IntOp $5 $4 + 24
    System::Call '$SYSDIR\kernel32.dll::CompareStringOrdinal(p r5, i 7, w "::$$DATA", i 7, i 0) i.r5'
    StrCmp $5 2 retirement_free_stream_info
    StrCpy $6 -1
  !endif
retirement_free_stream_info:
  System::Free $4
  StrCmp $6 0 0 retirement_close_and_keep ; NextEntryOffset
  StrCmp $7 14 0 retirement_close_and_keep ; StreamNameLength in bytes
  Push "$3"
  Call ReadPreviewRetiredSHA256
  Pop $4
  StrCmp $4 "" retirement_close_and_keep
retirement_hash_match:
  StrLen $5 $R0
  IntCmp $5 64 0 retirement_close_and_keep 0
  StrCpy $5 $R0 64
  StrCmp $4 $5 retirement_backup_file
  StrCpy $5 $R0 1 64
  StrCmp $5 "|" 0 retirement_close_and_keep
  StrCpy $R0 $R0 "" 65
  Goto retirement_hash_match
retirement_backup_file:
  ${GetParent} "$2" $4
  ClearErrors
  CreateDirectory "$4"
  IfErrors retirement_close_and_keep
  Push "$2"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 retirement_close_and_keep
  ; NSIS uses the x86 Unicode System plug-in. FILE_RENAME_INFO's fixed
  ; fields occupy 12 bytes here; FileNameLength excludes the terminating NUL.
  StrLen $4 $2
  IntOp $4 $4 * 2
  System::Call '*(i 0, p 0, i r4, &w${NSIS_MAX_STRLEN} r2) p.r5'
  StrCmp $5 0 retirement_close_and_keep
  StrCmp $5 "error" retirement_close_and_keep
  IntOp $4 $4 + 14
  System::Call '$SYSDIR\kernel32.dll::SetFileInformationByHandle(p r3, i 3, p r5, i r4) i.r6'
  System::Free $5
  StrCmp $6 1 0 retirement_close_and_keep
  !ifdef SNIP_STUDIO_STABLE
    StrCpy $StableOriginalMoved 1
  !endif
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i'
  Goto retirement_file_done
retirement_close_and_keep:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i'
retirement_file_kept:
  DetailPrint "An older installation file could not be verified or moved and was kept: $0"
retirement_file_done:
  System::Store "l"
FunctionEnd

Function RestorePreviewRetiredFile
  System::Store "s"
  Pop $0
  Push "$0"
  Push "$PreviewBackupRoot"
  Call CheckPreviewRetiredPathLength
  Pop $1
  StrCmp $1 1 0 restore_retired_done
  StrCpy $1 "$PreviewBackupRoot\$0"
  IfFileExists "$1" 0 restore_retired_done
  StrCpy $2 "$PreviewInstallRoot\$0"
  Push "$1"
  Call IsSafePreviewPath
  Pop $3
  StrCmp $3 1 0 restore_retired_failed
  Push "$2"
  Call IsSafePreviewPath
  Pop $3
  StrCmp $3 1 0 restore_retired_failed
  ; A new file might have appeared at this path. Never overwrite it.
  IfFileExists "$2" restore_retired_failed
  ClearErrors
  Rename "$1" "$2"
  IfErrors restore_retired_failed restore_retired_done
restore_retired_failed:
  StrCpy $PreviewRollbackFailed 1
  DetailPrint "The previous release file could not be restored: $2"
restore_retired_done:
  System::Store "l"
FunctionEnd

Function PreparePreviewStage
  Push $0
  Push $1
  StrCpy $PreviewInstallRoot $INSTDIR
  ClearErrors
  CreateDirectory "$PreviewInstallRoot"
  IfErrors staging_directory_failed
  StrCpy $0 "$PreviewInstallRoot\${PREVIEW_TRANSACTION_FOLDER}"
  ; Create exclusively. A preexisting path may hold the user's recovery data.
  System::Call '$SYSDIR\kernel32.dll::CreateDirectoryW(w r0, p 0) i.r1'
  StrCmp $1 1 0 staging_directory_failed
  StrCpy $PreviewTransactionRoot $0
  StrCpy $PreviewStageRoot "$PreviewTransactionRoot\Payload"
  StrCpy $PreviewBackupRoot "$PreviewTransactionRoot\Original"
  ClearErrors
  CreateDirectory "$PreviewStageRoot"
  IfErrors staging_directory_failed
  CreateDirectory "$PreviewBackupRoot"
  IfErrors staging_directory_failed
  Pop $1
  Pop $0
  Return
staging_directory_failed:
  Pop $1
  Pop $0
  Call AbortPreviewStaging
FunctionEnd

Function AbortPreviewStaging
  Call DiscardPreviewStage
  DetailPrint "The application could not be staged. Check folder permissions and free disk space. Existing files were kept."
  SetErrorLevel 2
  Abort "The application could not be staged. Check folder permissions and free disk space. Existing files were kept."
FunctionEnd

Function DeletePreviewStagedFile
  Exch $0
  Push $1
  StrCpy $0 "$INSTDIR\$0"
  IfFileExists "$0" 0 stage_delete_done
  Push "$0"
  Call IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 stage_delete_done
  Delete "$0"
stage_delete_done:
  Pop $1
  Pop $0
FunctionEnd

Function RemoveEmptyPreviewStageDirectory
  Exch $0
  Push $1
  StrCpy $0 "$INSTDIR\$0"
  Push "$0"
  Call IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 stage_directory_done
  ; The manifest lists each directory once, with children before parents.
  RMDir "$0"
stage_directory_done:
  Pop $1
  Pop $0
FunctionEnd

Function RemovePreviewStagedFiles
  Push "Uninstall.exe"
  Call DeletePreviewStagedFile
  !define PREVIEW_STAGE_CLEANUP
  !include "${PAYLOAD_UNINSTALL_INCLUDE}"
  !undef PREVIEW_STAGE_CLEANUP
  Push "$INSTDIR"
  Call IsSafePreviewPath
  Pop $0
  StrCmp $0 1 0 stage_root_kept
  RMDir "$INSTDIR"
stage_root_kept:
FunctionEnd

Function DiscardPreviewStage
  Push $0
  Push $1
  StrCpy $PreviewStageCleanupFailed 0
  StrCmp $PreviewTransactionRoot "" stage_discard_done
  SetOutPath "$PreviewInstallRoot"
  StrCpy $INSTDIR $PreviewStageRoot
  !ifdef SNIP_STUDIO_STABLE
    Call DiscardStableRegistrationSnapshot
    Push ".setup-start.lnk"
    Call DeletePreviewStagedFile
    Push ".setup-remove.lnk"
    Call DeletePreviewStagedFile
  !endif
  Call RemovePreviewStagedFiles
  StrCpy $INSTDIR $PreviewBackupRoot
  ${If} $PreviewRetireReleaseFiles == 1
    !define PREVIEW_DISCARD_RETIRED
    !define PREVIEW_REMOVE_RETIRED_DIRECTORIES
    !include "${RETIRED_PAYLOAD_INCLUDE}"
    !undef PREVIEW_REMOVE_RETIRED_DIRECTORIES
    !undef PREVIEW_DISCARD_RETIRED
  ${EndIf}
  Call RemovePreviewStagedFiles
  StrCpy $INSTDIR $PreviewInstallRoot
  Push "$PreviewTransactionRoot"
  Call IsSafePreviewPath
  Pop $0
  StrCmp $0 1 0 stage_discard_warn
  RMDir "$PreviewTransactionRoot"
  StrCpy $0 $PreviewTransactionRoot
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r0 ?e'
  Pop $1
  StrCmp $0 -1 0 stage_discard_warn
  StrCmp $1 2 stage_discard_removed ; ERROR_FILE_NOT_FOUND
  StrCmp $1 3 stage_discard_removed stage_discard_warn ; ERROR_PATH_NOT_FOUND
stage_discard_removed:
  StrCpy $PreviewTransactionRoot ""
  ClearErrors
  Goto stage_discard_done
stage_discard_warn:
  StrCpy $PreviewStageCleanupFailed 1
  DetailPrint "Setup could not verify removal of the transaction folder at $PreviewTransactionRoot."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Setup could not finish cleaning up its transaction folder.$\r$\n$\r$\nKeep the files at $PreviewTransactionRoot and contact support before retrying."
stage_discard_done:
  Pop $1
  Pop $0
FunctionEnd

!ifdef SNIP_STUDIO_STABLE
Function ReadStableStagedSHA256
  System::Store "s"
  Pop $1
  StrCpy $3 ""
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r1, i 0x80000000, i 1, p 0, i 3, i 0x00200000, p 0) p.r2'
  StrCmp $2 -1 stable_staged_done
  StrCmp $2 0 stable_staged_done
  StrCmp $2 "error" stable_staged_done
  Push "$2"
  Call ReadPreviewRetiredSHA256
  Pop $3
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r2) i'
stable_staged_done:
  Push "$3"
  System::Store "l"
FunctionEnd

; A repeat install of this exact release can replace its own identical files.
; Hash and move the installed file through the same write-denying handle.
Function BackUpIdenticalStableFile
  System::Store "s"
  Pop $0 ; relative path
  Pop $1 ; staged path
  StrCpy $StableOriginalMoved 0
  Push "$1"
  Call ReadStableStagedSHA256
  Pop $3
  StrCmp $3 "" stable_identical_done
  Push "$0"
  Push "$3"
  Call RetirePreviewInstalledFile
stable_identical_done:
  System::Store "l"
FunctionEnd

; Slots retain the value's original type and exact bytes. A zero or one
; state distinguishes a missing key from a missing value in an existing key.
Function CaptureStableRegistryValue
  System::Store "s"
  Pop $2
  Pop $1
  Pop $0
  IntOp $0 $0 - 1
  IntOp $0 $0 * 16
  IntOp $0 $StableRegistryBackup + $0
  StrCpy $9 0
  System::Call '$SYSDIR\advapi32.dll::RegOpenKeyExW(p 0x80000001, w r1, i 0, i 0x0101, *p 0 r3) i.r5'
  StrCmp $5 2 stable_capture_key_absent
  StrCmp $5 0 0 stable_capture_return
  System::Call '$SYSDIR\advapi32.dll::RegQueryValueExW(p r3, w r2, p 0, *i 0 r6, p 0, *i 0 r4) i.r5'
  StrCmp $5 2 stable_capture_value_absent
  StrCmp $5 0 0 stable_capture_close
  IntCmpU $4 65536 0 0 stable_capture_close
  IntOp $7 $4 + 1
  System::Alloc $7
  Pop $8
  StrCmp $8 0 stable_capture_close
  StrCmp $8 "error" stable_capture_close
  System::Call '$SYSDIR\advapi32.dll::RegQueryValueExW(p r3, w r2, p 0, *i 0 r6, p r8, *i r4 r4) i.r5'
  StrCmp $5 0 0 stable_capture_data_failed
  System::Call '*$0(i 2, i r6, i r4, p r8)'
  StrCpy $9 1
  Goto stable_capture_close
stable_capture_data_failed:
  System::Free $8
  Goto stable_capture_close
stable_capture_value_absent:
  System::Call '*$0(i 1)'
  StrCpy $9 1
stable_capture_close:
  System::Call '$SYSDIR\advapi32.dll::RegCloseKey(p r3) i'
  Goto stable_capture_return
stable_capture_key_absent:
  System::Call '*$0(i 0)'
  StrCpy $9 1
stable_capture_return:
  Push "$9"
  System::Store "l"
FunctionEnd

Function RestoreStableRegistryValue
  System::Store "s"
  Pop $R0 ; data published by Setup
  Pop $R1 ; Str or DWORD
  Pop $2
  Pop $1
  Pop $0
  IntOp $0 $0 - 1
  IntOp $0 $0 * 16
  IntOp $0 $StableRegistryBackup + $0
  System::Call '*$0(i .r5, i .r6, i .r7, p .r8)'
  StrCpy $9 0
  ; Keep external edits made after Setup's write. Restore only when the
  ; current type, length and bytes still equal the successful Setup write.
  System::Call '$SYSDIR\advapi32.dll::RegOpenKeyExW(p 0x80000001, w r1, i 0, i 0x0103, *p 0 r3) i.r4'
  StrCmp $4 0 0 stable_restore_value_return
  StrCpy $R2 4
  StrCpy $R3 4 ; REG_DWORD
  StrCmp $R1 "DWORD" stable_restore_query
  StrCpy $R3 1 ; REG_SZ
  StrLen $R2 $R0
  IntOp $R2 $R2 + 1
  IntOp $R2 $R2 * 2
stable_restore_query:
  System::Alloc $R2
  Pop $R6
  StrCmp $R6 0 stable_restore_value_close
  StrCmp $R6 "error" stable_restore_value_close
  System::Call '$SYSDIR\advapi32.dll::RegQueryValueExW(p r3, w r2, p 0, *i 0 R4, p R6, *i R2 R5) i.r4'
  StrCmp $4 0 0 stable_restore_value_free
  StrCmp $R4 $R3 0 stable_restore_value_free
  StrCmp $R5 $R2 0 stable_restore_value_free
  StrCmp $R1 "DWORD" 0 stable_restore_string
  System::Call '*$R6(i .R7)'
  StrCmp $R7 $R0 stable_restore_value_write stable_restore_value_free
stable_restore_string:
  System::Call '$SYSDIR\ntdll.dll::RtlCompareMemory(p R6, w R0, p R5) p.R7'
  StrCmp $R7 $R5 0 stable_restore_value_free
stable_restore_value_write:
  StrCmp $5 2 0 stable_restore_value_delete
  System::Call '$SYSDIR\advapi32.dll::RegSetValueExW(p r3, w r2, i 0, i r6, p r8, i r7) i.r4'
  StrCmp $4 0 stable_restore_value_ok stable_restore_value_free
stable_restore_value_delete:
  System::Call '$SYSDIR\advapi32.dll::RegDeleteValueW(p r3, w r2) i.r4'
  StrCmp $4 0 0 stable_restore_value_free
stable_restore_value_ok:
  StrCpy $9 1
stable_restore_value_free:
  System::Free $R6
stable_restore_value_close:
  System::Call '$SYSDIR\advapi32.dll::RegCloseKey(p r3) i'
stable_restore_value_return:
  Push "$9"
  System::Store "l"
FunctionEnd

Function DiscardStableRegistrationSnapshot
  System::Store "s"
  StrCmp $StableRegistryBackup "" stable_discard_registry_done
  StrCmp $StableRegistryBackup 0 stable_discard_registry_done
  StrCmp $StableRegistryBackup "error" stable_discard_registry_done
  StrCpy $0 0
stable_discard_registry_next:
  IntCmp $0 $StableRegistryCaptured stable_discard_registry_buffer 0 stable_discard_registry_buffer
  IntOp $1 $0 * 16
  IntOp $1 $StableRegistryBackup + $1
  System::Call '*$1(i .r2, i, i, p .r3)'
  StrCmp $2 2 0 +2
    System::Free $3
  IntOp $0 $0 + 1
  Goto stable_discard_registry_next
stable_discard_registry_buffer:
  System::Free $StableRegistryBackup
  StrCpy $StableRegistryBackup 0
  StrCpy $StableRegistryCaptured 0
stable_discard_registry_done:
  System::Store "l"
FunctionEnd

Function PrepareStableRegistration
  StrCpy $StableRegistryCaptured 0
  StrCpy $StableRegistryWritten 0
  System::Alloc 192 ; twelve 16-byte slots, used only in this x86 NSIS host
  Pop $StableRegistryBackup
  StrCmp $StableRegistryBackup 0 stable_capture_abort
  StrCmp $StableRegistryBackup "error" stable_capture_abort
  !insertmacro StableRegistrationValues CaptureStableRegistrationValue
  ClearErrors
  CreateShortcut "$PreviewStageRoot\.setup-start.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  IfErrors stable_capture_abort
  CreateShortcut "$PreviewStageRoot\.setup-remove.lnk" "$INSTDIR\Uninstall.exe"
  IfErrors stable_capture_abort
  Push "$PreviewStageRoot\.setup-start.lnk"
  Call ReadStableStagedSHA256
  Pop $StableStartLinkSHA256
  StrCmp $StableStartLinkSHA256 "" stable_capture_abort
  Push "$PreviewStageRoot\.setup-remove.lnk"
  Call ReadStableStagedSHA256
  Pop $StableRemoveLinkSHA256
  StrCmp $StableRemoveLinkSHA256 "" stable_capture_abort
  Return
stable_capture_abort:
  DetailPrint "Setup could not snapshot the current Snip Studio registration."
  Call AbortPreviewStaging
FunctionEnd

; Stable keeps original shortcuts because both target paths are unchanged.
; Return 2 for a link we created, 1 for a link already in place, 0 on error.
Function CreateMissingStableLink
  System::Store "s"
  Pop $0 ; live shortcut
  Pop $1 ; staged shortcut
  StrCpy $9 0
  Push "$0"
  Call IsSafePreviewPath
  Pop $2
  StrCmp $2 1 0 stable_link_create_done
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r2 ?e'
  Pop $3
  StrCmp $2 "error" stable_link_create_done
  StrCmp $2 -1 stable_link_missing
  IntOp $2 $2 & 0x410
  StrCmp $2 0 0 stable_link_create_done
  StrCpy $9 1
  Goto stable_link_create_done
stable_link_missing:
  StrCmp $3 2 stable_link_copy
  StrCmp $3 3 0 stable_link_create_done
stable_link_copy:
  System::Call '$SYSDIR\kernel32.dll::CopyFileW(w r1, w r0, i 1) i.r2'
  StrCmp $2 1 0 stable_link_create_done
  StrCpy $9 2
stable_link_create_done:
  Push "$9"
  System::Store "l"
FunctionEnd

Function RemoveCreatedStableLink
  System::Store "s"
  Pop $R0 ; exact link SHA-256
  Pop $0 ; live shortcut
  StrCpy $9 0
  Push "$0"
  Call IsSafePreviewPath
  Pop $2
  StrCmp $2 1 0 stable_link_remove_done
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r0, i 0x80010000, i 1, p 0, i 3, i 0x00200000, p 0) p.r3 ?e'
  Pop $2
  StrCmp $3 -1 stable_link_already_absent
  StrCmp $3 0 stable_link_remove_done
  StrCmp $3 "error" stable_link_remove_done
  System::Alloc 128
  Pop $4
  StrCmp $4 0 stable_link_close
  StrCmp $4 "error" stable_link_close
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 9, p r4, i 8) i.r5'
  StrCmp $5 1 0 stable_link_free
  System::Call '*$4(i .r5, i)'
  IntOp $5 $5 & 0x410
  StrCmp $5 0 0 stable_link_free
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 7, p r4, i 128) i.r5'
  StrCmp $5 1 0 stable_link_free
  System::Call '*$4(i .r6, i .r7)'
  StrCmp $6 0 0 stable_link_free
  StrCmp $7 14 0 stable_link_free
  IntOp $5 $4 + 24
  System::Call '$SYSDIR\kernel32.dll::CompareStringOrdinal(p r5, i 7, w "::$$DATA", i 7, i 0) i.r5'
  StrCmp $5 2 0 stable_link_free
  Push "$3"
  Call ReadPreviewRetiredSHA256
  Pop $5
  StrCmp $5 "$R0" 0 stable_link_free
  System::Call '*$4(i 1)'
  System::Call '$SYSDIR\kernel32.dll::SetFileInformationByHandle(p r3, i 4, p r4, i 1) i.r5'
  StrCmp $5 1 0 stable_link_free
  StrCpy $9 1
stable_link_free:
  System::Free $4
stable_link_close:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i'
  Goto stable_link_remove_done
stable_link_already_absent:
  StrCmp $2 2 stable_link_remove_ok
  StrCmp $2 3 0 stable_link_remove_done
stable_link_remove_ok:
  StrCpy $9 1
stable_link_remove_done:
  Push "$9"
  System::Store "l"
FunctionEnd

Function WriteStableRegistration
  StrCpy $StableRegistrationActive 1
  Push "$SMPROGRAMS\Automation Forge\${APP_NAME}"
  Call IsSafePreviewPath
  Pop $0
  StrCmp $0 1 0 stable_write_registration_failed
  ClearErrors
  CreateDirectory "$SMPROGRAMS\Automation Forge\${APP_NAME}"
  IfErrors stable_write_registration_failed
  Push "$PreviewStageRoot\.setup-start.lnk"
  Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk"
  Call CreateMissingStableLink
  Pop $StableStartLinkCreated
  StrCmp $StableStartLinkCreated 0 stable_write_registration_failed
  Push "$PreviewStageRoot\.setup-remove.lnk"
  Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  Call CreateMissingStableLink
  Pop $StableRemoveLinkCreated
  StrCmp $StableRemoveLinkCreated 0 stable_write_registration_failed
  !insertmacro StableRegistrationValues WriteStableRegistrationValue
  ClearErrors
  Return
stable_write_registration_failed:
  SetErrors
FunctionEnd

Function RollBackStableRegistration
  StrCpy $StableRegistrationRecovered 1
  !insertmacro StableRegistrationValues RestoreStableRegistrationValue
  ; If Setup created previously absent keys, remove them only if empty.
  System::Call '*$StableRegistryBackup(i .r0)'
  StrCmp $0 0 0 +2
    DeleteRegKey /ifempty HKCU "${APP_KEY}"
  IntOp $0 $StableRegistryBackup + 16
  System::Call '*$0(i .r0)'
  StrCmp $0 0 0 +2
    DeleteRegKey /ifempty HKCU "${ARP_KEY}"
  ${If} $StableStartLinkCreated == 2
    Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk"
    Push "$StableStartLinkSHA256"
    Call RemoveCreatedStableLink
    Pop $0
    ${If} $0 != 1
      StrCpy $StableRegistrationRecovered 0
    ${EndIf}
  ${EndIf}
  ${If} $StableRemoveLinkCreated == 2
    Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
    Push "$StableRemoveLinkSHA256"
    Call RemoveCreatedStableLink
    Pop $0
    ${If} $0 != 1
      StrCpy $StableRegistrationRecovered 0
    ${EndIf}
  ${EndIf}
  StrCpy $StableRegistrationActive 0
FunctionEnd

Function AbortStableRegistration
  Call RollBackStableRegistration
  StrCmp $StableRegistrationRecovered 1 stable_restore_original_files
  DetailPrint "Windows registration recovery is incomplete. Keep the transaction files at $PreviewTransactionRoot."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Setup could not restore the former Windows registration. Keep the recovery files at $PreviewTransactionRoot and contact support."
  SetErrorLevel 2
  Abort
stable_restore_original_files:
  DetailPrint "The previous Windows registration was restored."
  Call RollBackPreviewDeployment
  Abort "Windows registration could not finish. See Setup details for file recovery results."
FunctionEnd
!endif

Function PublishPreviewStagedFile
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  StrCpy $1 "$INSTDIR\$0"
  StrCpy $2 "$PreviewStageRoot\$0"
  StrCpy $3 "$PreviewBackupRoot\$0"
  !ifdef SNIP_STUDIO_STABLE
    Push $5
    StrCpy $5 $0
  !endif
  Push "$1"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 publish_file_failed
  Push "$2"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 publish_file_failed
  Push "$3"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 publish_file_failed
  ${GetParent} "$1" $4
  ClearErrors
  CreateDirectory "$4"
  IfErrors publish_file_failed
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r1) i.r4 ?e'
  Pop $0
  StrCmp $4 "error" publish_file_failed
  StrCmp $4 -1 0 publish_file_exists
  StrCmp $0 2 publish_staged
  StrCmp $0 3 publish_staged publish_file_failed
publish_file_exists:
  ; If content arrived in a fresh destination after the scan, preserve it.
  StrCmp $PreviewDestinationOwned 1 0 publish_file_failed
  IntOp $4 $4 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
  StrCmp $4 0 0 publish_file_failed
  !ifdef SNIP_STUDIO_STABLE
    ; A legacy file still present either was not owned or could not be moved.
    ; Preserve it and roll back. For a reinstall, check exact staged bytes.
    StrCmp $StableLegacyUpgrade 1 publish_file_failed
    Push "$2"
    Push "$5"
    Call BackUpIdenticalStableFile
    StrCmp $StableOriginalMoved 1 0 publish_file_failed
  !else
    ${GetParent} "$3" $4
    ClearErrors
    CreateDirectory "$4"
    IfErrors publish_file_failed
    ClearErrors
    Rename "$1" "$3"
    IfErrors publish_file_failed
  !endif
publish_staged:
  StrCmp $PreviewHoldExecutable 1 publish_file_done
  ClearErrors
  Rename "$2" "$1"
  IfErrors publish_file_failed
publish_file_done:
  !ifdef SNIP_STUDIO_STABLE
    Pop $5
  !endif
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  Return
publish_file_failed:
  DetailPrint "The staged file could not be activated: $1"
  !ifdef SNIP_STUDIO_STABLE
    DetailPrint "Any unrecognized file at that path has been kept."
    Pop $5
  !endif
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  !ifdef SNIP_STUDIO_STABLE
    StrCmp $StableRegistrationActive 1 0 +2
      Call AbortStableRegistration
  !endif
  Call RollBackPreviewDeployment
  Abort "The update was interrupted. See Setup details for recovery information."
FunctionEnd

Function RestorePreviewOriginalFile
  !ifdef SNIP_STUDIO_STABLE
    Exch $5 ; expected hash of a file activated by this Setup
    Exch
  !endif
  Exch $0
  Push $1
  Push $2
  Push $3
  Push $4
  StrCpy $1 "$INSTDIR\$0"
  StrCpy $2 "$PreviewStageRoot\$0"
  StrCpy $3 "$PreviewBackupRoot\$0"
  ; A staged file still present was never activated. A backup can exist if
  ; the old file moved but activation of the new one subsequently failed.
  IfFileExists "$2" restore_backup
  IfFileExists "$1" 0 restore_backup
  Push "$1"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 restore_file_failed
  Push "$2"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 restore_file_failed
  !ifdef SNIP_STUDIO_STABLE
    ; Missing staging bytes alone do not prove we own the destination. Keep
    ; a competing file and any old backup intact if exact ownership fails.
    Push "$PreviewBackupRoot"
    StrCpy $PreviewBackupRoot "$PreviewStageRoot"
    Push "$0"
    Push "$5"
    Call RetirePreviewInstalledFile
    Pop $PreviewBackupRoot
    StrCmp $StableOriginalMoved 1 0 restore_file_failed
  !else
    ClearErrors
    Rename "$1" "$2"
    IfErrors restore_file_failed
  !endif
restore_backup:
  IfFileExists "$3" 0 restore_file_done
  Push "$1"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 restore_file_failed
  Push "$3"
  Call IsSafePreviewPath
  Pop $4
  StrCmp $4 1 0 restore_file_failed
  ClearErrors
  Rename "$3" "$1"
  IfErrors restore_file_failed
  Goto restore_file_done
restore_file_failed:
  StrCpy $PreviewRollbackFailed 1
  DetailPrint "The previous file could not be restored: $1"
restore_file_done:
  Pop $4
  Pop $3
  Pop $2
  Pop $1
  Pop $0
  !ifdef SNIP_STUDIO_STABLE
    Pop $5
  !endif
FunctionEnd

Function RollBackPreviewDeployment
  StrCpy $PreviewRollbackFailed 0
  !ifdef SNIP_STUDIO_STABLE
    !define STABLE_RESTORE_EXISTING
    !include "${PAYLOAD_UNINSTALL_INCLUDE}"
    !undef STABLE_RESTORE_EXISTING
  !else
    !define PREVIEW_SKIP_EXECUTABLE
    !define PREVIEW_FILE_CHECK "RestorePreviewOriginalFile"
    !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
    !undef PREVIEW_FILE_CHECK
    !undef PREVIEW_SKIP_EXECUTABLE
  !endif
  Push "Uninstall.exe"
  !ifdef SNIP_STUDIO_STABLE
    Push "$StableUninstallerSHA256"
  !endif
  Call RestorePreviewOriginalFile
  !define PREVIEW_RESTORE_RETIRED
  !include "${RETIRED_PAYLOAD_INCLUDE}"
  !undef PREVIEW_RESTORE_RETIRED
  StrCmp $PreviewRollbackFailed 0 0 rollback_files_kept
  Push "${APP_EXE}"
  !ifdef SNIP_STUDIO_STABLE
    Push "${APP_SHA256}"
  !endif
  Call RestorePreviewOriginalFile
  StrCmp $PreviewRollbackFailed 0 0 rollback_files_kept
  Call DiscardPreviewStage
  DetailPrint "Previous application files were restored."
  SetErrorLevel 2
  Return
rollback_files_kept:
  DetailPrint "Automatic recovery could not finish. Preserve the recovery files at $PreviewTransactionRoot."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Automatic recovery could not finish. Keep the recovery files at $PreviewTransactionRoot and contact support."
  SetErrorLevel 2
FunctionEnd

Section "${APP_NAME} (required)" MainApplication
  SectionIn RO
  Call CheckPreviewDestination
  SetShellVarContext current
  SetRegView 64
  DetailPrint "Preparing the new application files..."
  Call PreparePreviewStage
  ; Retain the exact published archive paths while extracting into staging.
  StrCpy $INSTDIR $PreviewStageRoot
  !include "${PAYLOAD_INSTALL_INCLUDE}"
  ClearErrors
  SetOutPath "$INSTDIR"
  IfErrors 0 +2
    Call AbortPreviewStaging
  ClearErrors
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  IfErrors 0 +2
    Call AbortPreviewStaging
  !ifdef SNIP_STUDIO_STABLE
    Push "$INSTDIR\Uninstall.exe"
    Call ReadStableStagedSHA256
    Pop $StableUninstallerSHA256
    StrCmp $StableUninstallerSHA256 "" 0 +2
      Call AbortPreviewStaging
  !endif
  StrCpy $INSTDIR $PreviewInstallRoot
  SetOutPath "$INSTDIR"
  DetailPrint "Checking the installation and waiting for any running copy to exit..."
  Call CloseInstalledPreviewForUpdate
  Call CheckInstalledPreviewFiles
  Call CheckPreviewRetirementOwner
  !ifdef SNIP_STUDIO_STABLE
    Call PrepareStableRegistration
  !endif
  ; Keep the launcher absent while any installed dependency may be changing.
  DetailPrint "Applying the update..."
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      Push "${APP_EXE}"
      Push "${PREVIOUS_APP_SHA256}|370991B1BEE29F8B3AAC29C95323438AD9B8BEA94B3859C008FE3AD38AF8CD5C|291BFEBCA716AC8811D2CBE09F0FEBC7D3531C4303F0E3B797108E110001B81E"
      Call RetirePreviewInstalledFile
    ${EndIf}
  !endif
  StrCpy $PreviewHoldExecutable 1
  Push "${APP_EXE}"
  Call PublishPreviewStagedFile
  StrCpy $PreviewHoldExecutable 0
  !ifdef SNIP_STUDIO_STABLE
    ${If} $StableLegacyUpgrade == 1
      !include "${LEGACY_OVERLAP_INCLUDE}"
    ${EndIf}
  !endif
  ${If} $PreviewRetireReleaseFiles == 1
    !define PREVIEW_RETIRE_EXISTING
    !include "${RETIRED_PAYLOAD_INCLUDE}"
    !undef PREVIEW_RETIRE_EXISTING
  ${EndIf}
  !define PREVIEW_SKIP_EXECUTABLE
  !define PREVIEW_FILE_CHECK "PublishPreviewStagedFile"
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  !undef PREVIEW_FILE_CHECK
  !undef PREVIEW_SKIP_EXECUTABLE
  Push "Uninstall.exe"
  Call PublishPreviewStagedFile
  !ifndef SNIP_STUDIO_STABLE
  Push "${APP_EXE}"
  Call PublishPreviewStagedFile
  !endif
  ${If} $PreviewRetireReleaseFiles == 1
    !define PREVIEW_REMOVE_RETIRED_DIRECTORIES
    !include "${RETIRED_PAYLOAD_INCLUDE}"
    !undef PREVIEW_REMOVE_RETIRED_DIRECTORIES
  ${EndIf}
  ; Keep the predecessor files until every required registration step succeeds.
  !ifdef SNIP_STUDIO_STABLE
    DetailPrint "Registering Snip Studio with Windows..."
    Call WriteStableRegistration
    IfErrors preview_registration_failed
    Push "${APP_EXE}"
    Call PublishPreviewStagedFile
    StrCpy $StableRegistrationActive 0
  !else
    DetailPrint "Registering the preview with Windows..."
  ClearErrors
  CreateDirectory "$SMPROGRAMS\Automation Forge\${APP_NAME}"
  IfErrors preview_registration_failed
  CreateShortcut "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  IfErrors preview_registration_failed
  CreateShortcut "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${APP_KEY}" "InstallLocation" "$INSTDIR"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "${APP_NAME}"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "DisplayVersion" "${APP_VERSION}"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "Publisher" "Automation Forge LLC"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "URLInfoAbout" "${APP_RELEASE_URL}"
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  IfErrors preview_registration_failed
  WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  IfErrors preview_registration_failed
  WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
  IfErrors preview_registration_failed
  WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
  IfErrors preview_registration_failed
  WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" ${PACKAGE_KB}
  IfErrors preview_registration_failed
  !endif
  DetailPrint "Cleaning up temporary setup files..."
  Call DiscardPreviewStage
  StrCmp $PreviewStageCleanupFailed 0 preview_registration_done
  SetErrorLevel 2
  Abort "${APP_NAME} was registered. Temporary file cleanup remains incomplete. See Setup details."
preview_registration_done:
  SetErrorLevel 0
  Goto preview_install_finished
preview_registration_failed:
  !ifdef SNIP_STUDIO_STABLE
    Call AbortStableRegistration
  !else
  DetailPrint "Windows registration could not finish. Keep the transaction files at $PreviewTransactionRoot."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Windows registration could not finish after deploying ${APP_NAME}.$\r$\n$\r$\nKeep the files at $PreviewTransactionRoot and contact support before retrying."
  SetErrorLevel 2
  Abort "Windows registration could not finish after deploying ${APP_NAME}. See Setup details."
  !endif
preview_install_finished:
SectionEnd

Section /o "Desktop shortcut" DesktopShortcut
  ClearErrors
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  IfErrors 0 +3
    SetErrorLevel 2
    Abort "${APP_NAME} was installed. The desktop shortcut could not be created."
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  Call un.AcquirePreviewDeploymentLock
  StrCpy $0 "$INSTDIR\${PREVIEW_TRANSACTION_FOLDER}"
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r0 ?e'
  Pop $1
  StrCmp $0 -1 0 uninstall_recovery_pending
  StrCmp $1 2 uninstall_ready
  StrCmp $1 3 uninstall_ready
uninstall_recovery_pending:
  DetailPrint "An earlier Snip Studio transaction requires review at $INSTDIR\${PREVIEW_TRANSACTION_FOLDER}."
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "An earlier Snip Studio installation may require recovery. Keep the files at $INSTDIR\${PREVIEW_TRANSACTION_FOLDER} and contact support before uninstalling."
  SetErrorLevel 2
  Abort
uninstall_ready:
FunctionEnd

Function un.AbortPreviewCleanup
  Pop $0
  DetailPrint "Could not finish uninstallation: $0"
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Uninstall could not finish.$\r$\n$\r$\n$0$\r$\n$\r$\nClose programs using this item or check its permissions, then retry from $INSTDIR\Uninstall.exe."
  SetErrorLevel 2
  Abort
FunctionEnd

Function un.RemoveCheckedPreviewFile
  System::Store "s"
  Pop $0
  Push "$0"
  Call un.IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 cleanup_file_failed
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r1 ?e'
  Pop $2
  StrCmp $1 -1 cleanup_file_absent
  StrCmp $1 "error" cleanup_file_failed
  IntOp $3 $1 & 0x10 ; FILE_ATTRIBUTE_DIRECTORY
  StrCmp $3 0 0 cleanup_file_failed
  ClearErrors
  Delete "$0"
  System::Call '$SYSDIR\kernel32.dll::GetFileAttributesW(w r0) i.r1 ?e'
  Pop $2
  StrCmp $1 -1 0 cleanup_file_failed
cleanup_file_absent:
  StrCmp $2 2 cleanup_file_done ; ERROR_FILE_NOT_FOUND
  StrCmp $2 3 cleanup_file_done ; ERROR_PATH_NOT_FOUND
cleanup_file_failed:
  Push "$0"
  Call un.AbortPreviewCleanup
cleanup_file_done:
  System::Store "l"
FunctionEnd

; ReadRegStr handles decoding. If it reports an error, distinguish a missing
; key/value from registry access failure or data that could not be read.
Function un.ReadCheckedPreviewString
  System::Store "s"
  Pop $0 ; value
  Pop $1 ; HKCU subkey
  ClearErrors
  ReadRegStr $2 HKCU "$1" "$0"
  IfErrors 0 cleanup_string_present
  ; KEY_QUERY_VALUE | KEY_WOW64_64KEY, matching SetRegView 64.
  System::Call '$SYSDIR\advapi32.dll::RegOpenKeyExW(p 0x80000001, w r1, i 0, i 0x0101, *p 0 r3) i.r4'
  StrCmp $4 2 cleanup_string_no_key
  StrCmp $4 0 0 cleanup_string_failed
  System::Call '$SYSDIR\advapi32.dll::RegQueryValueExW(p r3, w r0, p 0, p 0, p 0, p 0) i.r4'
  System::Call '$SYSDIR\advapi32.dll::RegCloseKey(p r3) i'
  StrCmp $4 2 cleanup_string_no_value cleanup_string_failed
cleanup_string_present:
  Push "$2"
  Push "present"
  Goto cleanup_string_done
cleanup_string_no_key:
  Push ""
  Push "no-key"
  Goto cleanup_string_done
cleanup_string_no_value:
  Push ""
  Push "no-value"
  Goto cleanup_string_done
cleanup_string_failed:
  Push "HKCU\$1 ($0)"
  Call un.AbortPreviewCleanup
cleanup_string_done:
  System::Store "l"
FunctionEnd

Function un.PreviewRegistrationOwner
  System::Store "s"
  Pop $0
  Push "$0"
  Push "InstallLocation"
  Call un.ReadCheckedPreviewString
  Pop $1
  Pop $2
  StrCmp $1 "no-key" cleanup_owner_absent
  StrCmp $1 "present" 0 cleanup_owner_failed
  StrCpy $3 $2 2 1
  StrCmp $3 ":\" 0 cleanup_owner_failed
  ClearErrors
  GetFullPathName $2 "$2"
  IfErrors cleanup_owner_failed
  GetFullPathName $3 "$INSTDIR"
  IfErrors cleanup_owner_failed
  StrCmp $2 $3 cleanup_owner_ours
  Push "other"
  Goto cleanup_owner_done
cleanup_owner_ours:
  Push "ours"
  Goto cleanup_owner_done
cleanup_owner_absent:
  Push "absent"
  Goto cleanup_owner_done
cleanup_owner_failed:
  Push "HKCU\$0 (InstallLocation)"
  Call un.AbortPreviewCleanup
cleanup_owner_done:
  System::Store "l"
FunctionEnd

Function un.RemoveOwnedPreviewRegistration
  System::Store "s"
  Pop $0
  Push "$0"
  Call un.PreviewRegistrationOwner
  Pop $1
  StrCmp $1 "ours" 0 cleanup_registration_done
  ; The keys are specific to this installation. Refuse recursive deletion.
  System::Call '$SYSDIR\advapi32.dll::RegDeleteKeyExW(p 0x80000001, w r0, i 0x0100, i 0) i.r1'
  StrCmp $1 0 cleanup_registration_done
  StrCmp $1 2 cleanup_registration_done
  Push "HKCU\$0 (Windows error $1)"
  Call un.AbortPreviewCleanup
cleanup_registration_done:
  System::Store "l"
FunctionEnd

!ifdef SNIP_STUDIO_STABLE
Function un.VerifyStableUninstallOwnership
  Push "${APP_KEY}"
  Call un.PreviewRegistrationOwner
  Pop $0
  StrCmp $0 "ours" stable_uninstall_check_arp
  StrCmp $0 "absent" 0 stable_uninstall_wrong_release
stable_uninstall_check_arp:
  Push "${ARP_KEY}"
  Call un.PreviewRegistrationOwner
  Pop $0
  StrCmp $0 "ours" 0 stable_uninstall_wrong_release
  Push "${ARP_KEY}"
  Push "DisplayVersion"
  Call un.ReadCheckedPreviewString
  Pop $0
  Pop $1
  StrCmp $0 "present" 0 stable_uninstall_wrong_release
  StrCmpS $1 "${APP_VERSION}" 0 stable_uninstall_wrong_release
  StrCpy $0 "$INSTDIR\${APP_EXE}"
  Push "$0"
  Call un.IsSafePreviewPath
  Pop $1
  StrCmp $1 1 0 stable_uninstall_wrong_release
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r0, i 0x80000000, i 1, p 0, i 3, i 0x00200000, p 0) p.r2 ?e'
  Pop $3
  StrCmp $2 -1 stable_uninstall_launcher_missing
  StrCmp $2 0 stable_uninstall_wrong_release
  StrCmp $2 "error" stable_uninstall_wrong_release
  Push "$2"
  Call un.ReadPreviewRetiredSHA256
  Pop $4
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r2) i'
  StrCmp $4 "${APP_SHA256}" stable_uninstall_owned stable_uninstall_wrong_release
stable_uninstall_launcher_missing:
  ; A previous removal can have already removed the launcher while the
  ; exact version remains registered. Every remaining file still needs proof.
  StrCmp $3 2 stable_uninstall_owned
stable_uninstall_wrong_release:
  Push "The registered Snip Studio release does not match this uninstaller. No files were removed."
  Call un.AbortPreviewCleanup
stable_uninstall_owned:
FunctionEnd

Function un.RemoveStablePayloadFile
  System::Store "s"
  Pop $R0 ; exact candidate SHA-256
  Pop $0 ; exact manifest entry
  StrCpy $1 "$INSTDIR\$0"
  Push "$1"
  Call un.IsSafePreviewPath
  Pop $2
  StrCmp $2 1 0 stable_remove_kept
  ; Lock ordinary writes and competing renames before checking the bytes.
  System::Call '$SYSDIR\kernel32.dll::CreateFileW(w r1, i 0x80010000, i 1, p 0, i 3, i 0x00200000, p 0) p.r3 ?e'
  Pop $2
  StrCmp $3 -1 stable_remove_absent
  StrCmp $3 0 stable_remove_kept
  StrCmp $3 "error" stable_remove_kept
  System::Alloc 128
  Pop $4
  StrCmp $4 0 stable_remove_close
  StrCmp $4 "error" stable_remove_close
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 9, p r4, i 8) i.r5'
  StrCmp $5 1 0 stable_remove_free
  System::Call '*$4(i .r5, i)'
  IntOp $5 $5 & 0x410 ; FILE_ATTRIBUTE_DIRECTORY | FILE_ATTRIBUTE_REPARSE_POINT
  StrCmp $5 0 0 stable_remove_free
  System::Call '$SYSDIR\kernel32.dll::GetFileInformationByHandleEx(p r3, i 7, p r4, i 128) i.r5'
  StrCmp $5 1 0 stable_remove_free
  System::Call '*$4(i .r6, i .r7)'
  StrCmp $6 0 0 stable_remove_free ; NextEntryOffset
  StrCmp $7 14 0 stable_remove_free ; 7 UTF-16 characters in ::$DATA
  IntOp $5 $4 + 24
  System::Call '$SYSDIR\kernel32.dll::CompareStringOrdinal(p r5, i 7, w "::$$DATA", i 7, i 0) i.r5'
  StrCmp $5 2 0 stable_remove_free
  Push "$3"
  Call un.ReadPreviewRetiredSHA256
  Pop $5
  StrCmp $5 "$R0" 0 stable_remove_free
  System::Call '*$4(i 1)' ; FILE_DISPOSITION_INFO.DeleteFile
  System::Call '$SYSDIR\kernel32.dll::SetFileInformationByHandle(p r3, i 4, p r4, i 1) i.r5'
  StrCmp $5 1 0 stable_remove_free
  System::Free $4
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i'
  Goto stable_remove_done
stable_remove_free:
  System::Free $4
stable_remove_close:
  System::Call '$SYSDIR\kernel32.dll::CloseHandle(p r3) i'
  Goto stable_remove_kept
stable_remove_absent:
  StrCmp $2 2 stable_remove_done
  StrCmp $2 3 stable_remove_done
stable_remove_kept:
  DetailPrint "Kept a file that could not be verified and removed: $1"
stable_remove_done:
  System::Store "l"
FunctionEnd

Function un.RemoveOwnedLegacyImageRegistration
  Push "Software\Classes\AutomationForge.SnipStudio.Image\shell\open\command"
  Push ""
  Call un.ReadCheckedPreviewString
  Pop $0
  Pop $1
  StrCmp $0 "present" 0 stable_image_registration_done
  StrCmp $1 '$\"$INSTDIR\${APP_EXE}$\" $\"%1$\"' 0 stable_image_registration_done
  DeleteRegValue HKCU "Software\Classes\.png\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.jpg\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.jpeg\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.bmp\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.gif\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.tif\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  DeleteRegValue HKCU "Software\Classes\.tiff\OpenWithProgids" "AutomationForge.SnipStudio.Image"
  ; Remove our exact open command and leave other verbs or additions intact.
  DeleteRegValue HKCU "Software\Classes\AutomationForge.SnipStudio.Image\shell\open\command" ""
  DeleteRegKey /ifempty HKCU "Software\Classes\AutomationForge.SnipStudio.Image\shell\open\command"
  DeleteRegKey /ifempty HKCU "Software\Classes\AutomationForge.SnipStudio.Image\shell\open"
  DeleteRegKey /ifempty HKCU "Software\Classes\AutomationForge.SnipStudio.Image\shell"
  System::Call '$SYSDIR\shell32.dll::SHChangeNotify(i 0x08000000, i 0, p 0, p 0)'
stable_image_registration_done:
FunctionEnd
!endif

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  !ifdef SNIP_STUDIO_STABLE
    Call un.VerifyStableUninstallOwnership
  !endif
  Call un.CheckInstalledPreviewFiles
  !include "${PAYLOAD_UNINSTALL_INCLUDE}"
  !ifdef SNIP_STUDIO_STABLE
    Call un.RemoveOwnedLegacyImageRegistration
  !endif
  ; Remove only this installation's exact optional sign-in command.
  Push "Software\Microsoft\Windows\CurrentVersion\Run"
  Push "${APP_STARTUP_VALUE}"
  Call un.ReadCheckedPreviewString
  Pop $1
  Pop $2
  StrCmpS $2 '$\"$INSTDIR\${APP_EXE}$\" --background' 0 startup_done
  ClearErrors
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "${APP_STARTUP_VALUE}"
  Push "Software\Microsoft\Windows\CurrentVersion\Run"
  Push "${APP_STARTUP_VALUE}"
  Call un.ReadCheckedPreviewString
  Pop $1
  Pop $2
  StrCmpS $2 '$\"$INSTDIR\${APP_EXE}$\" --background' 0 startup_done
  Push "the Snip Studio sign-in startup entry"
  Call un.AbortPreviewCleanup
startup_done:
  Push "${APP_KEY}"
  Call un.PreviewRegistrationOwner
  Pop $0
  Push "${ARP_KEY}"
  Call un.PreviewRegistrationOwner
  Pop $1
  ; Shared shortcut names belong to the registered installation.
  StrCmp $0 "other" shortcuts_done
  StrCmp $1 "other" shortcuts_done
  StrCmp $0 "ours" remove_shortcuts
  StrCmp $1 "ours" 0 shortcuts_done
remove_shortcuts:
  Push "$DESKTOP\${APP_NAME}.lnk"
  Call un.RemoveCheckedPreviewFile
  Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk"
  Call un.RemoveCheckedPreviewFile
  Push "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  Call un.RemoveCheckedPreviewFile
  RMDir "$SMPROGRAMS\Automation Forge\${APP_NAME}"
shortcuts_done:
  Push "${APP_KEY}"
  Call un.RemoveOwnedPreviewRegistration
  Push "${ARP_KEY}"
  Call un.RemoveOwnedPreviewRegistration
  ; Retain the installed uninstaller until the known cleanup is complete.
  Push "$INSTDIR\Uninstall.exe"
  Call un.RemoveCheckedPreviewFile
  ; Generated directory removal is nonrecursive and removes empty folders only.
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
