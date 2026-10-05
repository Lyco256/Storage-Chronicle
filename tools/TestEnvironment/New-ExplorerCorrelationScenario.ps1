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
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'build/quality/AcceptanceContracts.ps1')

function Fail([string]$Message) { throw $Message }

function Assert-NoReparsePath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootPath = [IO.Path]::GetPathRoot($full)
    $current = $rootPath
    foreach ($part in $full.Substring($rootPath.Length).Split([IO.Path]::DirectorySeparatorChar, [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail "Reparse points are not allowed in the approved TestLab path: $current" }
        }
    }
}

function Write-NewUtf8File([string]$Path, [string]$Text) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
}

function New-EmptyFile([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Flush($true) } finally { $stream.Dispose() }
}

try {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $volumeRoot = [IO.Path]::GetPathRoot($rootFull).TrimEnd('\')
    if ($rootFull.Equals($volumeRoot, [StringComparison]::OrdinalIgnoreCase)) { Fail "Refusing to use a volume root: $rootFull" }
    if (Test-AcceptancePathIsProtected -Path $rootFull) { Fail "TestLab root is within a protected user, system, synchronized, or application path: $rootFull" }
    if (Test-AcceptancePathWithinProtectedRoot -Path $rootFull -ProtectedRoot $repositoryRoot) { Fail "TestLab root must be outside the repository: $rootFull" }
    if (-not (Test-Path -LiteralPath $rootFull -PathType Container)) { Fail "TestLab root does not exist: $rootFull" }
    if ([string]::IsNullOrWhiteSpace($TestId)) { Fail 'TestId is required.' }
    Assert-NoReparsePath $rootFull
    $allowedMarkerNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [void]$allowedMarkerNames.Add('.storage-chronicle-testlab-marker.json')
    [void]$allowedMarkerNames.Add('StorageChronicleTestVolume.json')
    foreach ($entry in Get-ChildItem -LiteralPath $rootFull -Force -ErrorAction Stop) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $entry.PSIsContainer -or -not $allowedMarkerNames.Remove($entry.Name)) {
            Fail "TestLab root is not fresh; refusing an unexpected entry: $($entry.FullName)"
        }
    }
    if ($allowedMarkerNames.Count -ne 0) { Fail 'TestLab root must contain exactly the two ownership marker files and no other entries.' }
    $volume = Get-Volume -FilePath $rootFull -ErrorAction Stop
    if ($null -eq $volume -or [string]$volume.DriveType -ne 'Fixed' -or [string]$volume.FileSystem -ne 'NTFS' -or [string]::IsNullOrWhiteSpace([string]$volume.UniqueId)) {
        Fail 'TestLab root must resolve to a local fixed NTFS volume with a stable identity.'
    }

    $markers = @()
    foreach ($name in @('.storage-chronicle-testlab-marker.json', 'StorageChronicleTestVolume.json')) {
        $markerPath = Join-Path $rootFull $name
        if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { Fail "Required TestLab marker is missing: $markerPath" }
        Assert-NoReparsePath $markerPath
        $marker = Get-Content -Raw -Encoding UTF8 -LiteralPath $markerPath | ConvertFrom-Json
        if ([string]$marker.Schema -ne 'StorageChronicle.TestLabDataMarker.v1' -or [string]$marker.TestId -ne $TestId -or [string]$marker.Role -ne 'Workload' -or [string]$marker.FileSystem -ne 'NTFS' -or [string]$marker.VolumeUniqueId -ne [string]$volume.UniqueId -or [string]$marker.VolumeLabel -ne [string]$volume.FileSystemLabel) {
            Fail "The root is not the approved NTFS Workload TestLab volume for TestId=${TestId}: $markerPath"
        }
        $markers += $marker
    }
    if ([string]$markers[0].VolumeUniqueId -ne [string]$markers[1].VolumeUniqueId -or [string]$markers[0].TestId -ne [string]$markers[1].TestId) { Fail 'The two TestLab ownership markers disagree.' }
    if ([string]$volume.FileSystemLabel -ne 'SC_TEST_VOLUME') { Fail 'TestLab volume label is not the approved workload label.' }

    $safeRunId = $RunId -replace '[^A-Za-z0-9_.-]', '-'
    if ([string]::IsNullOrWhiteSpace($safeRunId)) { Fail 'RunId must contain at least one safe character.' }
    $scenarioRoot = Join-Path $rootFull "ExplorerCorrelation-$safeRunId"
    if (-not $Apply -and -not [string]::IsNullOrWhiteSpace($OutputPath)) { Fail 'Preflight is read-only and prints its result to stdout; OutputPath is accepted only with -Apply.' }
    $output = if ([string]::IsNullOrWhiteSpace($OutputPath)) { Join-Path $scenarioRoot 'explorer-correlation-plan.json' } else { [IO.Path]::GetFullPath($OutputPath) }
    if (-not (Test-AcceptancePathWithinProtectedRoot -Path $output -ProtectedRoot $scenarioRoot)) { Fail "OutputPath must be a direct child of the new run-owned scenario directory: $scenarioRoot" }
    if ($Apply) {
        if (Test-Path -LiteralPath $scenarioRoot) { Fail "The scenario directory already exists; choose a new RunId: $scenarioRoot" }
        if (-not (Split-Path -Parent $output).Equals($scenarioRoot, [StringComparison]::OrdinalIgnoreCase)) { Fail 'Plan output must be directly inside the new run-owned scenario directory.' }
        if (Test-Path -LiteralPath $output) { Fail "Plan output already exists; refusing to overwrite it: $output" }
    }
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
        Write-Output ($preflight | ConvertTo-Json -Depth 12)
        exit 2
    }

    New-Item -ItemType Directory -Path $scenarioRoot | Out-Null
    $directories = @('SourceTree', 'Destination', 'SameVolumeMove', 'DragDrop', 'Recycle')
    foreach ($directory in $directories) { New-Item -ItemType Directory -Path (Join-Path $scenarioRoot $directory) | Out-Null }
    New-Item -ItemType Directory -Path (Join-Path $scenarioRoot 'SourceTree\Folder') | Out-Null
    $emptyFiles = @(
        'SourceTree\one.txt',
        'SourceTree\Folder\nested.txt',
        'SameVolumeMove\move-me.txt',
        'DragDrop\copy-me.txt',
        'DragDrop\move-me.txt',
        'Recycle\delete-me.txt'
    )
    foreach ($relative in $emptyFiles) { New-EmptyFile (Join-Path $scenarioRoot $relative) }

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
    if (-not $parent -or -not (Test-Path -LiteralPath $parent -PathType Container)) { Fail "Plan output parent must already exist: $parent" }
    Assert-NoReparsePath $parent
    Write-NewUtf8File $output ($plan | ConvertTo-Json -Depth 20)
    Write-Output ($plan | ConvertTo-Json -Depth 20)
    exit 0
}
catch {
    Write-Error $_.Exception.Message
    exit 1
}
