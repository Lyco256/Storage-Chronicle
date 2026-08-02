# InitialScanNotificationBuffer

Implements the bounded queue between the monitor start boundary and initial snapshot completion. It exposes the boundary sequence, retains source ordering, and marks overflow explicitly; it never silently drops notifications or labels an incomplete initial state continuous.
