[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $BundleRoot ('results/collection-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json') }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$files = @(Get-ChildItem -LiteralPath (Join-Path $BundleRoot 'results') -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($OutputPath) } | ForEach-Object { [ordered]@{ Path = $_.FullName; Length = $_.Length; SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash } })
$payload = [ordered]@{ Schema = 'StorageChronicle.PhysicalAcceptanceCollection.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; BundleRoot = $BundleRoot; Files = @($files); AcceptanceEligible = $false; Note = 'Collection only; individual acceptance manifests determine eligibility.' }
$payload | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$payload | ConvertTo-Json -Depth 12
exit 0
