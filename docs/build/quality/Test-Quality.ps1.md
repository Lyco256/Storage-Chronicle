# Test-Quality.ps1

Composes documentation mirror, architecture, integration, and CodeCoverage gates. CodeCoverage uses Microsoft.Testing.Extensions.CodeCoverage and therefore requires the repository's .NET 10 test runner selection to be `Microsoft.Testing.Platform`; it fails loudly when that integration-owned prerequisite is absent.
