# Code signing policy

Status: **being set up.** Releases will be signed with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (Microsoft's managed code-signing service), using a Public Trust certificate issued to the project's maintainer after Microsoft's identity validation. Until the first signed release, release files are unsigned and Windows or your antivirus may warn about them.

## What is signed

Only `EdgeLink.exe` and the installer `EdgeLink-Setup-<version>.exe`, both built by GitHub Actions from this repository (`.github/workflows/build.yml`) when a version tag is pushed. `EdgeLink.exe` is signed before the installer is built, so the installer carries the signed copy. Every signature is timestamped. No file built on a personal computer is signed.

The workflow logs in to Azure with OpenID Connect (no stored password or key); the signing key never leaves Microsoft's service.

## How to check a download

Right-click the file, choose **Properties > Digital Signatures**: the signer should be the publisher named on the [releases page](https://github.com/ultimateunraid/EdgeLink/releases), with a valid timestamp. In PowerShell: `Get-AuthenticodeSignature .\EdgeLink-Setup-<version>.exe`.

EdgeLink's built-in updater checks this too: once your copy is signed, it only installs an update that is validly signed by the same publisher, and it always checks the download against the SHA-256 checksum GitHub publishes for the release.

## Team roles

- Committers and reviewers: [members of the ultimateunraid organization](https://github.com/orgs/ultimateunraid/people)
- Approvers (who can push version tags and so trigger a signed release): [owners of the ultimateunraid organization](https://github.com/orgs/ultimateunraid/people?query=role%3Aowner)

## Privacy

EdgeLink does not collect or send personal data. See [Privacy](README.md#privacy) for the outbound requests it makes (item icons, map art and the update check).
