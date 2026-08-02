# IpcProtocol.cs

Defines versioned local IPC envelopes and a 4-byte little-endian length prefix with an 8 MiB cap. Protocol major mismatches and malformed lengths are rejected before payload allocation. JSON metadata is source-generated and shared by Agent, Desktop, and Session Agent.
