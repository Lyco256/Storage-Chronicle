# WindowsExclusionPolicy

Applies Storage Chronicle data-root, standard Windows directories, and user-selected roots before metadata/event creation. Prefix matching is path-boundary aware so similarly named siblings remain observable. Reparse detection is exposed as a traversal invariant. Configuration changes remain outside this collector and must be recorded by the settings owner.
