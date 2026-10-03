[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BundleRoot,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$contractCandidates = @(
    (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'build/quality/AcceptanceContracts.ps1'),
    (Join-Path $PSScriptRoot 'AcceptanceContracts.ps1')
)
$contractPath = @($contractCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1)
if ($contractPath.Count -ne 1) { throw 'AcceptanceContracts.ps1 is missing from the repository or manual acceptance bundle.' }
. $contractPath[0]
$BundleRoot = [IO.Path]::GetFullPath($BundleRoot)
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $BundleRoot ('results/collection-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json') }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
$files = @(Get-ChildItem -LiteralPath (Join-Path $BundleRoot 'results') -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($OutputPath) } | ForEach-Object { [ordered]@{ Path = $_.FullName; Length = $_.Length; SHA256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash } })
$payload = [ordered]@{ Schema = 'StorageChronicle.PhysicalAcceptanceCollection.v1'; GeneratedUtc = [DateTimeOffset]::UtcNow; BundleRoot = $BundleRoot; Files = @($files); AcceptanceEligible = $false; Note = 'Collection only; individual acceptance manifests determine eligibility.' }
$json = $payload | ConvertTo-Json -Depth 12
Write-NewJsonArtifact -Path $OutputPath -Value $payload -Depth 12
Write-Output $json
exit 0
