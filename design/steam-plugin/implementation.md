# Source-owned Steam plugin implementation

The parent selected mockup revision 2, SHA256
`6887a257a4d4838da46ee40c47bed176dffa88595bff51ee9a716abc85352fcd`.
This is implementation selection under the user's existing authorization, not a claim of user design approval.

## Implemented behavior

The bundled `Plugin/steam-plugin.js` and `.css` implement the purchase-area panel,
source selection, FastFetch progress, cancellation, installed-game management,
remove/restart confirmations, fixes/settings/update actions, Denuvo marker,
and the recently added games list. The primary action says **Add via LuaTools**.
The existing Discord link is retained. No explanatory snippets or CDN dependencies
are added. Shared labels use the app's existing locale resources; newly authored
plugin-specific phrases currently fall back to English.

The native proxy is built from `native/steam-plugin-loader`. Its 180 exports
forward multimedia calls to a copy of the machine's genuine Windows WinMM.
Only a `steam.exe` host starts the configured fork, using an explicit absolute
CreateProcess path without a shell. The system Windows DLL is untouched.

The installer uses embedded artifacts, not upstream LTSP releases. The existing
toggle and explicit Install/Update action remain. A successful receipt permits
runtime startup on later app launches; disable stops injection and re-enable
resumes an already installed plugin. Uninstall preserves unfamiliar changed
files. The configured launcher remains retained through signed-update cleanup.

## Integration record

| Integration | Preserved behavior | Controls / changes |
|---|---|---|
| Steam CEF `127.0.0.1:8080/json` and `/devtools/page/{id}` | Discover Steam store tabs and inject UI | Exact loopback endpoint; bounded discovery/CDP messages; isolated JS world; fresh frame origin and app-ID validation for each dispatch |
| Local app `127.0.0.1:6767/has`, `/add`, `/add-status`, `/add-source`, `/cancel`, `/remove` | Existing add and management workflows | Native-only ephemeral token; stable request IDs; numeric current-app scope; bounded arguments/results; errors/timeouts reject; canceled checks cannot later auto-enqueue |
| Local `/open/settings`, `/open/fix`, `/restart-steam`, `/check-updates`, `/loaded-apps`, `/icon` | Existing app actions | Allowlisted method/argument shapes; trusted UI events; confirmations for remove/restart; no arbitrary page-world capability queue |
| `/open-url` | SteamDB game links and upstream Discord invite | HTTPS, default port, no credentials/query/fragment, narrowly allowed paths; handed to external browser |
| Original catalog/auth/manifest services | Existing backend services continue to handle downloads | No replacement endpoints or server-side authorization claims; their existing controls and documented limitations remain |
| Upstream LTSP release/assets | Replaced with source-owned plugin | No plugin release or executable download on install/status; bundled resources follow the fork's signed release pipeline |

## Reproducible checks

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/build-plugin-loader.ps1 -Test
node --test src/LuaToolsGui/Plugin/steam-plugin.test.cjs
dotnet test LuaToolsGui.sln -c Release --no-restore
python scripts/plugin-preview.py
```

Native tests check paths, reparse rejection, exact startup arguments and all 180
export names/ordinals/forwarders against the source ABI and Windows targets.
The controlled JS tests execute the actual script with simulated bridge/DOM:
typed app IDs, synthetic-click rejection, picker/progress/cancel, stale polls,
remove/restart confirmations, navigation, failed-cancel recovery, and reopening
the fallback flow. .NET tests cover CDP correlation and bounds, method/argument
validation, origin/app scope, installer ownership, translations, and delayed
source-check cancellation/concurrent pick admission.

The actual frontend was visually inspected in the offline fixture at 1280 and
390 pixels, including source selection and progress. Fixture bridge responses
are simulated; these checks do not exercise Steam or remote downloads.

## Remaining limits

- Live Steam CEF and Big Picture controller behavior have not been verified.
  No installed Steam files were changed during development. An explicit plugin
  reinstall is needed to migrate the original installed wrapper.
- Plugin-specific translations are incomplete; shared labels are localized.
  The old inactive theme chooser and explanatory controller hints are omitted.
- A closed shadow root and isolated world prevent ordinary page scripts from
  calling the bridge directly. They do not prevent hostile page layout from
  obscuring or positioning controls, nor authenticate a local process owning
  Steam's CDP port. This is not a sandbox against local malware.
- Native configuration and the genuine DLL companion remain writable according
  to local filesystem permissions; the wrapper does not independently sign or
  authenticate them. Same-user path substitution is outside its guarantee.
- The app's default-off settings do not unload a DLL Steam has already loaded.
  DLL replacement/removal is explicit and requires Steam restart.

Zero regressions is not established until live Steam workflow and controller
testing is complete. See `feature-parity.md` for the original behavior inventory.

## Verified local build

Final checks: 735 .NET tests passed, 7 JavaScript behavior tests passed, native harness reported zero failures and verified 180 exports. Publish succeeded at artifacts/source-owned-plugin/LuaTools.exe. git diff --check passed. Build retains source version 1.4.0 (automatic release feed disabled for local source builds); CI assigns commit versions for published releases. Only existing unused-parameter warnings in DepotDownloaderService and SteamlessService remain.
