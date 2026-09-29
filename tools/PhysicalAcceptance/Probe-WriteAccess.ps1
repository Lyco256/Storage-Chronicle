[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
$probe = Join-Path $Path ('probe-' + [guid]::NewGuid().ToString('N') + '.tmp')
$createdByThisRun = $false
$probeExitCode = 2
try {
    $stream = [IO.File]::Open($probe, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    $createdByThisRun = $true
    try { $stream.WriteByte(0x53); $stream.Flush($true) } finally { $stream.Dispose() }
    # Exit 1 means the write probe was allowed; it is a test failure, not a denied-write pass.
    $probeExitCode = 1
} catch [UnauthorizedAccessException] {
    $probeExitCode = 0
} catch [System.Security.SecurityException] {
    $probeExitCode = 0
} catch [IO.IOException] {
    if (($_.Exception.HResult -band 0xFFFF) -eq 5) { $probeExitCode = 0 }
    else {
        $probeExitCode = 2
        [Console]::Error.WriteLine("Write probe failed for a reason other than access denied: $($_.Exception.Message)")
    }
} catch {
    $probeExitCode = 2
    [Console]::Error.WriteLine("Write probe could not establish an access-denied result: $($_.Exception.Message)")
} finally {
    if ($createdByThisRun -and (Test-Path -LiteralPath $probe -PathType Leaf)) {
        try { [IO.File]::Delete($probe) } catch {
            $probeExitCode = 2
            [Console]::Error.WriteLine("Could not remove this run's probe file: $($_.Exception.Message)")
        }
    }
}
exit $probeExitCode
