# ArchitectureContractTests.cs

Checks required project boundaries and prevents the Domain project from acquiring implementation references. It also protects real-I/O workload contracts: marked run-bound create-new outputs, exact-tree checks before recursive deletion, and rejection of system, user-profile, OneDrive, and repository fixture roots.
