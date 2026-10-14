# Fork releases and updates

Only `MRoiban/LuaTools` releases are used. Upstream feeds, mirrors and Velopack feeds cannot supply application updates.

The source-owned Steam frontend and WinMM wrapper ship inside the same application
assembly and signed release ZIP. CI compiles the wrapper from `native/steam-plugin-loader`,
checks all 180 exports, runs its native harness and the frontend behavior tests, then
builds/tests the app. No LTSP binaries or frontend assets are downloaded during this process.
The frontend changes with the app release. Replacing a changed native DLL in Steam
uses the plugin's existing explicit Install/Update action because Steam must restart.
The loader's configured app directory is retained by update cleanup so it remains a
working bootstrap into approved future app releases.

On a push to `security-hardened`, the build job restores the locked package graph, builds, and runs the complete test suite. Only after success may a separate guarded release job publish. Versions are `1.4.N`, where N is the number of commits since baseline `946125981a7addb6150e83886443cc8ddb68931b`. Preserve this ancestry; do not reset/rewrite release history. The release job publishes the exact tested commit, rechecks branch tip before publication, and skips existing version tags. Ordinary developer builds remain `1.4.0` and do not make update requests.

Each portable Windows x64 ZIP is accompanied by `release.json` and binary `release.sig`. The signature is ECDSA P-256 / SHA-256, IEEE P1363 fixed-width 64 bytes, over the exact UTF-8 manifest bytes. The signed manifest binds the fork repository, source commit, version, fixed asset filename, byte length and SHA-256. The updater pins the embedded SPKI public PEM (`Resources/release-signing-public.pem`). SHA-256 alone and HTTPS hosting do not authorize execution.

Setup: put the matching PKCS#8 private PEM in the fork's GitHub Actions secret `RELEASE_SIGNING_KEY`. The signing script verifies that the secret matches the pinned public key before writing metadata. Never commit the private key. Fork PRs receive no signing step. Restrict write access to the default branch and workflows, require review/checks where the GitHub account supports it, and keep the release key secret. Rotation requires a release signed with the old key that deliberately ships the new pin, or a separately trusted manual installation; replacing the online key alone does not change client trust.

Release builds silently download a newer signed package using the app's bounded HTTPS transport. Metadata, download and extraction have size limits; wrong signatures, repositories, versions, paths, archive links, duplicate paths and package hashes fail closed. ZIP bytes are locked during hash verification and extraction. UNC/reparse-point cache paths and existing extraction targets are rejected. All update cryptography tests use ephemeral test keys and inert executable-shaped fixtures, never the production key or live downloads.

An update only runs after the user selects the existing Restart action in the update toast. Normal exit does not apply it. Restart verifies again, extracts to a new GUID directory under `%LocalAppData%\LuaToolsGui-SecurityHardened\signed-updates\runs`, and directly launches `LuaTools.exe` without a shell or elevated installer. The child waits for its predecessor to exit. An atomic approved-package pointer lets the original shortcut bootstrap into that approved release on future launches; every bootstrap revalidates the signature and ZIP and extracts fresh files. The original portable directory is preserved. Framework-dependent releases require the .NET 8 Desktop Runtime x64, installed separately by the user.

Cleanup keeps the two newest cached package/run directories plus any active approved package, current pending package, or running directory. Locked files and unsafe redirected directories are left untouched and retried on later launches. No background installer or runtime downloader is bundled. A same-user process that can rewrite the application or mutate freshly extracted executable files can defeat application-level checks; the updater does not claim an OS isolation boundary against that attacker. Removing approved local state also removes the remembered upgrade floor, so local malware/admin rollback is outside this boundary. Server/network replay cannot downgrade below the running or remembered approved version.
