# Steam store plugin — feature parity

Reference: LTSP release v2.2 `public/luatools.js` (SHA-256 `abaa8481…2795`, 3,518 lines) and `public/themes/*`, read from the inert audit extraction in `%TEMP%\luatools-ltsp-audit-f802554171af4bdd96cab2f9e41863f5\release-v2.2`. Line numbers below refer to that file. It declares no license. Behaviour is re-specified here; **do not copy its code, strings or translation tables**.

Status: **Kept** = same behaviour, new UI. **Changed** = same capability, different interaction. **Dropped** = intentionally not carried over. **Dormant** = present in the reference but already a no-op against the current app backend.

## Store-page entry points

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| Header icon button in the Store menu bar (`._1wn1lBlAzl3HMRqS1llwie`, right of search) opens the "LuaTools · Menu" modal | 2610–2657 | Changed | Same slot. Opens a Steam `popup_block_new`-style dropdown instead of a full-screen modal. |
| "Add via LuaTools" button copied from `.apphub_OtherSiteInfo` / `.steamdb-buttons` classes, pinned last by a MutationObserver | 2669–2711, 2779–2955 | Changed | Purchase-style panel inserted as first child of `#game_area_purchase`. Header-row button is only a fallback when that container is missing. |
| Add button hidden when `HasLuaToolsForApp` reports `exists` | 2855–2874 | Changed | An installed game shows the compact "added via LuaTools" bar with Fixes / Remove / Restart Steam, not nothing. |
| "Restart Steam" button in the header row | 2713–2768 | Changed | Moved to the success state, the installed bar and the menu. Not added to the header row. |
| Big Picture: insert next to `#queueBtnFollow` | 2664–2667 | Kept (fallback) | Same fallback anchor when `#game_area_purchase` is missing. |
| Re-insert on SPA navigation (interval, popstate, pushState hooks, MutationObserver) | 3304–3395 | Kept | Required. CDP re-injects on a fresh realm; in-realm URL changes must re-render. |
| `window.__LuaToolsReady` and `__LuaToolsInjected` guards | 527–535, 3093–3117 | Kept | Must stay compatible with `CefInjectorService` liveness probe (`CefInjectorService.cs:204`). |

## Add flow (`startLuaToolsAdd`, `showTestPopup`)

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| `StartLuaToolsAdd {appid, name}`. Name from `.apphub_AppName` or `<title>` | 745–772 | Kept | Same call and name scrape. |
| Modal "Select Download Source" with spinner "Checking availability…" | 2100–2150 | Changed | Inline panel, state `checking`. |
| Poll `GetLuaToolsAddStatus` every 350 ms. Clear on finish, close or cancel | 884–995, 2206–2211 | Kept | Single owned timer per appid. Stop on unmount and navigation. |
| Source rows: Downloading / Needs key (amber) / Not found (red) / Available (green), plus `(stats)` | 808–881 | Kept, extended | DLC-style rows. Adds "Limit reached" when a key row is locked and has stats. Adds an "Add key" link (→ `OpenSettings`) on locked rows. "Not found" is muted grey, not red. |
| Click a row when FastFetch is off → `PickLuaToolsAddSource {appid, source: name}` | 860–878 | Kept | Explicit per-row Download button. The whole row is also clickable. |
| FastFetch on: title "Downloading…", status "Starting download…", rows not clickable | 980–985 | Kept | `checking` state, then `downloading`. No picker. |
| Progress bar (20 px gradient) plus "%" or "…" when indeterminate | 2151–2167, 963–978 | Changed | 6 px Steam download bar. Indeterminate gets an animated sweep. Bytes are shown when known. |
| Success: "Game Added!", `installStatus` text, Cancel → Close, store button removed | 911–941 | Changed | `added` state: "{Game} added", "Added via {source}", green Restart Steam. |
| Error: "Failed: {error}". Rows re-render clickable if FastFetch is off | 942–954 | Kept | `err-*` states. Error line shown above the still-pickable rows. |
| Single Cancel/Close button. Cancel → `CancelAddViaLuaTools` | 2172–2240 | Kept | Cancel in the action bar → `cancelled` state, then idle. |
| Duplicate-run guard (`runState.inProgress`) | 762, 3285 | Kept | Required. |

