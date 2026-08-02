# DocMirrorValidator/Program.cs

Validates that every non-generated source file under `src/` has a non-empty relative markdown explanation under `docs/src/`. It fails with a non-zero exit code and names each missing or empty mirror.
