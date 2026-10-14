# Source-owned Steam startup proxy (Windows x64)

Build from a Visual Studio C++ x64 Build Tools installation, located with `vswhere`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin-loader.ps1 -Test
```

The result is `artifacts/plugin-loader/winmm.dll`. Release packaging copies that
file to the application's `Plugin/winmm.dll`; `-OutputDirectory <directory>` can
select another staging directory. No downloaded loader or binary source input is
used. Build intermediates stay in the output directory. `/Brepro` removes build
timestamps; byte identity assumes the same MSVC/SDK, source and build options.

The checked-in `winmm.def` records all 180 export names and ordinals from the
audited LTSP v2.2 ABI. Every export forwards to the same named export in
`winmm_real.dll`. All four functions formerly implemented as native wrappers also
exist in native Windows x64 winmm and need no wrapper. The installer must copy
the genuine x64 `%SystemRoot%\System32\winmm.dll` to adjacent `winmm_real.dll`.
The system original is never modified. PE forwarders use Windows DLL resolution;
the companion DLL and Steam directory permissions remain part of the trust
boundary. The proxy does not authenticate the companion at runtime.

The installer writes adjacent `luatools-fork-loader.path`: UTF-16 little-endian,
optional BOM, containing **only** the full fork executable path, without a NUL,
newline, arguments or surrounding quotes. The loader requires a drive-absolute
path on a fixed local drive, an existing `.exe`, and no reparse points in any
component of the config or target path. It rejects UNC/device/relative paths,
alternate streams, dot components, trailing dots/spaces and control characters.
Missing, inaccessible or invalid config/target silently disables startup.

`DllMain` pins the already-loaded proxy for the process lifetime and creates a
worker without waiting. The worker runs after DLL initialization has released
the loader lock, checks the host basename is `steam.exe` (case insensitive), and
calls `CreateProcessW` with the explicit absolute application path, exact
`--minimized --tray-locked` arguments, no handle inheritance and the executable's
directory as its working directory. There is no shell, download, network,
updater, logger or upstream installation fallback. Single-instance behavior
belongs to the fork application. No `LoadLibrary` is called by proxy source.

The path config is installer-owned **configuration, not authentication**. A
same-user writer who can edit it can select another executable; basename checks
do not authenticate Steam. Checks and process creation are not atomic against
directory substitution by a same-user attacker. The executable handle denies
write/delete sharing through process creation, but this is not a signature,
hash, ACL, or secure launch policy. Use the application's normal update controls
and local filesystem permissions; this proxy is not a sandbox.

`-Test` compiles an isolated harness that checks path parsing, BOM handling,
missing targets, non-Steam host gating, and directly starts only a child copy of
the harness to verify the two startup arguments. It never loads the old DLL or
injects into/starts Steam. Static PE checks can compare `winmm.def` to the built
export directory; the local audited original is an optional extra comparison,
never a build dependency. Steam compatibility still requires a separately
authorized manual test of the final application release.
