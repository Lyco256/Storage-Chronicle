# New-ResourceQuietWitness.ps1

Runs as a separate lightweight PowerShell process during the final quiet interval of the formal resource measurement. It independently samples the Agent health named pipe, records the durable `LastSequence` delta as the observed event counter, records queue-depth bound violations and pending reconciliation observations, and checks for configured benchmark, workload, build, and test processes. It writes `StorageChronicle.ResourceQuietWitness.v1` JSON with the interval, source of each observation, samples, and failures.

The witness is fail-closed: a missing or malformed health response, sequence regression, sampling loss, forbidden process, queue bound violation, or non-zero observed event delta prevents `AcceptanceEligible=true`. The companion `Test-ResourceBudgetAcceptance.ps1` accepts only a passed artifact with this schema; a hand-written JSON containing only the legacy four fields is rejected. `BulkEventCount` is explicitly the Agent health `LastSequence` delta, not an inferred workload count, and the artifact remains environment acceptance evidence only after the real Release Agent/Session Agent 600-second run completes.

## Inputs and outputs

`-OutputPath` is the required evidence destination. `-AgentPipeName`, `-DurationSeconds`, `-IntervalMilliseconds`, `-MaxQueueDepth`, and `-ForbiddenProcessName` control only the bounded observation. The output contains `QuietPeriodStartedUtc`, `QuietPeriodCompletedUtc`, `QuietPeriodSeconds`, `BulkEventCount`, `QueueOverrunCount`, `ReconciliationActiveTimeSeconds`, process-absence flags, health samples, and errors.

## Failure behavior and tests

The script requires Windows and a reachable versioned Agent health pipe. It writes a failed, ineligible artifact before returning non-zero when prerequisites or observations fail. PowerShell parser validation covers the script; formal 300-second witness execution requires the approved Release Agent/Session Agent environment and is not implied by repository tests.
