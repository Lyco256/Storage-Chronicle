# Windows FileSystem project

Targets `net10.0-windows10.0.19041.0` with `EnableWindowsTargeting` so the assembly can be restored and compiled from a non-Windows build host. It references only the platform-neutral contracts and abstractions. Runtime entry points guard Windows-only calls and return safe unsupported/empty results on non-Windows systems. Tests build the project directly because solution registration is an integration-branch concern.
