param(
    [Parameter(Mandatory = $true)][string]$InstallerPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$ExpectedInstallerSha256,
    [Parameter(Mandatory = $true)][ValidateSet(0, 2, 3)][int]$ExpectedExitCode,
    [Parameter(Mandatory = $true)][string]$InstalledDirectory,
    [Parameter(Mandatory = $true)][string]$PreferencesPath,
    [int[]]$PreserveProcessId = @(),
    [switch]$WithoutSilentSwitch,
    [switch]$UseExplicitDirectory,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'

# This executes only the signed installer's read-only /CHECKREADY mode. It
# never starts Snip Studio, closes an application, or runs an install/uninstall.
$preflightInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$preflightVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($preflightInstaller).FileVersion
if ($preflightVersion -ne '0.5.8.0') { throw 'Only the selected installer with /CHECKREADY (0.5.8.0) may be checked.' }
$preflightHash = (Get-FileHash -LiteralPath $preflightInstaller -Algorithm SHA256).Hash
if ($preflightHash -ine $ExpectedInstallerSha256) { throw 'Installer hash does not match the explicitly selected artifact.' }
$preflightSignature = Get-AuthenticodeSignature -LiteralPath $preflightInstaller
if ($preflightSignature.Status -ne 'Valid' -or $null -eq $preflightSignature.TimeStamperCertificate -or $preflightSignature.SignerCertificate.Subject -notlike '*Automation Forge LLC*') {
    throw 'The diagnostic installer must have a valid, timestamped Automation Forge signature.'
}

$preflightInstallKey = 'HKCU:\Software\Automation Forge\Snip Studio'
$preflightRegisteredDirectory = Get-ItemPropertyValue -LiteralPath $preflightInstallKey -Name InstallLocation -ErrorAction SilentlyContinue
if (-not $UseExplicitDirectory -and -not [String]::IsNullOrWhiteSpace($preflightRegisteredDirectory) -and [IO.Path]::GetFullPath($InstalledDirectory).TrimEnd('\') -ine [IO.Path]::GetFullPath($preflightRegisteredDirectory).TrimEnd('\')) {
    throw 'InstalledDirectory must match the target registered for this Windows account; /CHECKREADY uses that registered installation.'
}
$preflightInstalledExe = Join-Path $InstalledDirectory 'Snip Studio.exe'
$preflightTranslationRuntime = Join-Path $InstalledDirectory 'translation\runtime\node.exe'

function Get-PreflightFileState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return [ordered]@{ Path = $Path; Exists = $false; Sha256 = $null } }
    return [ordered]@{ Path = $Path; Exists = $true; Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Get-PreflightRegistryState {
    return [ordered]@{
        InstallLocation = Get-ItemPropertyValue -LiteralPath $preflightInstallKey -Name InstallLocation -ErrorAction SilentlyContinue
        DisplayVersion = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AutomationForge.SnipStudio' -Name DisplayVersion -ErrorAction SilentlyContinue
        StartupCommand = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'Snip Studio - Automation Forge' -ErrorAction SilentlyContinue
    }
}
function Get-PreflightProcessState {
    return @($PreserveProcessId | ForEach-Object {
        $preflightExistingProcess = Get-Process -Id $_ -ErrorAction Stop
        [ordered]@{ Id = $preflightExistingProcess.Id; StartTimeUtc = $preflightExistingProcess.StartTime.ToUniversalTime().ToString('o'); Path = $preflightExistingProcess.Path }
    })
}
function Get-PreflightState {
    return [ordered]@{
        InstalledExecutable = Get-PreflightFileState $preflightInstalledExe
        TranslationRuntime = Get-PreflightFileState $preflightTranslationRuntime
        Preferences = Get-PreflightFileState $PreferencesPath
        Registry = Get-PreflightRegistryState
        PreservedProcesses = @(Get-PreflightProcessState)
    }
}

$preflightBefore = Get-PreflightState
$preflightArguments = @(if ($WithoutSilentSwitch) { '/CHECKREADY' } else { '/S'; '/CHECKREADY' })
if ($UseExplicitDirectory) {
    if (-not [IO.Path]::IsPathRooted($InstalledDirectory) -or $InstalledDirectory.IndexOf('"') -ge 0) { throw 'The diagnostic directory override must be an absolute unquoted path.' }
    # NSIS /D= must be last and unquoted; it consumes the remaining path,
    # including spaces. /CHECKREADY still exits before any installation section.
    $preflightArguments += '/D=' + $InstalledDirectory
}
$preflightStartedUtc = [DateTime]::UtcNow.ToString('o')
$preflightProcess = Start-Process -FilePath $preflightInstaller -ArgumentList $preflightArguments -PassThru -WindowStyle Hidden
if (-not $preflightProcess.WaitForExit(30000)) {
    throw ('The diagnostic did not exit within 30 seconds. It was not terminated; inspect installer process ' + $preflightProcess.Id + ' before continuing.')
}
$preflightActualExitCode = $preflightProcess.ExitCode
$preflightAfter = Get-PreflightState
$preflightUnchanged = ($preflightBefore | ConvertTo-Json -Depth 8 -Compress) -ceq ($preflightAfter | ConvertTo-Json -Depth 8 -Compress)
$preflightResult = [ordered]@{
    StartedUtc = $preflightStartedUtc
    CompletedUtc = [DateTime]::UtcNow.ToString('o')
    InstallerPath = $preflightInstaller
    InstallerVersion = $preflightVersion
    InstallerSha256 = $preflightHash
    SignatureStatus = $preflightSignature.Status.ToString()
    Arguments = $preflightArguments
    ExpectedExitCode = $ExpectedExitCode
    ActualExitCode = $preflightActualExitCode
    SelectedFilesRegistryAndProcessesUnchanged = $preflightUnchanged
    Before = $preflightBefore
    After = $preflightAfter
    Scope = 'Read-only installer preflight only; installation, uninstallation, prompt appearance, Retry/Cancel interaction, and successful upgrade are not tested.'
}
$preflightJson = $preflightResult | ConvertTo-Json -Depth 8
if (-not [String]::IsNullOrWhiteSpace($ReportPath)) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), $preflightJson, [Text.UTF8Encoding]::new($false)) }
Write-Output $preflightJson
if ($preflightActualExitCode -ne $ExpectedExitCode) { throw ('Unexpected preflight exit code: ' + $preflightActualExitCode) }
if (-not $preflightUnchanged) { throw 'A selected installed file, preference file, product registry value, or preserved process changed during the diagnostic. Review the report.' }
