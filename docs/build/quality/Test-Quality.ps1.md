# Test-Quality.ps1

Runs documentation, architecture, integration, coverage, and VirtualBox TestLab boundary contract validation. The VirtualBox contract test uses a mocked `VBoxManage` process seam only for provider-boundary failure cases; it does not replace real Windows guest acceptance.

Composes documentation mirror, architecture, integration, and CodeCoverage gates. CodeCoverage uses Microsoft.Testing.Extensions.CodeCoverage and therefore requires the repository's .NET 10 test runner selection to be `Microsoft.Testing.Platform`; it fails loudly when that integration-owned prerequisite is absent.
