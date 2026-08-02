# ADR 005: Folder relationships instead of descendant synthetic events

Folder moves and deletes preserve versioned parent/name relationships and reconstruct descendants at the requested time; they do not manufacture one event per descendant.
