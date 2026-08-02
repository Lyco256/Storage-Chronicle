# MftEnumerator.cs

`WindowsMftEnumerator` implements lightweight MFT-style enumeration through the documented `FSCTL_ENUM_USN_DATA` public API. It streams file reference number, sequence, parent reference, name, USN, and standard file attributes in bounded batches. It does not parse `$MFT` sectors or read file contents. Access and media errors surface as `NtfsAccessException`; cancellation is honored between batches and records.

`NtfsReconciliationComparer` compares saved File ID, parent, name, and last USN values and returns `NtfsReconciliationCandidate` values rather than ordinary change events. Detailed metadata acquisition can be layered on only for changed directory candidates by the application-selected reconciliation flow.
