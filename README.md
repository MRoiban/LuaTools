# LuaTools security-focused fork

Security-focused fork of [madoiscool/LuaTools](https://github.com/madoiscool/LuaTools), based on tag `v1.3.2`, commit `946125981a7addb6150e83886443cc8ddb68931b`. The original MIT license and copyright are retained in [LICENSE](LICENSE).

The WPF desktop interface is retained. This fork removes key donation, analytics and upstream self-updates, enforces scoped outbound HTTPS policies, validates downloaded data, and disables executable workflows by default. Eight independent Settings switches allow explicit subsequent installation or execution of unreviewed dependencies. Enabling a switch is user consent, not an independent audit or hash approval. [ENDPOINTS.md](ENDPOINTS.md) records the original integration contracts, preserved workflows, controlled tests, sanitized HTTP observations and remaining incompatibilities. See [SECURITY.md](SECURITY.md) for the trust boundaries.

## Build locally

Windows with the .NET 8 Desktop Runtime and a compatible .NET SDK is required.

```powershell
dotnet restore LuaToolsGui.sln --locked-mode
dotnet test LuaToolsGui.sln --configuration Release --no-restore
dotnet publish src/LuaToolsGui/LuaToolsGui.csproj --configuration Release --no-restore --output publish
```

Run `publish/LuaTools.exe`. The app uses a separate profile from upstream. Original HTTPS catalog, authentication, manifest, metadata and mirror integrations remain. Response-provided signed files and artwork use a separate credential-free public-HTTPS transfer policy. Executable game fixes, BetterSteamTools, OpenSteamTools, Steam plugin, DepotDownloaderMod, Steamless, SteamAutoCrack and CloudRedirect each default to off. Toggling saves the preference; use the existing Install or Run action afterward. No third-party binary is bundled or independently approved. The original HTTP source-availability lookup is permitted as a narrow, credential-free metadata exception; actual manifest downloads and independent Hubcap operations use HTTPS. Server availability and authorization remain outside the client's control.

The hosted fork is [MRoiban/LuaTools](https://github.com/MRoiban/LuaTools), on `security-hardened`; the original repository remains tracked as `upstream`. Release setup and update trust are documented in [RELEASES.md](RELEASES.md).

## Dependencies and updates

Application updates come only from this fork and require its pinned release-key signature. Downloading and staging are separate from the explicit restart that applies an update. Do not install upstream releases over this fork. NuGet dependency versions are captured in committed lockfiles; review lockfile changes before restoring updated dependencies. A lockfile provides reproducibility, not a source audit of each package.

BetterSteamTools, OpenSteamTools, DepotDownloaderMod and companion scripts are separate trust decisions. See [DEPENDENCY_TRUST.md](DEPENDENCY_TRUST.md). The independent allowlist remains empty; publisher hashes and local authorization receipts do not establish authenticity. Turning a switch off blocks app-initiated installs, tool launches and plugin injection. It does not terminate external programs or uninstall native DLLs: Steam can load previously installed DLLs on later launches even while the switch is off. Automatic runtime installation, upstream app updates, telemetry and key donation remain disabled. Targeted dependency findings and review limits are recorded in [audits/README.md](audits/README.md); no dependency received independent approval.
