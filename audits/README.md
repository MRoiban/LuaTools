# Dependency review record

These are targeted static reviews by GPT-6.1-Sol agents at medium reasoning effort, with source revisions and evidence recorded in each report. They are not exhaustive audits, malware certifications, or proof that distributed binaries match the inspected source. Fetched executables, installers and build scripts were not run. No independent executable approvals were added.

| Dependency | Review | Principal remaining concern |
| --- | --- | --- |
| BetterSteamTools | [Report](bettersteamtools.md) | Default-on manifest-code donation and self-replacement, unrestricted Lua and insecure build acquisition |
| OpenSteamTools | [Report](opensteamtools.md) | Unrestricted Lua, mutable hook metadata and unverified build inputs/provenance |
| Steam plugin and loader | [Report](steam-plugin.md), [binary analysis](steam-plugin-loader-re.md) | Loader starts the upstream application path; missing source/provenance and page-to-native bridge authority |
| DepotDownloaderMod | [Report](depotdownloader.md) | Manifest filenames reach filesystem operations without sufficient containment; repack provenance |
| Steamless | [Report](steamless.md) | Plugin execution and unsafe malformed-PE/disassembler boundaries; opaque bundled code |
| SteamAutoCrack | [Report](steamautocrack.md) | Nested executable acquisition, opaque bundled binaries and destructive restore scope |
| CloudRedirect | [Report](cloudredirect.md) | Its own unverified self-updater and replaceable helper caches |
| Executable game fixes | [Report](executable-fixes.md) | Each remote archive needs separate source/provenance review |
| Managed packages | [Report](managed-packages.md) | Scoped package/attack-surface review; transitive graph is not fully audited |

All executable dependencies remain independently unapproved. Their existing default-off Settings switches permit explicitly requested use of unreviewed code; those switches do not turn these reports into approval. Once launched or loaded into Steam, dependencies run outside the desktop application's HTTP and update policies and can implement their own network, updater, filesystem and execution behavior.

The application's own fork release signature is a separate trust decision: it authenticates a build authorized by this fork's release key. It neither approves these dependencies nor proves the signed application's source is free of defects. See [RELEASES.md](../RELEASES.md).
