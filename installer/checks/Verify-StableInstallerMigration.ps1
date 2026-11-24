param([string]$NsisPath = (Join-Path $PSScriptRoot '../SnipStudio-WinUI3.nsi'))

$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NsisPath))

function Get-InstallerBlock([string]$Text, [string]$Open, [string]$Close) {
    $options = '(?ms)^' + [regex]::Escape($Open) + '\s*\r?$\n(.*?)^' + [regex]::Escape($Close) + '\s*$'
    [regex]::Match($Text, $options).Groups[1].Value
}

function Test-Order([string]$Block, [string[]]$Parts) {
    $position = 0
    foreach ($part in $Parts) {
        $index = $Block.IndexOf($part, $position, [StringComparison]::Ordinal)
        if ($index -lt 0) { return $false }
        $position = $index + $part.Length
    }
    return $true
}

function Get-PolicyFailures([string]$Text) {
    $install = Get-InstallerBlock $Text 'Section "${APP_NAME} (required)" MainApplication' 'SectionEnd'
    $uninstall = Get-InstallerBlock $Text 'Section "Uninstall"' 'SectionEnd'
    $init = Get-InstallerBlock $Text 'Function .onInit' 'FunctionEnd'
    $destination = Get-InstallerBlock $Text 'Function CheckPreviewDestination' 'FunctionEnd'
    $caption = Get-InstallerBlock $Text 'Function ReadStableLegacyBrokerCaption' 'FunctionEnd'
    $find = Get-InstallerBlock $Text 'Function FindInstalledPreviewWindow' 'FunctionEnd'
    $close = Get-InstallerBlock $Text 'Function CloseInstalledPreviewForUpdate' 'FunctionEnd'
    $publisher = Get-InstallerBlock $Text 'Function PublishPreviewStagedFile' 'FunctionEnd'
    $retire = Get-InstallerBlock $Text 'Function RetirePreviewInstalledFile' 'FunctionEnd'
    $restore = Get-InstallerBlock $Text 'Function RestorePreviewOriginalFile' 'FunctionEnd'
    $owned = Get-InstallerBlock $Text 'Function un.VerifyStableUninstallOwnership' 'FunctionEnd'
    $remove = Get-InstallerBlock $Text 'Function un.RemoveStablePayloadFile' 'FunctionEnd'
    $openWith = Get-InstallerBlock $Text 'Function un.RemoveOwnedLegacyImageRegistration' 'FunctionEnd'
    $prepareRegistration = Get-InstallerBlock $Text 'Function PrepareStableRegistration' 'FunctionEnd'
    $writeRegistration = Get-InstallerBlock $Text 'Function WriteStableRegistration' 'FunctionEnd'
    $restoreValue = Get-InstallerBlock $Text 'Function RestoreStableRegistryValue' 'FunctionEnd'
    $abortRegistration = Get-InstallerBlock $Text 'Function AbortStableRegistration' 'FunctionEnd'

    $checks = [ordered]@{
        'Use the original stable install, startup and update identities' = (Test-Order $Text @(
            '!ifdef SNIP_STUDIO_STABLE', '!define APP_NAME "Snip Studio"', '!define APP_EXE "Snip Studio.exe"',
            '!define APP_KEY "Software\Automation Forge\Snip Studio"',
            '!define ARP_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio"',
            '!define APP_SETUP_NAME "SnipStudio"',
            '!define APP_RELEASE_URL "https://github.com/jaydawg473/Snip-Studio"',
            '!define APP_STARTUP_VALUE "Snip Studio - Automation Forge"',
            '!define PREVIEW_EXIT_MESSAGE "AutomationForge.SnipStudio.RequestInstallerExit.v1"'
        ))
        'Reject other processor architectures before acquiring the deployment lock' = (Test-Order $init @(
            '!ifdef SNIP_STUDIO_STABLE', '${IfNot} ${IsNativeAMD64}', 'Abort', 'Call AcquirePreviewDeploymentLock'
        ))
        'Require the original installation path for an existing upgrade' = (Test-Order $destination @(
            'ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"',
            'GetFullPathName $0 "$0"', 'StrCmp $0 $INSTDIR destination_registered_path_done',
            'destination_registered_path_mismatch:', 'Abort'
        ))
        'Recognize the verified public v0.6.21 launcher' = $Text.Contains(
            'StrCmp $5 "291BFEBCA716AC8811D2CBE09F0FEBC7D3531C4303F0E3B797108E110001B81E" 0 destination_owner_free'
        )
        'Resolve the exact current-account broker caption and reject unconfirmed discovery' = (
            (Test-Order $caption @('OpenProcessToken(p r0, i 0x0008', 'GetTokenInformation(p r1, i 1',
                'ConvertSidToStringSidW', 'AutomationForge.SnipStudio.ShortcutHost.v1.$6')) -and
            (Test-Order $find @('${If} $StableLegacyUpgrade == 1',
                'StrCmp $StableLegacyBrokerCaption "" preview_find_uncertain',
                'StrCpy $1 $StableLegacyBrokerCaption', 'FindWindowExW(p -3, p r0, p 0, w r1)',
                'StrCmp $0 "error" preview_find_uncertain', 'StrCmp $0 0 0 preview_find_pid',
                'StrCmp $4 1168 preview_find_done preview_find_uncertain')) -and
            (Test-Order $close @('preview_close_retry:', 'Call ReadStableLegacyBrokerCaption',
                'preview_close_find:', 'Call FindInstalledPreviewWindow'))
        )
        'Probe the original broker only after validating its process' = (Test-Order $find @(
            'FindWindowExW(p -3, p r0, p 0, w r1)', 'StrCmp $0 0 0 preview_find_pid',
            'GetWindowThreadProcessId', 'OpenProcess', 'QueryFullProcessImageNameW',
            'StrCmp $1 $PreviewClosePath', 'Call CheckPreviewCloseTarget', 'SendMessageTimeoutW'
        ))
        'Request interactive approval for the original broker and skip silent legacy closure' = (
            (Test-Order $close @('${If} $StableLegacyUpgrade == 1', 'IfSilent preview_close_done')) -and
            (Test-Order $close @('preview_close_cooperative:', 'MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2',
                '/SD IDNO IDYES stable_exit_confirmed', 'Goto preview_close_cancel', 'preview_close_send:',
                'Call CheckPreviewCloseTarget', 'SendMessageTimeoutW'))
        )
        'Start the legacy shutdown deadline only after Yes' = (Test-Order $close @(
            'StrCmp $StableLegacyUpgrade 1 0 stable_close_deadline_ready',
            '/SD IDNO IDYES stable_exit_confirmed', 'Goto preview_close_cancel', 'stable_exit_confirmed:',
            'GetTickCount() i.r7', 'StrCpy $PreviewCloseStarted $7', 'stable_close_deadline_ready:',
            'Call CheckPreviewCloseTarget', 'SendMessageTimeoutW'
        ))
        'Wait for exact legacy process exit after an uncertain send and enforce the deadline' = (Test-Order $close @(
            'SendMessageTimeoutW', '${If} $StableLegacyUpgrade == 1',
            'StrCmp $R7 0 stable_close_wait_for_exit', 'StrCmp $R7 "error" stable_close_wait_for_exit',
            'StrCmp $R7 0 preview_close_release', 'stable_close_wait_for_exit:', 'preview_close_wait:',
            'WaitForSingleObject(p r3, i 0)', 'StrCmp $4 0 stable_close_exited', 'StrCmp $4 258 0 preview_close_release',
            'IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_close_release 0 preview_close_release',
            'Goto preview_close_wait', 'stable_close_exited:', 'CloseHandle(p r3)', 'Goto preview_close_done',
            'preview_close_release:', 'preview_close_pending:'
        ))
        'Close, verify and stage exact legacy overlaps while the launcher is withheld' = (Test-Order $install @(
            'Call CloseInstalledPreviewForUpdate', 'Call CheckInstalledPreviewFiles', 'Call CheckPreviewRetirementOwner',
            'Call RetirePreviewInstalledFile', 'StrCpy $PreviewHoldExecutable 1', 'Call PublishPreviewStagedFile',
            '!include "${LEGACY_OVERLAP_INCLUDE}"', '!define PREVIEW_FILE_CHECK "PublishPreviewStagedFile"'
        ))
        'Reject a legacy file still present at a replacement path' = (Test-Order $publisher @(
            'publish_file_exists:', 'StrCmp $PreviewDestinationOwned 1 0 publish_file_failed',
            'StrCmp $StableLegacyUpgrade 1 publish_file_failed', 'Call BackUpIdenticalStableFile',
            'StrCmp $StableOriginalMoved 1 0 publish_file_failed', 'publish_staged:', 'Rename "$2" "$1"'
        ))
        'Bind previous-file identity and verify no alternate streams before moving' = (Test-Order $retire @(
            'CreateFileW(w r1, i 0x80010000, i 1', 'StrCmp $6 0 0 retirement_free_stream_info',
            'CompareStringOrdinal', 'Call ReadPreviewRetiredSHA256', 'retirement_hash_match:',
            'StrCmp $4 $5 retirement_backup_file', 'SetFileInformationByHandle'
        ))
        'Roll back only the exact file installed by Setup and retain previous backup on conflict' = (Test-Order $restore @(
            'StrCpy $PreviewBackupRoot "$PreviewStageRoot"', 'Push "$5"', 'Call RetirePreviewInstalledFile',
            'StrCmp $StableOriginalMoved 1 0 restore_file_failed', 'Rename "$3" "$1"',
            'restore_file_failed:', 'StrCpy $PreviewRollbackFailed 1'
        ))
        'Verify installed version and path before uninstaller file preflight' = (
            (Test-Order $uninstall @('Call un.VerifyStableUninstallOwnership', 'Call un.CheckInstalledPreviewFiles',
                '!include "${PAYLOAD_UNINSTALL_INCLUDE}"')) -and
            (Test-Order $owned @('Push "${ARP_KEY}"', 'Call un.PreviewRegistrationOwner', 'StrCmp $0 "ours"',
                'Push "DisplayVersion"', 'StrCmpS $1 "${APP_VERSION}"', 'Call un.ReadPreviewRetiredSHA256',
                'StrCmp $4 "${APP_SHA256}"'))
        )
        'Uninstall deletes only matching manifest bytes through the checked handle' = (Test-Order $remove @(
            'CreateFileW(w r1, i 0x80010000, i 1', 'StrCmp $6 0 0 stable_remove_free',
            'CompareStringOrdinal', 'Call un.ReadPreviewRetiredSHA256', 'StrCmp $5 "$R0" 0 stable_remove_free',
            'SetFileInformationByHandle(p r3, i 4, p r4, i 1)', 'stable_remove_kept:'
        ))
        'Only remove legacy Open With entries owned by this installation' = (Test-Order $openWith @(
            'Call un.ReadCheckedPreviewString', 'StrCmp $1 ''$\"$INSTDIR\${APP_EXE}$\" $\"%1$\"'' 0 stable_image_registration_done',
            'DeleteRegValue HKCU "Software\Classes\.png\OpenWithProgids" "AutomationForge.SnipStudio.Image"'
        ))
        'Keep the new stable launcher staged until registration succeeds' = (Test-Order $install @(
            '!ifndef SNIP_STUDIO_STABLE', 'Push "${APP_EXE}"', 'Call PublishPreviewStagedFile',
            'Call WriteStableRegistration', 'IfErrors preview_registration_failed',
            'Push "${APP_EXE}"', 'Call PublishPreviewStagedFile', 'Call DiscardPreviewStage'
        ))
        'Snapshot the exact write set before moving installed files' = (
            (Test-Order $install @('Call CheckPreviewRetirementOwner', 'Call PrepareStableRegistration',
                'Call RetirePreviewInstalledFile', 'Call PublishPreviewStagedFile')) -and
            (Test-Order $prepareRegistration @('!insertmacro StableRegistrationValues CaptureStableRegistrationValue',
                'CreateShortcut "$PreviewStageRoot\.setup-start.lnk"', 'CreateShortcut "$PreviewStageRoot\.setup-remove.lnk"')) -and
            $writeRegistration.Contains('!insertmacro StableRegistrationValues WriteStableRegistrationValue') -and
            $Text.Contains('!insertmacro StableRegistrationValues RestoreStableRegistrationValue')
        )
        'Keep existing shortcuts and fail before overwriting an arriving link' = (
            (Test-Order (Get-InstallerBlock $Text 'Function CreateMissingStableLink' 'FunctionEnd') @(
                'stable_link_missing:', 'CopyFileW(w r1, w r0, i 1)'
            )) -and
            (Test-Order (Get-InstallerBlock $Text 'Function RollBackStableRegistration' 'FunctionEnd') @(
                '${If} $StableStartLinkCreated == 2', 'Push "$StableStartLinkSHA256"', 'Call RemoveCreatedStableLink',
                '${If} $StableRemoveLinkCreated == 2', 'Push "$StableRemoveLinkSHA256"', 'Call RemoveCreatedStableLink'
            ))
        )
        'Restore only verified Setup registry writes using the exact prior data' = (Test-Order $restoreValue @(
            'RegQueryValueExW', 'StrCmp $R4 $R3', 'StrCmp $R5 $R2',
            'RtlCompareMemory', 'StrCmp $R7 $R5 0 stable_restore_value_free', 'RegSetValueExW', 'RegDeleteValueW'
        ))
        'Require registration reversal before restoring the old launcher' = (
            (Test-Order $abortRegistration @('Call RollBackStableRegistration',
                'StrCmp $StableRegistrationRecovered 1 stable_restore_original_files',
                'Keep the transaction files', 'Abort', 'stable_restore_original_files:',
                'Call RollBackPreviewDeployment')) -and
            (Test-Order $publisher @('publish_file_failed:', 'StrCmp $StableRegistrationActive 1 0 +2',
                'Call AbortStableRegistration', 'Call RollBackPreviewDeployment'))
        )
    }
    @($checks.GetEnumerator() | Where-Object { !$_.Value } | ForEach-Object Key)
}

