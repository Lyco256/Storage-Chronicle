# Code-signing procedure

Storage Chronicle has two deliberately separate publication paths:

1. Development builds are unsigned. They are for local testing only, and the MSI and its three self-contained executables must be labeled as unsigned development artifacts.
2. Release builds are signed after the self-contained binaries and MSI have been built and validated. The release pipeline must use an organization-controlled Authenticode certificate or signing service, verify the certificate chain and timestamp, and publish the signature verification output with the release evidence.

The repository does not contain a certificate, private key, password, signing endpoint, or automatic SmartScreen bypass. A release operator should sign the Agent, Session Agent, UI executable, and final MSI with the approved signing service, then verify them with the organization’s approved Authenticode verification command before distribution. Signing must not change service ACLs, named-pipe ACLs, UAC behavior, or any other security boundary.

The unsigned development path remains the default for `build/package/Build-Installer.ps1`; a future signed-release pipeline must be an explicit wrapper around that output and must fail if a required signature or timestamp is absent. No installer behavior may be added solely to suppress SmartScreen warnings.
