# Collect-PhysicalAcceptanceResults.ps1

Collects only result files already written under a manual bundle's `results/` directory and writes a manifest with file metadata and hashes. It uses the shared `Write-NewJsonArtifact` contract, which atomically publishes a fully flushed same-directory temporary and never replaces an existing file; an output collision fails closed. It does not infer pass/fail, alter acceptance eligibility, read arbitrary user data, or delete evidence. Individual result manifests and the final acceptance aggregator remain authoritative.

Tests: parser validation and empty-results collection path.
