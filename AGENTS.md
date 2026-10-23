# LuaTools (MRoiban fork) — agent context

Windows WPF app (net8.0-windows) that installs Lua-based Steam unlock content, fixes and add-ons.
Default branch **`security-hardened`**. A push to it runs `.github/workflows/build.yml`, whose
`release` job publishes a signed fork release, so never push to it; work on feature branches.

## Commands
- `scripts/verify` — restore (locked), Release build, i18n check, plugin JS tests. Ends `VERIFY PASS|FAIL`.
- `scripts/verify --ci` — same, then force-pushes the current feature branch to `origin` and runs
  `build.yml` on a Windows runner (`dotnet test` runs there). Use it before reporting done; ~5 min.
- Raw: `dotnet restore LuaToolsGui.sln --locked-mode`, `dotnet build LuaToolsGui.sln -c Release`,
  `dotnet test LuaToolsGui.sln -c Release`, `node --test src/LuaToolsGui/Plugin/steam-plugin.test.cjs`,
  `python3 scripts/check-i18n.py`.

## Environment gotchas (Linux dev box)
- dotnet lives in `~/.dotnet` (SDK 8.0.426). Builds need `-p:EnableWindowsTargeting=true` (verify sets it).
- IPv6 to api.nuget.org hangs: export `DOTNET_SYSTEM_NET_DISABLEIPV6=1` (verify sets it).
- `DOTNET_SYSTEM_NET_DISABLEIPV6=1` is needed at RUN time too (any net8 harness hitting the network): hosts
  with AAAA records (e.g. uploads.online-fix.me) otherwise hang until the response budget and look "Unavailable".
- To exercise WPF-free service code for real on Linux, build a throwaway net8.0 console in /tmp that
  `<Compile Include=...>`s the needed src files (stub the few WPF-bound types); see design/onlinefix-provider.
- Tests can't run on Linux (no Microsoft.WindowsDesktop.App); they only compile. No Wine here.
- Adding a NuGet package changes `packages.lock.json` (restore with `--use-lock-file`, then commit
  the lock) and needs an entry in `DEPENDENCY_TRUST.md`; prefer the BCL.

## Map
| Path | What |
|---|---|
| `src/LuaToolsGui/AppConfig.cs` | Every compiled-in endpoint/repo (e.g. `CloudRedirectRepo`) |
| `src/LuaToolsGui/Services/OutboundNetworkPolicy.cs`, `ENDPOINTS.md` | Allowed HTTPS destinations; new hosts go in both |
| `src/LuaToolsGui/Services/UnlockerService.cs` | Unlocker modes; CloudRedirect add-on (`cloud_redirect.dll`) ~l.543–708 |
| `src/LuaToolsGui/Services/Cabinet/` | Cabinet port; `Engine/Cloud.cs` = Cabinet cloud client (connect link, DPAPI settings) |
| `src/LuaToolsGui/ViewModels/SettingsViewModel.cs`, `Views/SettingsView.xaml` | Settings incl. Cabinet connect box |
| `src/LuaToolsGui/ViewModels/FixesViewModel.cs`, `Services/ExecutableFixPolicy.cs`, `AppliedFixIndexService.cs` | Fixes tab, fix providers, applied-fix index |
| `src/LuaToolsGui/Services/DependencyQuarantine.cs`, `UserAuthorizedDependencies` | Consent + quarantine for downloaded executables |
| `src/LuaToolsGui/Resources/Strings*.resx`, `Strings.Designer.cs` | EN/FR strings (hand-maintained designer) |
| `tests/LuaToolsGui.Tests/` | xUnit |

## Rules (from CONTRIBUTING.md / SECURITY.md)
- Every user-visible string localized in `Strings.resx` + `Strings.fr.resx` + accessor in `Strings.Designer.cs`.
- Network only via the approved HTTPS policy; executable workflows are opt-in (`IsExecutableWorkflowEnabled`)
  and downloaded binaries go through `DependencyQuarantine` digest checks.
- Never print or log tokens. Don't touch `.github/workflows/`, signing scripts or `native/` unless the brief says so.
