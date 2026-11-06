Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"
!include "FileFunc.nsh"

Var PreflightCheckOnly

!ifndef RELEASE_DIR
  !define RELEASE_DIR "${__FILEDIR__}\..\release-0.5.4-signed"
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
OutFile "${OUTPUT_DIR}\SnipStudio-0.5.4-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Automation Forge\Snip Studio"
InstallDirRegKey HKCU "Software\Automation Forge\Snip Studio" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 16
ShowInstDetails show
ShowUninstDetails show
BrandingText "Automation Forge"
VIProductVersion "0.5.4.0"
VIAddVersionKey /LANG=1033 "ProductName" "Snip Studio"
VIAddVersionKey /LANG=1033 "ProductVersion" "0.5.4.0"
VIAddVersionKey /LANG=1033 "FileVersion" "0.5.4.0"
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

; Query only executables in this installation. A portable copy elsewhere
; must not block an update. Restart Manager is used only to identify use: no
; shutdown, restart, process termination, or changes to the running app occur.
; Keep this check before every install/uninstall file or registry mutation.
!macro DefineInstalledApplicationPreflight Prefix ActionText
Function ${Prefix}CheckInstalledApplication
  System::Store "s"
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
    MessageBox MB_RETRYCANCEL|MB_ICONEXCLAMATION "Snip Studio files are still in use from this installation.$\r$\n$\r$\nBefore ${ActionText}, save your snips and stop any recording. Right-click the Snip Studio icon near the Windows clock and choose Exit Snip Studio. Closing an editor window may leave the shortcut listener running.$\r$\n$\r$\nChoose Retry after Snip Studio has exited, or Cancel to leave this installation unchanged." /SD IDCANCEL IDRETRY preflight_retry
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
  !include "${TRANSLATION_INSTALL_INCLUDE}"
  SetOutPath "$INSTDIR"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Automation Forge\Snip Studio"
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Snip Studio.lnk" "$INSTDIR\Snip Studio.exe" "" "$INSTDIR\Snip Studio.exe" 0
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Uninstall Snip Studio.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\Automation Forge\Snip Studio" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayName" "Snip Studio"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayVersion" "0.5.4"
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
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
  Call un.CheckInstalledApplication
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
