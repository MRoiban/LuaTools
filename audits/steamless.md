# Steamless dependency source audit

Date: 2026-10-15. Reviewer: Codex static-review agent. Decision: **not approved for the independent executable allowlist**. This is a scoped source review, not proof that the release binaries are benign.

## Identity and method

- Repository: https://github.com/atom0s/Steamless
- Exact reviewed revision: `cd770bf9749d3e4f438d23ac643917ad1a804257`, checked out detached after querying upstream HEAD. Commit date: 2024-03-29T22:13:07-07:00. Tag `v3.1.0.5` resolves to this commit in the fetched repository.
- Source acquired into a unique directory outside the workspace: `C:\Users\Alex\AppData\Local\Temp\steamless-source-audit-8a0b90a81852425d9b4d6a623fe68632`.
- Read local `DEPENDENCY_TRUST.md` and `src/LuaToolsGui/Services/SteamlessService.cs`. Inspected CLI loading and dispatch, GUI loading/resolution, PE32/PE64 parsing and unmanaged helpers, unpacker input/output paths, options, project references, bundled DLL inventory, and repository build/release configuration. Repository-wide static searches covered process, networking, registry, dynamic assembly, native API and write primitives. This was not an exhaustive semantic review of every cryptographic/unpacking operation.
- Only Git operations, metadata HTTP reads, file reads/searches, and SHA-256 hashing were performed. No fetched executable, DLL, project, build, script, test, installer, or game executable was run. No allowlist, settings, application service, or live Steam files were changed.

