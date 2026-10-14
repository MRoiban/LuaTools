# Executable game-fix workflow audit

Date: 2026-10-15. Reviewer: Codex static-review agent. Decision: **no generic or per-artifact executable approval**. This review covers the local installer boundary, not the contents of any remotely supplied game fix.

## Scope and exact local snapshot

The checkout HEAD was `946125981a7addb6150e83886443cc8ddb68931b`. Relevant files contain uncommitted hardening changes, so HEAD alone does not identify this review. The following SHA-256 hashes identify the reviewed working-tree snapshots; references below use their observed line numbers:

| File under `src/LuaToolsGui/Services/` | SHA-256 |
| --- | --- |
| `ExecutableFixPolicy.cs` | `48B077DDDCD349A6DDCD16DC2808B78480B1047FF69C91D563F282E2D1E61E84` |
| `Downloads/ManifestJobFactory.cs` | `FBCEACE98C8235B9DC0841BFCC350FF76EE8FB416C95394381D7F081E8E6D63A` |
| `UserAuthorizedDependencies.cs` | `8F5AA3D3F73A0A6DDB8E8E45F4DEFBA24B1A65E949B65B3003A3095E79DFBDC5` |
| `ManifestContentPolicy.cs` | `84ED6F0535FACA43850EFD054947D7F50565CA808DF794261D61975619561724` |

Read `DEPENDENCY_TRUST.md`; inspected the files above plus `LuaToolsApiClient.cs:246-264`, the response model and relevant apply/revert code. No arbitrary fix URL was requested, downloaded, unpacked or executed. No executable, script, build or application test was run, and no application code/settings were changed by this review. Only this audit document was written for this additional scope.

Unlike a named GitHub dependency, this setting covers a class of backend-selected archives. There is no single fixed upstream source repository or pinned fix revision to fetch and audit. The server's `fixId`/slot response is not a source identity.

## What the local policy establishes

`ManifestJobFactory.cs:81-110` distinguishes the non-executable `manifest` slot from the executable `fix` slot. It checks `DenuvoFix` consent and game installation before requesting executable bytes. `ExecutableFixPolicy.cs:9-25` rechecks consent, rejects reparse-point paths, opens the downloaded file, takes a bounded in-memory snapshot and validates ZIP structure. `ApplyDenuvoFix` uses that snapshot (`ManifestJobFactory.cs:608-633`) rather than reopening potentially changed download bytes during extraction.

The applicable fix archive limits are **256 MiB input and aggregate declared expansion, 64 MiB per entry, 4096 entries, and 200:1 per-entry expansion with a 1 MiB allowance** (`ManifestContentPolicy.cs:12-16,155-174`). These differ from the 512 MiB generic executable-dependency bundle limit. Relative-path validation, case-insensitive duplicate checks, ZIP link/special-file rejection and destination reparse checks constrain filesystem writes. The installer rejects `.luatools-fix` path components so an archive cannot directly overwrite its recovery records (`ManifestJobFactory.cs:637-646`).

The installer plans changes, writes a provisional recovery record before applying, keeps original-file backups, rechecks consent during apply, records before/after file hashes and reports partial failures (`ManifestJobFactory.cs:654-739`). These controls reduce accidental corruption and archive-path abuse. They do not inspect executable semantics or certify publishers.

## Findings and remaining risks

### High: archive origin and executable behavior remain unreviewed

`LuaToolsApiClient.cs:254-264` requests `/api/denuvo/download?fix=...&slot=...` and downloads the URL returned by the backend. `Models/FixModels.cs:55-58` defines only a `url` in that response. No pinned source revision, independently approved archive digest, per-file executable digest or build attestation is required by `ExecutableFixPolicy.ReadPackage`. ZIP validation uses `manifestPackage: false`; `ManifestContentPolicy.cs:180` therefore deliberately skips the non-executable-content restriction.

An allowed archive can contain replacement game executables, DLL loaders, scripts or configuration that affects executable loading. It can replace permitted paths throughout the game directory (`ManifestJobFactory.cs:654,713-714`). A compromised publisher, storage account, backend or deliberately harmful fix can supply code with the user's privileges when the game loads it. HTTPS/destination protections and a signed storage URL establish transport/access properties, not benign executable behavior. No malware, credential theft, telemetry or persistence claim can be confirmed or excluded without the exact archive and its executable inputs. No malicious fix was observed or fetched in this review.

