# LTSP v2.2 loader: static binary reverse engineering

This supplements [the plugin review](steam-plugin.md). The missing source is still missing, but the loader's principal startup behavior is now traced in its machine code. **No independent approval is granted.** Neither the DLL nor its launch command was executed during this inspection. No live Steam files were changed.

## Exact specimen and reproduction

- Source asset: https://github.com/madoiscool/LTSP/releases/download/v2.2/winmm.dll
- Locally computed SHA-256: `dc1594774d3003f7c82fbcdaac4cc9bbc81d7ee2ab82dd5f47c4f04cc3bd8236`.
- Size: 268,800 bytes. AMD64 PE DLL; image base `0x180000000`; entry RVA `0x1b24c`.
- 180 exports: 176 PE forwarders to `winmm_real.*`, and four native wrappers (`PlaySound`, `WOWAppExit`, `mciExecute`, `mmsystemGetVersion`).
- The PE certificate directory is empty: no embedded Authenticode signature. This does not assess external catalog signing.
- Strings identify Rust runtime source revision `31fca3adb283cc9dfd56b49cdee9a96eb9c96ffd` and `.loader-core\src\lib.rs`. These are compiler/debug clues, not recovered source or build provenance.

Run `python scripts/inspect-plugin-loader.py path/to/winmm.dll artifacts/loader-review` with `pefile` and `capstone` installed. It refuses a different binary hash and emits imports/exports, TLS callbacks, strings and linear disassembly. It never calls LoadLibrary or otherwise executes the input. The real DLL stays in ignored `artifacts/`; it is not redistributed in the repository. RVAs below are relative to this exact image, not file offsets.

## Reconstructed startup behavior

```text
PE entry / CRT initialization
  -> custom DllMain (RVA 0x1000)
     on DLL_PROCESS_ATTACH:
       spawn loader worker with module handle and "winmm" identity

worker body (around RVA 0x3d4c)
  initialize local state and loader log
  if current process basename equals "steam.exe", ASCII case-insensitive:
    read LOCALAPPDATA
    target = LOCALAPPDATA + "\LuaTools\current\LuaTools.exe"
    check target filesystem metadata / availability
    if absent or unavailable: log and return
    workingDirectory = LOCALAPPDATA + "\LuaTools\current"
    CreateProcessW(NULL,
      'cmd.exe /c start "" "' + target + '" --minimized --tray-locked',
      no inherited handles, CREATE_NO_WINDOW, workingDirectory)
  otherwise log that the host is not Steam and skip launch
```

Evidence: CRT dispatch calls RVA `0x1000` at `0x1b1a9`. `DllMain` checks reason `1` at `0x1012`, then invokes the spawn path at `0x1026`. Worker calls host check `0x23f0` at `0x3d5a` and launcher `0x1b40` at `0x3d63`. The host check calls the `GetModuleFileNameW` thunk at `0x243d`, requires basename length 9 at `0x2565`, and compares the case-folded characters of `steam.exe` at `0x2574–0x267e`. The command string is referenced at `0x1de4`; `CreateProcessW` is reached at `0x1ef8` through import thunk `0x1af26`. Its arguments set `lpApplicationName=NULL`, command buffer in RDX, flags `0x08000000`, and the constructed working directory.

The forwarding helper at `0x22b0` constructs a directory-qualified DLL path and calls `LoadLibraryW` through `0x1af2c` at `0x2368`. Native export wrappers resolve functions with `GetProcAddress` (`0x305f`) and call the returned pointer (`0x308a`). The 176 PE forwarders use the `winmm_real` module name; their resolution additionally relies on Windows loader search behavior. Logging references `\luatools_loader.log` at `0x2dbf`. The single TLS callback at `0xc340` takes detach paths and iterates thread-local callbacks; inspected behavior is consistent with Rust runtime cleanup, not a second observed downloader.

## Security findings

### High, conditional: starts the upstream installation outside fork update policy

The launcher targets the upstream `%LOCALAPPDATA%\LuaTools\current\LuaTools.exe`, not the fork's portable directory, isolated profile or signed-update tree. Between the observed existence/metadata check and process creation there is no signature/hash verification or consultation of the fork's consent/update settings. If that path contains upstream LuaTools or substituted code, Steam startup can start it with the current user's authority. Its subsequent behavior is outside the fork's protections. This is not evidence that the loader downloads or replaces that executable itself.

Read-only checks on this machine found that Steam's installed `winmm.dll` matches the specimen hash exactly. The upstream executable path did **not** exist at inspection time. Thus this is a confirmed installed capability with a currently absent target, not proof that upstream LuaTools was launched here.

### Medium, conditional: unnecessary shell and executable-search trust

The launcher passes a bare `cmd.exe` command to `CreateProcessW` with no explicit application path, then uses `start`. This delegates executable lookup and command parsing to Windows/the shell. The executable argument is quoted, but environment-derived path text is still passed through a command interpreter. Search-path or environment manipulation is a conditional risk; no working exploit, remote trigger or privilege escalation was demonstrated. A replacement should use direct process creation with an explicit verified application path and no command shell.

### Native forwarding still extends the trusted code set

The proxy loads/forwards into `winmm_real.dll` without an observed independent signer/hash check in the forwarding helper. The fork initially copies the system DLL, but later filesystem substitution is a separate boundary. Read-only comparison on this machine found the installed `winmm_real.dll` byte-identical to `%WINDIR%\System32\winmm.dll` at inspection time. This check does not constrain future mutations or all forwarded-module search behavior.

## What was not found, and what that means

The import table contains Kernel32, NTDLL and synchronization APIs, with no direct WinHTTP, WinINet or Winsock imports. Printable ASCII/UTF-16 string inspection found no embedded HTTP(S) destinations. The traced startup path is process launch, logging and proxy initialization; no credential collection, donation request or executable download was observed in it. `VirtualProtect`, debugger checks and dynamic symbol loading also occur in runtime code; their presence alone is not evidence of malicious injection or evasion.

These are limited negative observations. Dynamic symbol resolution, forwarded DLLs, runtime callbacks and launched programs extend execution beyond that path. Linear disassembly is not complete control-flow recovery, and Rust runtime code was not exhaustively reconstructed. No debugger/emulator execution, traffic capture, complete decompilation, independent source reconstruction, reproducible build or proof of absence of hidden behavior was performed. The frontend's page-to-native bridge findings from the earlier report are unchanged.

## Disposition

The loader is more understood than before, but remains unapproved. The upstream launch path is specifically incompatible with the intended fork-only executable/update boundary. A small source-owned replacement (or removal of the loader if the required workflow can operate without it) should forward the required Windows functions and start only the explicitly selected fork through a direct, verified path. That change and its Steam compatibility need separate implementation/testing; this reverse-engineering task did not replace or disable the installed DLL.
