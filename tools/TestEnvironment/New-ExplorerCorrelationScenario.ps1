[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$TestId,
    [string]$RunId = ([guid]::NewGuid().ToString('N')),
    [string]$OutputPath,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Fail([string]$Message) { throw $Message }

try {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $volumeRoot = [IO.Path]::GetPathRoot($rootFull).TrimEnd('\')
    if ($rootFull.Equals($volumeRoot, [StringComparison]::OrdinalIgnoreCase)) { Fail "Refusing to use a volume root: $rootFull" }
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { Fail "TestLab root does not exist: $rootFull" }
    if ([string]::IsNullOrWhiteSpace($TestId)) { Fail 'TestId is required.' }

    foreach ($name in @('.storage-chronicle-testlab-marker.json', 'StorageChronicleTestVolume.json')) {
        $markerPath = Join-Path $rootFull $name
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { Fail "Required TestLab marker is missing: $markerPath" }
        $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
        if ([string]$marker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$marker.TestId -ne $TestId -or [string]$marker.Role -ne 'Workload' -or [string]$marker.FileSystem -ne 'NTFS') {
            Fail "The root is not the approved NTFS Workload TestLab volume for TestId=${TestId}: $markerPath"
        }
    }

    $safeRunId = $RunId -replace '[^A-Za-z0-9_.-]', '-'
    if ([string]::IsNullOrWhiteSpace($safeRunId)) { Fail 'RunId must contain at least one safe character.' }
    $scenarioRoot = Join-Path $rootFull "ExplorerCorrelation-$safeRunId"
    $output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $scenarioRoot 'explorer-correlation-plan.json' } else { [IO.Path]::GetFullPath($OutputPath) }
    if (-not $Apply -and [string]::IsNullOrWhiteSpace($OutputPath)) { $output = Join-Path $rootFull "ExplorerCorrelation-$safeRunId.preflight.json" }
    if (-not ($output.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or $output.StartsWith($rootFull + '\', [StringComparison]::OrdinalIgnoreCase))) { Fail "OutputPath must remain under the approved TestLab root: $output" }
    if (-not $Apply) {
        $preflight = [ordered]@{
            Schema = 'StorageChronicle.ExplorerCorrelationPlan.v1'
            Status = 'READY_FOR_USER_APPLY'
            AcceptanceEligible = $false
            Root = $rootFull
            ScenarioRoot = $scenarioRoot
            RunId = $safeRunId
            Reason = 'Pass -Apply only after confirming the marker, TestId, and disposable TestLab volume.'
        }
        $parent = Split-Path -Parent $output
        if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        $preflight | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $output -Encoding UTF8
        Write-Output ($preflight | ConvertTo-Json -Depth 12)
        exit 2
    }

    if (Test-Path -LiteralPath $scenarioRoot) { Fail "The scenario directory already exists; choose a new RunId: $scenarioRoot" }
    New-Item -ItemType Directory -Force -Path $scenarioRoot | Out-Null
    $directories = @('SourceTree', 'Destination', 'SameVolumeMove', 'DragDrop', 'Recycle')
    foreach ($directory in $directories) { New-Item -ItemType Directory -Force -Path (Join-Path $scenarioRoot $directory) | Out-Null }
    New-Item -ItemType Directory -Force -Path (Join-Path $scenarioRoot 'SourceTree\Folder') | Out-Null
    $emptyFiles = @(
        'SourceTree\one.txt',
        'SourceTree\Folder\nested.txt',
        'SameVolumeMove\move-me.txt',
        'DragDrop\copy-me.txt',
        'DragDrop\move-me.txt',
        'Recycle\delete-me.txt'
    )
    foreach ($relative in $emptyFiles) { New-Item -ItemType File -Force -Path (Join-Path $scenarioRoot $relative) | Out-Null }

    $relative = { param([string]$path) [IO.Path]::GetRelativePath($rootFull, $path).Replace('/', '\') }
    $rows = @(
        [ordered]@{ ScenarioId = 'copy-file-paste'; Order = 1; Operation = 'Copy file then paste'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'SourceTree\one.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\one-copy.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'copy-tree-paste'; Order = 2; Operation = 'Copy directory tree then paste'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'SourceTree'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\tree-copy'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'same-generation-second-paste'; Order = 3; Operation = 'Paste the same clipboard generation a second time'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'SourceTree\one.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\one-copy-2.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'clipboard-change-paste'; Order = 4; Operation = 'Change clipboard contents, then paste'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'SourceTree\Folder\nested.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\nested-copy.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'rename'; Order = 5; Operation = 'Rename an existing file in Explorer'; SourceRelativePath = $null; DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'SameVolumeMove\renamed.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'same-volume-move'; Order = 6; Operation = 'Move a file within the same volume'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'SameVolumeMove\move-me.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\moved.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'drag-copy'; Order = 7; Operation = 'Drag-and-drop copy'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'DragDrop\copy-me.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\drag-copy.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'drag-move'; Order = 8; Operation = 'Drag-and-drop move'; SourceRelativePath = & $relative (Join-Path $scenarioRoot 'DragDrop\move-me.txt'); DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Destination\drag-move.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'delete'; Order = 9; Operation = 'Delete a file'; SourceRelativePath = $null; DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Recycle\delete-me.txt'); Required = $true; ExpectedClassification = 'Observe' },
        [ordered]@{ ScenarioId = 'recycle-restore'; Order = 10; Operation = 'Move to Recycle Bin and restore when stable'; SourceRelativePath = $null; DestinationRelativePath = & $relative (Join-Path $scenarioRoot 'Recycle\delete-me.txt'); Required = $false; ExpectedClassification = 'Observe' }
    )
    $plan = [ordered]@{
        Schema = 'StorageChronicle.ExplorerCorrelationPlan.v1'
        Status = 'READY_FOR_MANUAL_RUN'
        AcceptanceEligible = $false
        RunId = $safeRunId
        TestId = $TestId
        Root = $rootFull
        ScenarioRoot = $scenarioRoot
        Instructions = @('Run the rows in Order inside the real Windows Explorer window.', 'Record the actual Explorer process evidence independently.', 'After the run, create StorageChronicle.ExplorerScenario.v1 rows with observed ExpectedCorrelation and ExpectedSourceFileId values.', 'This plan is preparation only and is never a live acceptance artifact.')
        Rows = $rows
        CreatedUtc = [DateTimeOffset]::UtcNow
    }
    $parent = Split-Path -Parent $output
    if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
    $plan | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $output -Encoding UTF8
    Write-Output ($plan | ConvertTo-Json -Depth 20)
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
