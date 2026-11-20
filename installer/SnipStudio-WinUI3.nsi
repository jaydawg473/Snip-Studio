; Compile through the signed preview packaging gate. The three generated
; includes enumerate the same verified payload; no wildcard copying/removal.
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "x64.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.7.0-preview.5"
!endif
!ifndef APP_VERSION_NUMERIC
  !define APP_VERSION_NUMERIC "0.7.0.5"
!endif
!define APP_NAME "Snip Studio WinUI 3 Preview"
!define APP_EXE "SnipStudio.WinUI3.Preview.exe"
!define APP_KEY "Software\Automation Forge\Snip Studio WinUI 3 Preview"
!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio.WinUI3.Preview"

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
!ifndef APP_SHA256
  !error "The selected signed application hash is required."
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
OutFile "${OUTPUT_DIR}\SnipStudio-WinUI3-${APP_VERSION}-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Automation Forge\${APP_NAME}"
InstallDirRegKey HKCU "${APP_KEY}" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
CRCCheck force
SetCompressor /SOLID lzma
SetCompressorDictSize 16
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
!define MUI_WELCOMEPAGE_TEXT "Install the WinUI 3 preview for your Windows account.$\r$\n$\r$\nPreview version: ${APP_VERSION}.$\r$\n$\r$\nOpen the application from the Start menu after installation."
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
  ${IfNot} ${RunningX64}
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "This preview installer contains the x64 application."
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

; A selected folder must be empty or contain this preview's own application.
; This avoids placing preview runtime files into another application's folder.
Function CheckPreviewDestination
  Push $0
  Push $1
  IfFileExists "$INSTDIR\${APP_EXE}" destination_ok
  ClearErrors
  FindFirst $0 $1 "$INSTDIR\*"
  IfErrors destination_ok
destination_next:
  StrCmp $1 "" destination_empty
  StrCmp $1 "." destination_continue
  StrCmp $1 ".." destination_continue
  FindClose $0
  Pop $1
  Pop $0
  IfSilent +2
    MessageBox MB_OK|MB_ICONEXCLAMATION "Choose an empty folder or the folder containing ${APP_NAME}."
  SetErrorLevel 2
  Abort
destination_continue:
  FindNext $0 $1
  Goto destination_next
destination_empty:
  FindClose $0
destination_ok:
  Pop $1
  Pop $0
  Call CheckInstalledPreviewFiles
FunctionEnd

; Read-only probes run before any installed file is changed. A busy or
; unwritable file blocks the operation; no process is launched or closed.
!macro DefinePreviewFileCheck Prefix
Function ${Prefix}CheckInstalledFileAvailable
  Exch $0
  Push $1
  IfFileExists "$0" 0 available
  System::Call 'kernel32::CreateFileW(w r0, i 0x40010000, i 7, p 0, i 3, i 0x80, p 0) p.r1'
  ${If} $1 == -1
    DetailPrint "File cannot be updated: $0"
    IfSilent +2
      MessageBox MB_OK|MB_ICONEXCLAMATION "A preview file is in use or cannot be changed.$\r$\n$\r$\nClose ${APP_NAME}, check folder permissions, and retry.$\r$\n$\r$\n$0"
    Pop $1
    Pop $0
    SetErrorLevel 2
    Abort
  ${EndIf}
  System::Call 'kernel32::CloseHandle(p r1) i'
available:
  Pop $1
  Pop $0
FunctionEnd
!macroend
!insertmacro DefinePreviewFileCheck ""
!insertmacro DefinePreviewFileCheck "un."

; Generated includes invoke these macros with validated, escaped literals.
!macro PreviewCheckFile RelativePath
  Push "$INSTDIR\${RelativePath}"
  Call ${PREVIEW_FILE_CHECK}
!macroend

!macro PreviewInstallFile RelativeDirectory FileName SourcePath
  ClearErrors
  SetOutPath "$INSTDIR\${RelativeDirectory}"
  IfErrors 0 +3
    SetErrorLevel 2
    Abort "The preview destination folder could not be created."
  ClearErrors
  File "/oname=${FileName}" "${SourcePath}"
  IfErrors 0 +3
    SetErrorLevel 2
    Abort "A preview file could not be copied. Complete any file-use prompts and retry Setup."
!macroend

!macro PreviewRemoveFile RelativePath
  IfFileExists "$INSTDIR\${RelativePath}" 0 +6
  ClearErrors
  Delete "$INSTDIR\${RelativePath}"
  IfErrors 0 +3
    SetErrorLevel 2
    Abort "A preview file could not be removed. Close the application and retry."
!macroend

Function CheckInstalledPreviewFiles
  !define PREVIEW_FILE_CHECK "CheckInstalledFileAvailable"
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  !undef PREVIEW_FILE_CHECK
  Push "$INSTDIR\Uninstall.exe"
  Call CheckInstalledFileAvailable
FunctionEnd

Function un.CheckInstalledPreviewFiles
  !define PREVIEW_FILE_CHECK "un.CheckInstalledFileAvailable"
  !include "${PAYLOAD_PREFLIGHT_INCLUDE}"
  !undef PREVIEW_FILE_CHECK
  ; NSIS manages its own running uninstaller image; do not probe that image.
FunctionEnd

Section "${APP_NAME} (required)" MainApplication
  SectionIn RO
  Call CheckPreviewDestination
  SetShellVarContext current
  SetRegView 64
  SetOutPath "$INSTDIR"
  !include "${PAYLOAD_INSTALL_INCLUDE}"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  CreateDirectory "$SMPROGRAMS\Automation Forge\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortcut "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "${APP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${ARP_KEY}" "Publisher" "Automation Forge LLC"
  WriteRegStr HKCU "${ARP_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${ARP_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${ARP_KEY}" "URLInfoAbout" "https://github.com/jaydawg473/Snip-Studio-WinUI3"
  WriteRegStr HKCU "${ARP_KEY}" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'
  WriteRegStr HKCU "${ARP_KEY}" "QuietUninstallString" '$\"$INSTDIR\Uninstall.exe$\" /S'
  WriteRegDWORD HKCU "${ARP_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${ARP_KEY}" "NoRepair" 1
  WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" ${PACKAGE_KB}
  SetErrorLevel 0
SectionEnd

Section /o "Desktop shortcut" DesktopShortcut
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

Function un.onInit
  SetShellVarContext current
  SetRegView 64
  Call un.CheckInstalledPreviewFiles
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  SetRegView 64
  Call un.CheckInstalledPreviewFiles
  !include "${PAYLOAD_UNINSTALL_INCLUDE}"
  Delete "$INSTDIR\Uninstall.exe"
  ; Remove only this installation's exact optional sign-in command.
  ReadRegStr $2 HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Snip Studio WinUI3 Preview - Automation Forge"
  StrCmpS $2 '$\"$INSTDIR\${APP_EXE}$\" --background' 0 startup_done
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Snip Studio WinUI3 Preview - Automation Forge"
startup_done:
  ; Preserve a later preview installation's registration and shortcuts.
  ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"
  StrCmp $0 "" registration_done
  GetFullPathName $0 "$0"
  GetFullPathName $1 "$INSTDIR"
  StrCmp $0 $1 0 registration_done
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\Automation Forge\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\Automation Forge\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\Automation Forge\${APP_NAME}"
  DeleteRegKey HKCU "${ARP_KEY}"
  DeleteRegKey HKCU "${APP_KEY}"
registration_done:
  ; Generated directory removal is nonrecursive and removes empty folders only.
  RMDir "$INSTDIR"
  SetErrorLevel 0
SectionEnd