### High trust limitation: installed fixes are outside future launch-receipt checks

The generic `UserAuthorizedDependencies` mechanism records a complete file set with SHA-256 (`UserAuthorizedDependencies.cs:87-106`) and rechecks that set and consent before controlled tool launches (`109-131`). Those records are explicitly user-writable consent/integrity evidence, not independent trust.

The fix apply path does **not** call that mechanism. It uses `ExecutableFixPolicy.ReadPackage`, then writes files directly into the game directory and records recovery information (`ManifestJobFactory.cs:610,714-727`). The `HashAfter` values support conflict-aware revert; they are not an independently reviewed allowlist or mandatory checks before Steam/game execution. Disabling executable fixes stops subsequent authorized installation steps. It does not remove installed patches or prevent Steam, a launcher or a user from later executing an already patched game or loading an installed DLL. The local installer cannot constrain that code's later networking, self-modification, persistence or writes outside the game directory.

### Medium: partial extraction can leave changed files absent from the settled recovery record

`ManifestJobFactory.cs:714` extracts over the destination. The entry is added to `applied` only after hashing succeeds at lines 719-720. If extraction partially overwrites a file and then throws, or hashing fails after a successful overwrite, line 722 increments the failure count without recording the changed entry. Lines 726-727 replace the provisional record with only `applied` entries. The backup may still exist, but normal revert no longer has the corresponding mapping. This is a source-derived failure scenario, not a reproduced test result.

Consider preserving each started write in the recovery record and using a fully written staged file with validated hash before replacement. Reported partial failure does not by itself mean every changed file remains automatically recoverable.

### Medium: consent, backups and mutable records do not provide publisher integrity or atomic rollback

Backups are retained when already present (`ManifestJobFactory.cs:692-695`); `HashBefore` describes the destination before overwrite, not necessarily an older existing backup. Revert checks destination conflicts and restores a backup (`883-897`), without independently authenticating that backup against publisher provenance. Records and backups live in a user-writable game directory. Same-user tampering, concurrent game updates and repeated/layered fixes can therefore complicate recovery. Apply is sequential and can stop part way through; it is not an atomic package transaction. Reparse checks reduce link abuse but are pathname checks, not an atomic defense against a concurrently malicious same-user process.

These are integrity/recovery limitations rather than evidence that the remote fixes are malicious. Revert is useful operational recovery; it cannot undo arbitrary behavior of code that has already executed.

## Per-artifact approval requirements

Keep the independent executable allowlist empty for this workflow. A setting-wide approval, backend ID, storage URL, checksum supplied by the same publisher or local revert record cannot approve all future fixes. For each candidate fix, independently record and review:

1. The exact game/app ID, compatible game/depot build, fix version, source owner and immutable revision, complete archive identity and file inventory. Capture archive SHA-256 and every executable/script/DLL/configuration input that affects code loading.
2. The complete pinned source, loader/injector/plugin behavior, network/update/persistence behavior, native operations, transitive dependencies and build scripts/toolchain. Where source is missing, state that limitation and perform a separately justified exact-binary review; do not label an installer-only review a source audit of the fix.
3. Reproducible-build comparison or independently verifiable provenance linking the reviewed inputs to the exact distribution. Publisher-hosted release hashes alone show publisher consistency, not independent source-to-binary linkage.
4. Expected destination paths and modifications, original game hashes/compatibility, safe apply/rollback behavior and any DLL search/load side effects. Verify in an isolated environment with no live Steam credentials or production game install before considering execution approval.
5. Reviewer identity, audit date, findings/limitations, exact archive and per-file hashes and the narrow scope approved. New bytes, a new game build, new dependencies or an updated fix require reassessment.

No candidate artifacts, source revisions or independently reproduced binaries were available for this workflow, so none of those per-artifact approval requirements was satisfied here. This document covers the eighth settings-controlled executable dependency class while explicitly leaving remote executable-content trust unresolved.
