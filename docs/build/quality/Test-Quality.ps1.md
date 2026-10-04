# Test-Quality.ps1

Runs documentation, architecture, integration, physical MFT seed identity contracts, coverage, and VirtualBox legacy-boundary validation. The MFT contracts use only in-memory inventories and explicitly assert that physical MFT execution remains blocked without an audited seed-creation provenance workflow. The VirtualBox contract test uses a mocked `VBoxManage` process seam only for provider-boundary failure cases; it does not replace physical Windows acceptance.

Composes documentation mirror, architecture, integration, MFT seed contract, and CodeCoverage gates. CodeCoverage uses Microsoft.Testing.Extensions.CodeCoverage and therefore requires the repository's .NET 10 test runner selection to be `Microsoft.Testing.Platform`; it fails loudly when that integration-owned prerequisite is absent.