## Menu (`showSettingsPopup`)

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| Discord icon → `OpenExternalUrl https://discord.gg/luatools` | 1688, 1794–1808 | Kept, **owner decision** | Last menu item. This is an upstream community link from a source-owned fork; remove it if unwanted. |
| Settings icon → `OpenSettings` (app window) | 1694, 1817–1829 | Kept | "Settings…" |
| Close icon | 1700 | Changed | Esc, click outside, or toggle the icon. |
| "Remove via LuaTools" (only when `exists`) → confirm → `DeleteLuaToolsForApp` → alert "LuaTools removed for this app." | 1710–1729, 1867–1966 | Kept | "Remove {Game}…" in the menu and in the installed bar. Steam-style confirm modal. The panel returns to the `removed` state instead of an alert. |
| "Fixes Menu" card (game pages only) → `OpenFix {appid}`, alert if no appid | 1736–1742, 1831–1865 | Kept | "Fixes for {Game}…" in the menu and in the installed bar. Hidden on non-app pages. |
| "Check Updates" card → `CheckForUpdatesNow` → alert(message ‖ "No updates available.") | 1744–1792 | Changed | Modal "Checking for updates". The backend returns only `{success}` (`HttpServerService.cs:529`), so the old "No updates available." text was always misleading. |
| Big Picture mouse-mode tip box in the menu | 1635–1647 | Dropped | Explanatory snippet. |

## Popups and alerts

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| Restart confirm "Restart Steam now?" → `RestartSteam` | 2080–2096 | Kept | Steam modal "Restart Steam?" with Cancel and Restart. |
| "LuaTools · Added Games": once per session on store host (`sessionStorage` gate), `ReadLoadedApps`. Left click → `steam://install/{id}`. Right click → SteamDB. Dismiss → `DismissLoadedApps` | 3226–3248, 3396–3505 | Changed | Modal "Added since last restart". Explicit Install button and SteamDB link per row, so there is no hidden right-click. Adds a Restart Steam button. Also reachable from the menu. |
| `GetInitApisMessage` startup alert or restart-confirm on "update/restart" text | 3198–3225 | Dormant | Shim always returns empty (`667`). Not designed. Revisit if the app adds a startup-message endpoint. |
| Generic alert / confirm modals (blurred black overlay, 16 px radius, Font Awesome icons) | 2325–2502 | Changed | Steam `newmodal` look. No blur, no icons, square corners. |
| Gamepad hint toast in Big Picture | 3140–3196 | Dropped | Explanatory snippet. |

## Status pills on the store button

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| Playable / Unplayable / Fix Available pills from `GetGamesDatabase` / `CheckForFixes` | 2957–3069 | Dormant | Both are stubbed to empty (`660`, `663`), so these pills never render today. A neutral `.lt-tag` slot exists in the panel subline if real data is added. |
| "Denuvo" pill when `.DRM_notice` contains "Denuvo" and there is no fix | 3002–3005, 3060–3065 | Kept | Amber `.lt-tag.warn` in the panel subline and the installed bar. This is live today because fixes are always empty. |

## Cross-cutting

| Reference behaviour | Ref. lines | Status | Replacement |
|---|---|---|---|
| 11 colour themes (`themes.json`: original, dark, light, purple, space, ocean, forest, rosepine, catppuccin, dracula, christmas), with per-theme `select` CSS | 1087–1497, `themes/*` | Dropped | Theme key comes from `GetSettingsConfig.values.general.theme`, which the shim fixes to `{}`, so "original" was the only reachable theme. Only Steam-native tokens are used. |
| Font Awesome 6.5.1 from cdnjs (SRI) | 1522–1538 | Dropped | Inline SVG. Removes the third-party network dependency (audit Low finding). |
| Embedded translations for 30 locales (incl. joke locales), `document.documentElement.lang` resolution | 537–594, 1970–2076 | Changed | Keep localisation keyed off Steam's page language. **Author new strings** (app `Resources.Strings` or a new resource). The reference tables are unlicensed. English copy in the mockup is the source text. |
| Gamepad navigation system: focus scan, D-pad and stick, blocks Steam input while an overlay is open | 6–519 | Changed | The panel is inline and uses native focus order (`Focusable` class, `:focus-visible` ring). Only modals and the menu need focus trap, Esc and B-to-close. A custom gamepad poller is not required. Verify in Big Picture. |
| `backendLog` → `Logger.log` | 727–743 | Dormant | The local shim (`611–684`) answers unknown `Logger.log` with `{success:true}`, so logs never reach the bridge. The injector also maps `Logger` → `/log`, but `HttpServerService` has no `/log` route. Use `console` only, unless a log route is added deliberately. |
| `GetIconDataUrl` for the header icon | 2625–2645 | Kept | Same, with the inline-SVG fallback. |
| Disclaimer, API-key, donate-keys, installed-fixes and installed-Lua settings strings | translations only | Dormant | No reachable UI in v2.2. Settings live in the app. |
| `CheckApisForApp`, `StartAddViaLuaToolsFromUrl`, `GetAddViaLuaToolsStatus`, `GetApiList` (legacy download path) | bridge map only | Dormant | Not called by v2.2 UI. Keep the server routes for compatibility; the new UI does not use them. |

## Deviations needing owner sign-off

1. Panel in the purchase area instead of Add/Restart buttons in the Community Hub row.
2. Installed games show a manage bar instead of no button.
3. Themes are dropped.
4. Discord link is retained for now.
5. "Add to Library" wording on the primary button. It is native-sounding, but it matches Steam's free-game button. The alternative is "Add via LuaTools".
