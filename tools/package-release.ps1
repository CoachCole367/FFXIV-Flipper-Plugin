[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$PublishRepository = 'CoachCole367/FFXIV-Flipper-Plugin',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipBuild) {
    & dotnet run --project (Join-Path $root 'tests/Economics.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Regression suite failed.' }
    & dotnet build (Join-Path $root 'FFXIVFlipper/FFXIVFlipper.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
}
$build = Join-Path $root 'FFXIVFlipper/bin/Release'
$manifest = Get-Content -LiteralPath (Join-Path $build 'FFXIVFlipper.json') -Raw | ConvertFrom-Json
$version = [Version]$manifest.AssemblyVersion
$project = [xml](Get-Content -LiteralPath (Join-Path $root 'FFXIVFlipper/FFXIVFlipper.csproj') -Raw)
$releaseVersion = [string]$project.Project.PropertyGroup.Version
if ($version -ne [Version]("$releaseVersion.0")) { throw 'Build and project versions differ; rebuild before packaging.' }
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$asset = "FFXIVFlipper-$releaseVersion.zip"
$target = Join-Path $dist $asset
$source = Join-Path $build 'FFXIVFlipper/latest.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($source)
try {
    $allowed = @('FFXIVFlipper.dll','FFXIVFlipper.json','FFXIVFlipper.deps.json','Jint.dll','Acornima.dll',
        'licenses/Acornima.txt','licenses/Acornima-NOTICE.txt','licenses/Jint.txt')
    $entries = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
    foreach ($entry in $entries) { if ($entry -notin $allowed) { throw "Unexpected package entry: $entry" } }
    foreach ($required in $allowed) { if ($required -notin $entries) { throw "Missing package entry: $required" } }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath $source -Destination $target -Force
$hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $asset" | Set-Content -LiteralPath (Join-Path $dist "$asset.sha256") -Encoding utf8
$link = "https://github.com/$PublishRepository/releases/download/v$releaseVersion/$asset"
$entry = [ordered]@{}
$manifest.PSObject.Properties | ForEach-Object { $entry[$_.Name] = $_.Value }
$entry['DownloadLinkInstall'] = $link
$entry['DownloadLinkUpdate'] = $link
$entry['LastUpdate'] = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
$entry['Changelog'] = 'Compact scanner controls with collapsed settings; watchlists paused with saved lists preserved; one retry for temporary market failures; separate attempted, analyzed and skipped counts. Two-character in-game verification remains pending.'
ConvertTo-Json -InputObject @($entry) -Depth 8 | Set-Content -LiteralPath (Join-Path $dist 'repo.json') -Encoding utf8
Write-Host "Prepared $target ($((Get-Item -LiteralPath $target).Length) bytes)"
Write-Host "SHA256: $hash"
Write-Host "Staged repo.json targets $PublishRepository. Feed and assets must be publicly readable before installation can work."
Write-Host 'This script does not publish or alter repository visibility.'
