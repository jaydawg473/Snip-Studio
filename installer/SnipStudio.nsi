Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "WinVer.nsh"

!ifndef RELEASE_DIR
  !define RELEASE_DIR "${__FILEDIR__}\..\release-0.3.0-signed"
!endif
!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "${__FILEDIR__}\output"
!endif
!ifndef PACKAGE_KB
  !define PACKAGE_KB 600
!endif

Name "Snip Studio"
OutFile "${OUTPUT_DIR}\SnipStudio-0.3.0-Setup.exe"
InstallDir "$LOCALAPPDATA\Programs\Automation Forge\Snip Studio"
InstallDirRegKey HKCU "Software\Automation Forge\Snip Studio" "InstallLocation"
RequestExecutionLevel user
ManifestDPIAware true
SetCompressor /SOLID lzma
SetCompressorDictSize 16
ShowInstDetails show
ShowUninstDetails show
BrandingText "Automation Forge"
VIProductVersion "0.3.0.0"
VIAddVersionKey /LANG=1033 "ProductName" "Snip Studio"
VIAddVersionKey /LANG=1033 "ProductVersion" "0.3.0"
VIAddVersionKey /LANG=1033 "FileVersion" "0.3.0.0"
VIAddVersionKey /LANG=1033 "FileDescription" "Snip Studio Setup"
VIAddVersionKey /LANG=1033 "CompanyName" "Automation Forge LLC"
VIAddVersionKey /LANG=1033 "LegalCopyright" "Copyright 2026 Automation Forge LLC"

!define MUI_ICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_UNICON "${__FILEDIR__}\..\assets\SnipStudio.ico"
!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE "Install Snip Studio"
!define MUI_WELCOMEPAGE_TEXT "Capture, annotate, copy text, and record your screen.$\r$\n$\r$\nThis installer installs Snip Studio for your Windows account. It does not start the app or add it to Windows startup.$\r$\n$\r$\nYour screenshots and preferences stay in their current locations."
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

Section "Snip Studio (required)" MainApplication
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"
  SetOverwrite on
  File "${RELEASE_DIR}\Snip Studio.exe"
  File "${RELEASE_DIR}\Snip Studio.exe.config"
  File "${RELEASE_DIR}\README.txt"
  File "${RELEASE_DIR}\VALIDATION.txt"
  File "${RELEASE_DIR}\Signature Verification.json"
  File "${RELEASE_DIR}\SHA256SUMS.txt"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\Automation Forge\Snip Studio"
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Snip Studio.lnk" "$INSTDIR\Snip Studio.exe" "" "$INSTDIR\Snip Studio.exe" 0
  CreateShortcut "$SMPROGRAMS\Automation Forge\Snip Studio\Uninstall Snip Studio.lnk" "$INSTDIR\Uninstall.exe"

  WriteRegStr HKCU "Software\Automation Forge\Snip Studio" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayName" "Snip Studio"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio" "DisplayVersion" "0.3.0"
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

LangString DESC_MainApplication ${LANG_ENGLISH} "Install the application, help files, Start menu shortcut, and uninstaller."
LangString DESC_DesktopShortcut ${LANG_ENGLISH} "Add a shortcut to your desktop. This is optional."
!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${MainApplication} $(DESC_MainApplication)
  !insertmacro MUI_DESCRIPTION_TEXT ${DesktopShortcut} $(DESC_DesktopShortcut)
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Function un.onInit
  SetShellVarContext current
FunctionEnd

Section "Uninstall"
  SetShellVarContext current
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
