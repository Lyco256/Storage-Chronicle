# ReadDirectoryChangesBufferParser

Parses `FILE_NOTIFY_INFORMATION` records with bounds and alignment checks. Rename-old/new pairs are correlated, source sequence order is preserved, and malformed records return a failure result rather than being treated as continuous. The parser does not read file contents and is covered by synthetic notification and malformed-buffer tests.
