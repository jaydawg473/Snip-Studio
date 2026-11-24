# Source-policy checks only. This does not execute Setup or change any registry or install path.
param([string]$NsisPath = (Join-Path $PSScriptRoot '../SnipStudio-WinUI3.nsi'))
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText([IO.Path]::GetFullPath($NsisPath))

function Test-LineOrder([string]$Block, [string[]]$Lines) {
    $cursor = 0
    foreach ($line in $Lines) {
        $index = $Block.IndexOf($line, $cursor, [StringComparison]::Ordinal)
        if ($index -lt 0) { return $false }
        $cursor = $index + $line.Length
    }
    return $true
}

function Get-Body([string]$Text, [string]$Start, [string]$End) {
    $pattern = '(?ms)^' + [regex]::Escape($Start) + '\s*$(.*?)^' + [regex]::Escape($End) + '\s*$'
    return [regex]::Match($Text, $pattern).Groups[1].Value
}

function Find-InstallPolicyFailures([string]$Text) {
    $install = Get-Body $Text 'Section "${APP_NAME} (required)" MainApplication' 'SectionEnd'
    $activate = Get-Body $Text 'Function PublishPreviewStagedFile' 'FunctionEnd'
    $restore = Get-Body $Text 'Function RestorePreviewOriginalFile' 'FunctionEnd'
    $rollback = Get-Body $Text 'Function RollBackPreviewDeployment' 'FunctionEnd'
    $stageFailure = Get-Body $Text 'Function AbortPreviewStaging' 'FunctionEnd'
    $deleteStagedFile = Get-Body $Text 'Function DeletePreviewStagedFile' 'FunctionEnd'
    $deleteStagedDirectory = Get-Body $Text 'Function RemoveEmptyPreviewStageDirectory' 'FunctionEnd'
    $stageCleanup = Get-Body $Text 'Function RemovePreviewStagedFiles' 'FunctionEnd'
    $discard = Get-Body $Text 'Function DiscardPreviewStage' 'FunctionEnd'
    $destination = Get-Body $Text 'Function CheckPreviewDestination' 'FunctionEnd'
    $destinationState = Get-Body $Text 'Function ReadPreviewDestinationState' 'FunctionEnd'
    $destinationOwner = Get-Body $Text 'Function VerifyOwnedPreviewLauncher' 'FunctionEnd'
    $destinationRecheck = Get-Body $Text 'Function CheckPreviewRetirementOwner' 'FunctionEnd'
    $uninstall = Get-Body $Text 'Section "Uninstall"' 'SectionEnd'
    $uninstallInit = Get-Body $Text 'Function un.onInit' 'FunctionEnd'
    $findWindow = Get-Body $Text 'Function FindInstalledPreviewWindow' 'FunctionEnd'
    $closeApp = Get-Body $Text 'Function CloseInstalledPreviewForUpdate' 'FunctionEnd'
    $registration = [regex]::Match($install, '(?ms)^\s*DetailPrint "Registering the preview with Windows\.\.\."\s*$(.*?)^\s*DetailPrint "Cleaning up temporary setup files\.\.\."').Groups[1].Value
    $registrationFailure = [regex]::Match($install, '(?ms)^preview_registration_failed:\s*$(.*?)^preview_install_finished:').Groups[1].Value
    $conditions = [ordered]@{
        'Installer and uninstaller share a deployment lock across Windows sessions' = (
            $Text.Contains('!define PREVIEW_DEPLOYMENT_MUTEX "Global\AutomationForge.SnipStudio.WinUI3.Deployment.v1"') -and
            $Text.Contains('CreateMutexW(p 0, i 0, w "${PREVIEW_DEPLOYMENT_MUTEX}")') -and
            (Get-Body $Text 'Function .onInit' 'FunctionEnd').Contains('Call AcquirePreviewDeploymentLock') -and
            (Get-Body $Text 'Function un.onInit' 'FunctionEnd').Contains('Call un.AcquirePreviewDeploymentLock')
        )
        'Setup stages payload and uninstaller before activating any installed file' = (
            Test-LineOrder $install @(
                'Call CheckPreviewDestination', 'Call PreparePreviewStage',
                'StrCpy $INSTDIR $PreviewStageRoot', '!include "${PAYLOAD_INSTALL_INCLUDE}"',
                'WriteUninstaller "$INSTDIR\Uninstall.exe"', 'StrCpy $INSTDIR $PreviewInstallRoot',
                'Call CloseInstalledPreviewForUpdate', 'Call CheckInstalledPreviewFiles', 'Call PublishPreviewStagedFile'
            )
        )
        'Setup and uninstall perform one complete preflight immediately before mutation' = (
            [regex]::Matches($install, '(?m)^\s*Call CloseInstalledPreviewForUpdate\s*$').Count -eq 1 -and
            [regex]::Matches($install, '(?m)^\s*Call CheckInstalledPreviewFiles\s*$').Count -eq 1 -and
            [regex]::Matches($Text, '(?m)^\s*Call un\.CheckInstalledPreviewFiles\s*$').Count -eq 1 -and
            (Test-LineOrder $uninstall @('Call un.CheckInstalledPreviewFiles', '!include "${PAYLOAD_UNINSTALL_INCLUDE}"'))
        )
        'Executable stays unavailable until dependencies and uninstaller have been activated' = (
            Test-LineOrder $install @(
                'StrCpy $PreviewHoldExecutable 1', 'Push "${APP_EXE}"', 'Call PublishPreviewStagedFile',
                'StrCpy $PreviewHoldExecutable 0', '!define PREVIEW_SKIP_EXECUTABLE',
                '!include "${PAYLOAD_PREFLIGHT_INCLUDE}"', 'Push "Uninstall.exe"', 'Call PublishPreviewStagedFile',
                'Push "${APP_EXE}"', 'Call PublishPreviewStagedFile', 'WriteRegStr'
            )
        )
        'Commit waits for all required registration operations, each with its own error check' = (
            [regex]::Matches($registration, '(?m)^\s*(?:CreateDirectory|CreateShortcut|WriteRegStr|WriteRegDWORD)\s+[^\r\n]+$').Count -eq 15 -and
            [regex]::Matches($registration, '(?m)^\s*(?:CreateDirectory|CreateShortcut|WriteRegStr|WriteRegDWORD)\s+[^\r\n]+\r?\n[ \t]*IfErrors preview_registration_failed[ \t]*$').Count -eq 15 -and
            [regex]::Matches($install, '(?m)^\s*Call DiscardPreviewStage\s*$').Count -eq 1 -and
            (Test-LineOrder $install @('WriteRegDWORD HKCU "${ARP_KEY}" "EstimatedSize" ${PACKAGE_KB}',
                'IfErrors preview_registration_failed', 'Call DiscardPreviewStage',
                'StrCmp $PreviewStageCleanupFailed 0 preview_registration_done',
                'SetErrorLevel 2', 'Abort ', 'preview_registration_done:', 'SetErrorLevel 0'))
        )
        'A registration failure keeps predecessor bytes and identifies the transaction' = (
            -not $registrationFailure.Contains('Call DiscardPreviewStage') -and
            -not $registrationFailure.Contains('Call RollBackPreviewDeployment') -and
            (Test-LineOrder $registrationFailure @('Keep the transaction files at $PreviewTransactionRoot.',
                'IfSilent', 'Keep the files at $PreviewTransactionRoot and contact support before retrying.',
                'SetErrorLevel 2', 'Abort '))
        )
        'Only exact transaction root absence permits cleanup success' = (
            (Test-LineOrder $discard @('Call IsSafePreviewPath', 'StrCmp $0 1 0 stage_discard_warn',
                'RMDir "$PreviewTransactionRoot"', 'StrCpy $0 $PreviewTransactionRoot',
                '::GetFileAttributesW(w r0) i.r0 ?e', 'Pop $1', 'StrCmp $0 -1 0 stage_discard_warn',
                'StrCmp $1 2 stage_discard_removed', 'StrCmp $1 3 stage_discard_removed stage_discard_warn',
                'stage_discard_removed:', 'StrCpy $PreviewTransactionRoot ""',
                'stage_discard_warn:', 'StrCpy $PreviewStageCleanupFailed 1',
                'at $PreviewTransactionRoot', 'stage_discard_done:')) -and
            -not $discard.Contains('IfFileExists "$PreviewTransactionRoot\*.*"')
        )
        'Clean cancellation and retained transaction failures use distinct exit codes' = (
            Test-LineOrder $closeApp @('preview_close_cancel:', 'Call DiscardPreviewStage',
                'StrCmp $PreviewStageCleanupFailed 0 preview_close_cancel_clean', 'SetErrorLevel 2',
                'Abort', 'preview_close_cancel_clean:', 'SetErrorLevel 3', 'Abort')
        )
        'An earlier transaction stays intact and the path is reported before Setup or Uninstall mutates anything' = (
            (Test-LineOrder $destination @('::GetFileAttributesW(w r0) i.r0 ?e',
                'StrCmp $0 -1 0 destination_recovery_exists', 'StrCmp $1 2 destination_check_contents',
                'StrCmp $1 3 destination_check_contents', 'destination_recovery_exists:',
                'DetailPrint "An earlier Snip Studio transaction requires review at $INSTDIR\${PREVIEW_TRANSACTION_FOLDER}."',
                'SetErrorLevel 2', 'Abort')) -and
            (Test-LineOrder $uninstallInit @('::GetFileAttributesW(w r0) i.r0 ?e',
                'StrCmp $0 -1 0 uninstall_recovery_pending', 'StrCmp $1 2 uninstall_ready',
                'StrCmp $1 3 uninstall_ready', 'uninstall_recovery_pending:',
                'DetailPrint "An earlier Snip Studio transaction requires review at $INSTDIR\${PREVIEW_TRANSACTION_FOLDER}."',
                'SetErrorLevel 2', 'Abort')) -and
            -not $destination.Contains('Call DiscardPreviewStage') -and
            -not $uninstallInit.Contains('RMDir')
        )
        'Destination scan proves absence or clean enumeration completion' = (
            (Test-LineOrder $destinationState @('StrCpy $7 "unknown"', '::GetFileAttributesW(w r0) i.r1 ?e',
                'StrCmp $1 -1 0 destination_state_directory', 'StrCmp $3 2 destination_state_empty',
                'StrCmp $3 3 destination_state_empty destination_state_done', 'IntOp $1 $1 & 0x410',
                'IntCmp $1 16 0 destination_state_done destination_state_done',
                '::FindFirstFileW(w r0, p r1) p.r2 ?e', 'StrCmp $2 -1 0 destination_state_entry',
                'StrCmp $3 2 0 destination_state_free', 'StrCpy $7 "empty"',
                'StrCmp $5 "" destination_state_close', 'StrCmp $5 "." destination_state_continue',
                'StrCmp $5 ".." destination_state_continue', 'StrCpy $7 "occupied"',
                '::FindNextFileW(p r2, p r1) i.r6 ?e', 'StrCmp $6 0 0 destination_state_close',
                'StrCmp $3 18 0 destination_state_close', 'StrCpy $7 "empty"',
                '::FindClose(p r2)', 'System::Free $1')) -and
            (Test-LineOrder $destination @('Call ReadPreviewDestinationState', 'StrCmp $0 "empty" destination_ok',
                'StrCmp $0 "occupied" 0 destination_unsafe', 'Call VerifyOwnedPreviewLauncher',
                'StrCmp $0 1 destination_owned')) -and
            -not [regex]::IsMatch($destination, '(?m)^\s*(?:IfFileExists|FindFirst|FindNext)\b')
        )
        'Nonempty destination ownership requires the registered local path and a pinned launcher byte identity' = (
            $Text.Contains('!ifndef PREVIOUS_APP_SHA256') -and
            (Test-LineOrder $destinationOwner @(
                'ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"', 'IfErrors destination_owner_done',
                'StrCmp $1 ":\" 0 destination_owner_done', 'GetFullPathName $0 "$0"',
                'StrCmp $0 $INSTDIR 0 destination_owner_done', 'Call CheckPreviewRetiredPathLength',
                'Call IsSafePreviewPath', '::CreateFileW(w r0, i 0x80000000, i 1, p 0, i 3, i 0x00200000, p 0) p.r2',
                '::GetFileInformationByHandleEx(p r2, i 9, p r3, i 8) i.r4',
                'IntOp $4 $4 & 0x410', 'StrCmp $4 0 0 destination_owner_free', 'Call ReadPreviewRetiredSHA256',
                'StrCmp $5 "${APP_SHA256}" destination_owner_verified',
                'StrCmp $5 "${PREVIOUS_APP_SHA256}" destination_owner_verified',
                'StrCmp $5 "97F0E066B9274A85CE0DAC32034549E2A3605C1EF2ED0B9E153EBF93A724FEB7" destination_owner_verified',
                'StrCmp $5 "DA7BB6F5A47B5B70A619D05C61454DB04F3D286F8BB52FAC5F9BECABEAAA9134" destination_owner_verified',
                'StrCmp $5 "CFD202FEE2D21D0FCEFFCB85F3C53DF96A52D68C640DCA6A8AE9B660073B3A69" 0 destination_owner_free',
                'destination_owner_verified:', 'StrCpy $6 1', '::CloseHandle(p r2)'))
        )
        'Setup revalidates a supported launcher after closing the preview and before changing installed files' = (
            (Test-LineOrder $install @('Call CloseInstalledPreviewForUpdate', 'Call CheckInstalledPreviewFiles',
                'Call CheckPreviewRetirementOwner', 'Call PublishPreviewStagedFile')) -and
            (Test-LineOrder $destinationRecheck @('StrCmp $PreviewDestinationOwned 0 retirement_owner_done',
                'Call VerifyOwnedPreviewLauncher', 'StrCmp $0 1 retirement_owner_verified',
                'Call DiscardPreviewStage', 'SetErrorLevel 2', 'Abort ', 'retirement_owner_verified:'))
        )
        'Files appearing in a fresh target and linked or directory targets never enter the overwrite branch' = (
            Test-LineOrder $activate @('publish_file_exists:', 'StrCmp $PreviewDestinationOwned 1 0 publish_file_failed',
                'IntOp $4 $4 & 0x410', 'StrCmp $4 0 0 publish_file_failed', 'Rename "$1" "$3"')
        )
        'A move failure invokes rollback and stops the install' = (
            Test-LineOrder $activate @(
                'Rename "$1" "$3"', 'IfErrors publish_file_failed', 'Rename "$2" "$1"',
                'IfErrors publish_file_failed', 'publish_file_failed:', 'Call RollBackPreviewDeployment', 'Abort '
            )
        )
        'Rollback retains recovery artifacts if any original cannot be restored' = (
            (Test-LineOrder $restore @('IfFileExists "$2" restore_backup', 'Rename "$1" "$2"', 'Rename "$3" "$1"')) -and
            (Test-LineOrder $rollback @(
                'Call RestorePreviewOriginalFile', 'StrCmp $PreviewRollbackFailed 0 0 rollback_files_kept',
                'Push "${APP_EXE}"', 'Call RestorePreviewOriginalFile',
                'StrCmp $PreviewRollbackFailed 0 0 rollback_files_kept', 'Call DiscardPreviewStage', 'rollback_files_kept:'
            ))
        )
        'Staging failure cannot change installed content' = (
            $stageFailure.Contains('Call DiscardPreviewStage') -and
            -not $stageFailure.Contains('Call PublishPreviewStagedFile')
        )
        'Cleanup never recursively removes a directory' = (-not [regex]::IsMatch($Text, '(?im)^\s*RMDir\s+/r\b'))
        'Cleanup skips absent files and checks exact files and unique directories before removal' = (
            (Test-LineOrder $deleteStagedFile @('IfFileExists "$0" 0 stage_delete_done', 'Call IsSafePreviewPath', 'Delete "$0"')) -and
            -not $deleteStagedFile.Contains('RMDir') -and
            (Test-LineOrder $deleteStagedDirectory @('Call IsSafePreviewPath', 'RMDir "$0"')) -and
            (Test-LineOrder $stageCleanup @('!define PREVIEW_STAGE_CLEANUP', '!include "${PAYLOAD_UNINSTALL_INCLUDE}"', '!undef PREVIEW_STAGE_CLEANUP')) -and
            $Text.Contains('Call RemoveEmptyPreviewStageDirectory')
        )
        'Native path validation rejects links and treats API failure as an error' = (
            $Text.Contains('FILE_ATTRIBUTE_REPARSE_POINT') -and
            $Text.Contains('StrCmp $1 "error" preview_path_unsafe')
        )
        'Silent update requests only the idle-safe protocol and refuses pending closure' = (
            $Text.Contains('StrCpy $6 2 ; New previews receive an idle-only request during silent setup.') -and
            $Text.Contains('IfSilent preview_close_cancel')
        )
        'Cooperative discovery proves the installed image and HWND identity before probing' = (
            (Test-LineOrder $closeApp @('ReadRegStr $0 HKCU "${APP_KEY}" "InstallLocation"',
                'StrCmp $0 $PreviewClosePath 0 preview_close_done', 'StrCpy $PreviewCloseLegacyScan 0',
                'Call FindInstalledPreviewWindow')) -and
            (Test-LineOrder $findWindow @('IntCmpU $7 ${PREVIEW_CLOSE_TIMEOUT_MS} preview_find_uncertain',
                '${If} $PreviewCloseLegacyScan == 1', '::GetWindow(p r0, i 4)',
                '::IsWindowVisible(p r0)', '::GetWindowTextW(', '${EndIf}', 'preview_find_pid:',
                '::OpenProcess(i 0x101000', '::QueryFullProcessImageNameW(',
                'StrCmp $1 $PreviewClosePath 0 preview_find_release', 'Call CheckPreviewCloseTarget',
                'p ${PREVIEW_EXIT_COOKIE}, p 1,', 'StrCmp $R8 ${PREVIEW_EXIT_COOKIE} preview_find_accepted'))
        )
        'Uncertain probes or an unsupported newer preview do not trigger legacy Close' = (
            (Test-LineOrder $closeApp @('GetDLLVersion "$PreviewClosePath" $0 $1',
                'IntCmpU $0 7 0 preview_close_register preview_close_register',
                'IntCmpU $1 1 0 preview_close_register 0', 'IntCmpU $1 5 0 0 preview_close_register',
                'StrCpy $PreviewLegacyCloseAllowed 1', 'StrCpy $PreviewCloseLegacyScan 0',
                'Call FindInstalledPreviewWindow', 'StrCmp $PreviewCloseProbeFailed 1 preview_close_pending',
                'IfSilent preview_close_pending', 'StrCmp $PreviewLegacyCloseAllowed 1 0 preview_close_pending',
                'StrCpy $PreviewCloseLegacyScan 1', 'Call FindInstalledPreviewWindow')) -and
            $findWindow.Contains('StrCmp $R7 0 preview_find_probe_failed') -and
            $findWindow.Contains('StrCpy $PreviewCloseProbeFailed 1') -and
            (Test-LineOrder $closeApp @('StrCmp $PreviewCloseLegacyScan 0 preview_close_cooperative',
                '::PostMessageW(p r0, i 0x0112, p 0xF060, p 0)', 'preview_close_cooperative:',
                'Call CheckPreviewCloseTarget', 'p ${PREVIEW_EXIT_COOKIE}, p r6,',
                'StrCmp $R8 0x5355 preview_close_accepted preview_close_release')) -and
            -not [regex]::IsMatch($closeApp, '(?i)TerminateProcess|RmShutdown|WM_ENDSESSION')
        )
        'Legacy Close has no generic caption match and never targets settings or review' = (
            (Test-LineOrder $findWindow @('preview_find_legacy_title:', 'StrCmpS $1 "Snip Studio" preview_find_pid',
                'StrCmpS $4 "Recording" preview_find_pid', 'StrCmpS $4 "Record video" preview_find_pid',
                '${GetFileExt} "$4" $5', 'StrCmp $5 "pdf" preview_find_pid preview_find_next')) -and
            -not [regex]::IsMatch($findWindow, '(?mi)^\s*StrCmpS?\s+\$\d\s+"(?:Settings|Snip Studio .+Settings|Video review|mp4)"\s+preview_find_pid')
        )
    }
    foreach ($condition in $conditions.GetEnumerator()) {
        if (-not $condition.Value) { $condition.Key }
    }
}

