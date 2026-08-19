# Run-RealMachineInstallerAcceptance.ps1

Human-started runner for the physical Windows 10 22H2 or Windows 11 installer matrix. It verifies bundle completeness and hashes, target ProductName/DisplayVersion/build, x64, administrator token, an approved test-data root containing matching TestLab `.storage-chronicle-testlab-marker.json` and `StorageChronicleTestVolume.json` markers, free space, and a clean Program Files/ProgramData/product/service target before asking for exact `YES` confirmation. It invokes the existing fail-closed installer matrix with the real case driver and writes a non-eligible preflight/failure artifact when the environment is wrong.

It requires a DPAPI-protected non-admin credential reference and never accepts a plaintext password. It does not call remote control, does not treat VM/fixture output as physical acceptance, and never deletes history. Tests: parser validation and wrong-environment fail-closed path.
