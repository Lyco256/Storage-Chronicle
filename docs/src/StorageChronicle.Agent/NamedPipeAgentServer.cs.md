# NamedPipeAgentServer

Hosts the local four-byte little-endian/source-generated JSON IPC endpoint. It applies the 8 MiB and protocol-major gates, DACLs SYSTEM/Administrators and authenticated users, validates client SID/session/admin identity, dispatches projection/diff/health/settings/clipboard messages, and returns explicit rejection errors. Authentication and Windows pipe failures are isolated and fail closed for mutating endpoints; malformed or disconnected clients do not stop the Agent or prevent the next connection.
