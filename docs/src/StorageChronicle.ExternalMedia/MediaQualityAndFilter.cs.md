# MediaQualityAndFilter.cs

Classifies NTFS/USN, FAT/FAT32, exFAT, read-only, unsupported, and continuity-gap states without changing the volume. NTFS with USN support can be exact; a gap requests USN recovery; non-USN or non-NTFS media requests full reconciliation; read-only media remains read-only. `PlanRecovery` explicitly reports that it never creates a USN journal.

`MediaEventFilter` selects by volume, logical media identifier, and mount session, and an unspecified filter still requires media identity on the event. Tests verify system events are excluded and each filesystem quality is honest.