$failures = @(Get-PolicyFailures $source)
if ($failures.Count) { throw "Stable installer policy failed: $($failures -join '; ')" }

$mutations = @{
    '${IfNot} ${IsNativeAMD64}' = '${IfNot} ${RunningX64}'
    '0 destination_owner_free ; stable 0.6.21' = '0 destination_owner_verified ; stable 0.6.21'
    '/SD IDNO IDYES stable_exit_confirmed' = '/SD IDYES IDYES stable_exit_confirmed'
    'FindWindowExW(p -3, p r0, p 0, w r1)' = 'FindWindowExW(p -3, p r0, p 0, p 0)'
    'StrCmp $4 1168 preview_find_done preview_find_uncertain' = 'Goto preview_find_done'
    '    StrCpy $PreviewCloseStarted $7' = '    ; leave the deadline measured from before Yes'
    'StrCmp $R7 0 stable_close_wait_for_exit' = 'StrCmp $R7 0 preview_close_release'
    'StrCmp $4 0 stable_close_exited' = 'StrCmp $4 0 preview_close_again'
    'StrCmp $StableLegacyUpgrade 1 publish_file_failed' = 'StrCmp $StableLegacyUpgrade 1 publish_staged'
    'StrCmp $4 $5 retirement_backup_file' = 'Goto retirement_backup_file'
    'StrCmp $5 "$R0" 0 stable_remove_free' = 'StrCmp $5 "$R0" 0 stable_remove_kept'
    'CopyFileW(w r1, w r0, i 1)' = 'CopyFileW(w r1, w r0, i 0)'
    'StrCmp $R7 $R5 0 stable_restore_value_free' = 'Goto stable_restore_value_write'
    'StrCmp $StableRegistrationRecovered 1 stable_restore_original_files' = 'Goto stable_restore_original_files'
}
foreach ($before in $mutations.Keys) {
    $mutated = $source.Replace($before, $mutations[$before])
    if ($mutated -eq $source -or @(Get-PolicyFailures $mutated).Count -eq 0) {
        throw "Stable installer policy failed to reject mutation: $before"
    }
}

"PASS: 21 stable installer source-policy checks and $($mutations.Count) negative mutations. No native execution."
