# Module dependencies

`Domain` is dependency-free. `Contracts` exposes stable ports. `Platform.Abstractions` contains OS-neutral collector contracts. Windows collectors implement those ports, while Application composes collectors, normalization, state, and storage. UI depends on projections and contracts only.