All upstream source paths/line numbers below refer to this exact commit. A permanent source root is [the pinned tree](https://github.com/atom0s/Steamless/tree/cd770bf9749d3e4f438d23ac643917ad1a804257).

## Findings

### High: every installed plugin is executable authority; compatibility checks are not authentication

`Steamless.CLI/Program.cs:97-128` enumerates every `Plugins/*.dll`, loads bytes using `Assembly.Load`, discovers subclasses, instantiates them and invokes `Initialize`. The attribute check at lines 116-124 only compares API version. Assembly resolution at lines 153-177 also reads dependency DLLs from `Plugins` without signature/hash approval. GUI behavior repeats this in `Steamless/Model/DataService.cs:81-124` and `Steamless/App.xaml.cs:51-75`.

An added or replaced compatible plugin can execute arbitrary code with the invoking user's rights before any game processing. This is an intentional extension trust boundary, not evidence of a malicious upstream plugin. Approving only `Steamless.CLI.exe` cannot approve the workflow. The complete API, plugins, transitive assemblies and archive contents need independent review. LuaTools' complete-file receipt helps detect changes against the installed receipt; the receipt and explicit consent do not supply that independent approval.

### High, potential memory safety exposure: unmanaged disassembly span is not bounded to the allocated file buffer

`Steamless.Unpacker.Variant20.x86/Main.cs:453-469` allocates exactly `FileData.Length` bytes, computes an unmanaged pointer by adding the input-derived entry-point file offset, and passes that pointer with a fixed length of 4096 to `SharpDisasm.Disassembler`. Variant 2.1 repeats it in `Steamless.Unpacker.Variant21.x86/Main.cs:573-589`. Neither call site proves that the offset lies within the allocation or that 4096 bytes remain. `Steamless.API/PE32/Pe32File.cs:352-355` performs RVA arithmetic without such a remaining-length check.

For crafted files that reach this path, the declared disassembly range can extend beyond the allocation. Out-of-bounds reads/crashes are a credible concern; exploitability and actual behavior inside SharpDisasm were not established because the repository supplies its DLL without its source and no binary was executed or decompiled. This is not a confirmed arbitrary-code-execution exploit. Validate offsets and bound the length before constructing a disassembler; independently review the exact disassembler implementation. Process isolation with reduced rights would limit impact from parser defects.

### Medium: malformed PE fields can cause unbounded allocation or parsing failures

`Steamless.API/PE32/Pe32File.cs:82-115` reads the entire input and allocates DOS-stub/section buffers from PE values without a comprehensive prior file-range/allocation budget. The TLS loop at lines 149-159 relies on finding a zero callback, without an explicit table count. PE64 has analogous behavior (`Steamless.API/PE64/Pe64File.cs:82-115,154-164`). Header-controlled payload/DRMP sizes also drive allocations and copies, for example `Steamless.Unpacker.Variant31.x86/Main.cs:269-270,309-311`.

Managed bounds checks reject many invalid accesses, but large allocations, exceptions and incomplete processing remain possible. `Pe32Helpers.cs:43-54` and `Pe64Helpers.cs:43-54` allocate unmanaged storage before copying and do not use `finally` for cleanup if copying/marshalling throws. The helper reduces an oversized structure copy to the entire buffer length rather than checking `offset + structure size`; short copies also leave the remainder of the unmanaged structure uninitialized. These are robustness and resource-exhaustion concerns; no working malicious PE or memory-disclosure proof was developed.

### Medium: predictable overwrite output and LuaTools replacement can promote stale/partial bytes

Unpackers write next to the input with `FileMode.Create`, for example `Steamless.Unpacker.Variant31.x86/Main.cs:454-456`, truncating an existing `<input>.unpacked.exe`. Optional dump flags write `<input>.payload` and directory-local `SteamDRMP.dll` (`Main.cs:275-277,318-321`). Dumps are off by default (`Steamless.API/Model/SteamlessOptions.cs:35-42`), and the LuaTools invocation supplies only the input path.

The caller `src/LuaToolsGui/Services/SteamlessService.cs:81-87,133-141` waits for exit but does not check the exit code or validate output freshness, PE structure, completed write, or expected identity before replacing the original whenever that path exists. A preexisting stale output or partial failed write may therefore be promoted. It backs up once, deletes the original, then moves output; a move failure can leave the original absent. These are integration findings, separate from upstream trust. Concurrent tampering or filesystem links in the game directory are not addressed by the dependency-bundle receipt. Prefer a fresh isolated output directory, successful process exit, output validation, and a recoverable atomic replacement.

### High assurance gap: bundled opaque build inputs and absent source-to-release proof

Variant 2.0 and 2.1 projects reference local `SharpDisasm.dll` (`Steamless.Unpacker.Variant20.x86/Steamless.Unpacker.Variant20.x86.csproj:28-30`; Variant21 project lines 26-28). Its source is not included in this repository. The GUI references embedded MVVM Light, ServiceLocation, Json.NET and interactivity DLLs (`Steamless/Steamless.csproj:32-56`). Therefore even a clean rebuild of this tree would still consume third-party binary inputs that this review has not independently audited.

`Steamless.CLI/Steamless.CLI.csproj:11-14,26-30,56-65` targets .NET Framework 4.5.2, marks deterministic compilation, references the local API project and imports the host MSBuild targets. A deterministic flag alone does not establish reproducibility or release provenance. Only `.github/FUNDING.yml` is tracked under `.github`; no tracked GitHub Actions build/release workflow was found. No signed attestation, independently reproduced release, complete toolchain pin, or dependency-source reconstruction was established. This is an approval-blocking evidence gap, not an allegation that publisher binaries differ from source.

## Behavior and network observations

The inspected unpackers parse and decrypt input bytes, rebuild PE sections and write a separate executable. No direct call that executes the target game, decrypted payload or SteamDRMP DLL was found in the checked C# sources. Header fields named `LoadLibrary*` are parsed data, not themselves calls to those APIs. Explicit native imports found in the source are Windows `imagehlp.dll` checksum calls (`Steamless.API/PE32/NativeApi32.cs:616-617` and PE64 lines 612-613), used by the checksum helpers; unmanaged marshalling and SharpDisasm remain additional native-memory surfaces.

No CLI networking, telemetry, credential collection, persistence, downloader or automatic updater implementation was identified by source inspection/search. GUI `Steamless/ViewModel/MainWindowViewModel.cs:79-84` starts configured hyperlink URIs through `Process.Start`, allowing user-clicked external website navigation. This limited negative finding excludes behavior hidden inside the bundled DLLs and does not prove that a downloaded release has the same behavior. LuaTools performs the latest-release acquisition itself and runs the CLI at `SteamlessService.cs:51-52,133-141`.

## Release linkage and hashes

The read-only GitHub latest-release metadata queried on the audit date reported tag `v3.1.0.5`, target `master`, published `2024-03-30T05:21:19Z`, with one ZIP asset:

- `Steamless.v3.1.0.5.-.by.atom0s.zip`, declared size 610646 bytes.
- [Publisher asset URL](https://github.com/atom0s/Steamless/releases/download/v3.1.0.5/Steamless.v3.1.0.5.-.by.atom0s.zip).
- API `digest` field: null. This source review did not download, unpack, hash, decompile or validate that release asset. No archive or release-file hash is approved.

The tag resolving to the reviewed commit identifies the source associated by the publisher; it does not prove how the ZIP was built. LuaTools selects the first ZIP from mutable latest-release metadata, so a later release can fall outside this review even if this commit were eventually approved.

SHA-256 of binary inputs committed in the reviewed source tree (inventory identity only; **not approvals**):

| Source-tree file | SHA-256 |
| --- | --- |
| `Steamless.Unpacker.Variant20.x86/SharpDisasm.dll` | `6D3CE990CDF58DA228697D25416D16D15994135C5F66571FE1E00E9C975BC2CF` |
| `Steamless.Unpacker.Variant21.x86/SharpDisasm.dll` | `6D3CE990CDF58DA228697D25416D16D15994135C5F66571FE1E00E9C975BC2CF` |
| `Steamless/Embedded/GalaSoft.MvvmLight.Extras.dll` | `449257DB5F00CC244404E20534AF1E4029B5F5D65480F8167A12F6E1E41BF733` |
| `Steamless/Embedded/GalaSoft.MvvmLight.dll` | `2911EF26AACBA61E8730C8622BE320930751DBA7B17E3606FAD1FACFA8BE134F` |
| `Steamless/Embedded/Microsoft.Practices.ServiceLocation.dll` | `34005D6A80434542780C6D192E6ABD07BEA49B2EEB7E43FBFDFE90C2889986E5` |
| `Steamless/Embedded/Newtonsoft.Json.dll` | `0516D4109263C126C779E4E8F5879349663FA0A5B23D6D44167403E14066E6F9` |
| `Steamless/Embedded/System.Windows.Interactivity.dll` | `4AACE8C8A330AE8429CD8CC1B6804076D3A9FFD633470F91FD36BDD25BB57876` |

## Recommendation and remaining coverage

Keep the compiled independent allowlist empty for Steamless. Preserve default-off, explicit-action consent as documented; this report supplies no reviewed archive/per-file approval. Before an approval decision, review/reconstruct the bundled dependency sources and exact binaries, remedy or reproduce parser bounds findings in an isolated harness, audit every distributed plugin, pin a controlled toolchain, and independently reproduce or establish verifiable provenance for the exact distribution. Record archive and complete runtime-file hashes only after that work.

No dynamic malware analysis, fuzzing, exploit reproduction, binary reverse engineering, release-archive inventory, Authenticode verification, or reproducible build was performed. Source search cannot establish the absence of malicious behavior in bundled binary inputs, platform components, future plugins or future releases. This report covers the pinned source and the observed local invocation, not a blanket safety certification.
