$ErrorActionPreference = 'Stop'
$translationDownloads = Join-Path $PSScriptRoot 'downloads'
[IO.Directory]::CreateDirectory($translationDownloads) | Out-Null
$npm = Get-Content -LiteralPath (Join-Path $translationDownloads 'bergamot-npm-metadata.json') -Raw | ConvertFrom-Json
if ($npm.version -cne '0.4.9' -or $npm.dist.tarball -cne 'https://registry.npmjs.org/@browsermt/bergamot-translator/-/bergamot-translator-0.4.9.tgz') { throw 'Unexpected Bergamot package metadata.' }
$expectedNpmIntegrity = 'sha512-bNuuCwM/JnsIYQCKXcYKFT4Qc5vLMoB8Nbvz8ReIgs7xzebK8Sa+R8iEK8mLvNBe/WaZ6zrPaUQilLsRT/ea8Q=='
if ($npm.dist.integrity -cne $expectedNpmIntegrity) { throw 'The published package integrity changed.' }
$npmHash = [BitConverter]::ToString([Convert]::FromBase64String($expectedNpmIntegrity.Substring(7))).Replace('-','').ToLowerInvariant()
$jobs = @([pscustomobject]@{Name='bergamot-translator-0.4.9.tgz';Url=$npm.dist.tarball;Algorithm='SHA512';Hash=$npmHash})
$jobs += [pscustomobject]@{Name='node-v24.22.2-win-x64.zip';Url='https://nodejs.org/dist/v24.22.2/node-v24.22.2-win-x64.zip';Algorithm='SHA256';Hash='ca1bff5d8ead13c9f2ea404a176db46bd430704d5b5113734049fa365e7799e2'}
$models = @(Get-Content -LiteralPath (Join-Path $translationDownloads 'model-catalog-selected.json') -Raw | ConvertFrom-Json)
if ($models.Count -ne 6) { throw 'Six pinned models are required.' }
foreach ($model in $models) {
    $uri = [Uri]$model.url
    if ($uri.Scheme -cne 'https' -or $uri.Host -cne 'data.statmt.org' -or $uri.AbsolutePath -notlike '/bergamot/models/*' -or $model.checksum -cnotmatch '^[0-9a-f]{64}$' -or $model.shortName -cnotmatch '^(en-(de|es|fr)|(de|es|fr)-en)-tiny$') { throw 'Unexpected model metadata.' }
    $jobs += [pscustomobject]@{Name=($model.shortName+'.tar.gz');Url=$model.url;Algorithm='SHA256';Hash=$model.checksum}
}
$results = @($jobs | ForEach-Object -Parallel {
    $ErrorActionPreference='Stop'
    $job = $_
    $path = Join-Path $using:translationDownloads $job.Name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm $job.Algorithm).Hash -ine $job.Hash) {
        Invoke-WebRequest -Uri $job.Url -OutFile $path -TimeoutSec 120 -Headers @{'User-Agent'='AutomationForge-SnipStudio-OfflineBundle'}
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm $job.Algorithm).Hash.ToLowerInvariant()
    if ($actual -cne $job.Hash) { throw ('Hash mismatch: '+$job.Name) }
    [pscustomobject]@{Name=$job.Name;Url=$job.Url;Algorithm=$job.Algorithm;Hash=$actual;Bytes=(Get-Item -LiteralPath $path).Length}
} -ThrottleLimit 3)
if ($results.Count -ne 8) { throw 'Incomplete download set.' }
$results | Sort-Object Name | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $translationDownloads 'verified-downloads.json') -Encoding UTF8
$results | Sort-Object Name | Select-Object Name,Bytes,Hash | Format-Table -AutoSize