$failures = @(Find-InstallPolicyFailures $source)
if ($failures.Count) { throw ('WinUI installer source policy did not pass: ' + ($failures -join '; ')) }
$mutations = @(
    @{ Before = 'StrCpy $INSTDIR $PreviewStageRoot'; After = '; omitted stage' }
    @{ Before = 'Call un.AcquirePreviewDeploymentLock'; After = '; uninstaller no longer serialized' }
    @{ Before = 'Global\AutomationForge.SnipStudio.WinUI3.Deployment.v1'; After = 'Local\AutomationForge.SnipStudio.WinUI3.Deployment.v1' }
    @{ Before = 'Call RollBackPreviewDeployment'; After = '; omitted rollback' }
    @{ Before = 'Call CheckInstalledPreviewFiles'; After = '; omitted final preflight' }
    @{ Before = 'Call PreparePreviewStage'; After = "Call CheckInstalledPreviewFiles`n  Call PreparePreviewStage" }
    @{ Before = 'StrCmp $PreviewRollbackFailed 0 0 rollback_files_kept'; After = '; always discards the backups' }
    @{ Before = 'RMDir "$PreviewTransactionRoot"'; After = 'RMDir /r "$PreviewTransactionRoot"' }
    @{ Before = 'IfFileExists "$0" 0 stage_delete_done'; After = '; checks every absent file' }
    @{ Before = '  DetailPrint "Registering the preview with Windows..."'; After = '  Call DiscardPreviewStage' + [char]10 + '  DetailPrint "Registering the preview with Windows..."' }
    @{ Before = ('  CreateDirectory "$SMPROGRAMS\Automation Forge\${APP_NAME}"' + [char]10 + '  IfErrors preview_registration_failed'); After = '  CreateDirectory "$SMPROGRAMS\Automation Forge\${APP_NAME}"' }
    @{ Before = 'StrCmp $PreviewStageCleanupFailed 0 preview_registration_done'; After = 'Goto preview_registration_done' }
    @{ Before = 'Keep the transaction files at $PreviewTransactionRoot.'; After = 'Check permissions and retry Setup.' }
    @{ Before = 'StrCmp $0 -1 0 stage_discard_warn'; After = 'StrCmp $0 -1 0 stage_discard_removed' }
    @{ Before = 'StrCmp $1 3 stage_discard_removed stage_discard_warn'; After = 'StrCmp $1 3 stage_discard_removed stage_discard_removed' }
    @{ Before = 'StrCmp $PreviewStageCleanupFailed 0 preview_close_cancel_clean'; After = 'Goto preview_close_cancel_clean' }
    @{ Before = 'StrCmp $1 3 uninstall_ready'; After = 'Goto uninstall_ready' }
    @{ Before = 'StrCmp $3 2 0 destination_state_free'; After = '; any initial enumeration failure counts as empty' }
    @{ Before = 'StrCmp $3 18 0 destination_state_close'; After = '; any followup enumeration failure counts as complete' }
    @{ Before = 'Call ReadPreviewDestinationState'; After = 'IfFileExists "$INSTDIR\${APP_EXE}" destination_ok' }
    @{ Before = 'StrCmp $0 $INSTDIR 0 destination_owner_done'; After = '; permit a different registration path' }
    @{ Before = 'StrCmp $5 "${PREVIOUS_APP_SHA256}" destination_owner_verified'; After = 'Goto destination_owner_verified' }
    @{ Before = 'Call CheckPreviewRetirementOwner'; After = '; skipped final launcher verification' }
    @{ Before = 'StrCmp $PreviewDestinationOwned 1 0 publish_file_failed'; After = '; overwrite files in a newly occupied destination' }
    @{ Before = 'IntOp $4 $4 & 0x410'; After = 'IntOp $4 $4 & 0x10' }
    @{ Before = 'StrCmp $1 $PreviewClosePath 0 preview_find_release'; After = '; no exact process-image check' }
    @{ Before = 'StrCmp $PreviewCloseProbeFailed 1 preview_close_pending'; After = '; allows fallback after probe timeout' }
    @{ Before = 'IntCmpU $1 5 0 0 preview_close_register'; After = '; allows later releases to use legacy Close' }
    @{ Before = '${GetFileExt} "$4" $5'; After = ('StrCmpS $4 "Settings" preview_find_pid' + "`n    " + '${GetFileExt} "$4" $5') }
)
foreach ($mutation in $mutations) {
    if (-not $source.Contains($mutation.Before)) { throw ('Invalid mutation target: ' + $mutation.Before) }
    if (@(Find-InstallPolicyFailures ($source.Replace($mutation.Before, $mutation.After))).Count -eq 0) {
        throw ('Source policy failed to detect mutation: ' + $mutation.After)
    }
}
'PASS: 23 WinUI installer source-policy checks and 29 negative mutations. No native execution.'
