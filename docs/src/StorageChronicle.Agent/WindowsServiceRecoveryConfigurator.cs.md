# WindowsServiceRecoveryConfigurator.cs

`WindowsServiceRecoveryConfigurator` is the Windows-only service-host boundary that applies the durable Service Control Manager recovery actions after the service has been installed. It configures restart delays of 5, 15, and 60 seconds and leaves the service running when configuration is unavailable, such as a developer console launch or insufficient service-manager permissions.

The implementation uses only `advapi32.dll` service APIs and does not start a shell command. It never touches Storage Chronicle history. The installer provides the initial service registration; this startup step installs the distinct three-stage delays once the service exists.

Tests and validation: the installer manifest test checks service registration and recovery metadata, while the Windows integration suite validates the P/Invoke layout and the service-host startup path without requiring a machine-wide service mutation.

## Role

This mirror documents the source boundary for this file and explains how it participates in Storage Chronicle.

## Public types and responsibilities

Public types preserve source facts and the explicitly owned responsibility; UI interpretation and correlation remain outside this boundary.

## Inputs and outputs

Inputs and outputs are the declared contracts of the source file. File contents and file-content hashes are never an input or output.

## Dependencies

Dependencies are limited to the referenced project contracts and platform services shown by the source file.

## Invariants

The source keeps canonical facts distinguishable from reconstructed state and does not synthesize descendant events.

## Threading and lifetime

Callers own cancellation and lifetime; asynchronous work must not outlive the owning pipeline or UI scope.

## Failure behavior

Failure, corruption, cancellation, and recovery remain observable and are not converted into a false successful observation.

## Tests

Validated by tests/StorageChronicle.Integration.Tests and the affected integration tests.

## OS constraints

Platform-neutral behavior remains portable; Windows-only APIs are isolated in the Windows platform projects.

## Change-sensitive contracts

Public names, serialized fields, persistence boundaries, and the mirrored path are compatibility-sensitive contracts.
