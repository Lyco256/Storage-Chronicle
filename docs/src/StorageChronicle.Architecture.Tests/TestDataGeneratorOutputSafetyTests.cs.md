# TestDataGeneratorOutputSafetyTests.cs

Exercises the TestDataGenerator CLI against a uniquely named temporary fixture root. It verifies successful new-file creation, preservation of an existing target, refusal to create a missing parent, refusal of traversal paths, and refusal of reparse-point parents. On Windows, it also opens the output guard and proves the fixture directory cannot be renamed until the guard is disposed. The symbolic-link case is skipped when the host cannot create links. These tests cover output behavior, not approval or isolation of an evidence root.
