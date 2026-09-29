# WindowsCollectorAdapters

Connects platform collectors to the bounded Agent contract. NTFS volumes use FSCTL USN plus first-run public MFT enumeration and persist a per-volume journal cursor; the directory collector skips those volumes to prevent duplicate facts. Clipboard uses the hidden `AddClipboardFormatListener` source, and shares use registry notification with the documented fallback. No file contents or hashes are read.
