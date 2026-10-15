# Cabinet in LuaTools — handoff

Design artifact: design/cabinet/mockup.html (revision 2). Open it in a browser; the top state switcher shows every state.
Design status: selected by orchestrator, not user-approved.
Source being ported: C:\Users\Alex\Documents\Playground\_ext\cabinet (github.com/MRoiban/cabinet @ 84eb8a5). Read only; never build or run it.
Shared contract: src/LuaToolsGui/Services/Cabinet/CabinetContracts.cs. Change only with orchestrator agreement.
Languages: the fork ships English and French only. Every visible string goes in Strings.resx + Strings.fr.resx + Strings.Designer.cs (see CONTRIBUTING.md).

## Visual requirements
- New nav item "Cabinet" between Fixes and Plugin (MainWindow.xaml NavigationViewItem, WPF-UI SymbolIcon e.g. Archive24).
- CabinetView follows ManageView: Grid Margin 8,16,16,8; title 22 Bold; subtitle 12 #6b7280; primary ui:Button "Sync now" with ArrowSync24, top right; it is disabled while syncing ("Syncing…") or when Cabinet is off.
- Status chips: rounded pill (CornerRadius 999), #08FFFFFF background, #12FFFFFF border, 12px text, 7px dot. Green #4ade80 = on; grey #6b7280 = off; amber #fbbf24 = warning. Chips: Tracking on/off; Steam running/not running; "Playing: <game>" (only while playing); Cabinet Cloud status; and an amber "Standalone Cabinet is running — paused" when StandaloneCabinetRunning.
- Games list: one card (SettingsCard style: #08FFFFFF, #12FFFFFF border, radius 8), header row 11px SemiBold #9ca3af (GAME / ACHIEVEMENTS / PLAYED / LAST SYNC). Columns *, 150, 70, 110. Rows 10/14 padding, #10FFFFFF top divider, hover #06FFFFFF. Cover 92x43, radius 4, from CoverCache (Steam header image); dark placeholder when missing. Name 13 SemiBold, ellipsis. Badges 11px pills: green "Playing now · live playtime"; amber "N min pending · added when Steam closes"; grey "Achievement list not downloaded yet". Achievements: "14 / 22" (total muted) + 4px progress bar in the app accent; "—" without a list. Hours "38.4 h" tabular. Last sync: relative/short date, "Never". Clicking or pressing Enter on a row opens steam://nav/games/details/<appid>. Sort: playing first, then most recently synced.
- Empty state: centered card "No OnlineFix games found yet" + hint naming C:\Users\Public\Documents\OnlineFix.
- Off state: centered card "Cabinet is turned off" + primary "Turn on" button (sets CabinetEnabled = true); chips hidden.
- Settings: new section "Cabinet" inserted after "Executable workflows", reusing SettingsCard/SectionHeader/RowDivider and the label + #6b7280 hint + ui:ToggleSwitch row pattern from the Startup section. Four rows, as in the mockup (master, Live playtime, Download missing achievement lists, Workshop links). The three sub-switches are disabled (opacity) while the master switch is off. Controller layouts and screenshot attribution were removed at the user's request (revision 2).
- Section "Cabinet Cloud" (connect link field + Connect, connected/Disconnect state, friends' real games switch): round 2, cloud agent. Do not build it in round 1.
- Startup section: add "Start with Windows" under Minimize to tray. Hint: "Starts in the tray so Cabinet can track games."

## Responsive and state requirements
Window widths ≥ 900 px: four columns. Below ~640 px of content width, the colhead hides, achievements wrap under the title and LAST SYNC hides (mockup media query). States: populated, syncing, empty, off, standalone-paused; settings master on/off.

## Integration targets
- Engine (round 1, engine agent): Services/Cabinet/* implementing ICabinetService plus IHostedService (class CabinetService). Program.cs hidden helper subcommands. Tests in tests/LuaToolsGui.Tests/Cabinet/*.
- UI (round 1, UI agent): Views/CabinetView.xaml(.cs), ViewModels/CabinetViewModel.cs, MainWindow.xaml nav item, App.xaml.cs DI registration (including CabinetService as singleton + ICabinetService + hosted service), SettingsService (implements ICabinetSettings; real StartWithWindows), SettingsViewModel/SettingsView, Strings*, tray/toast notifications via ToastService and the MainWindow NotifyIcon.
- Cloud (round 2): Cloud client, friend labels via the owned Steam plugin, the Cabinet Cloud settings card, ENDPOINTS.md.

## Simulations to replace
Every mockup value is fake: games, hours, badges, chips, cloud user. All of it comes from ICabinetService.Status. Covers come from CoverCache.

## Acceptance
- Visual: the native page matches the mockup's hierarchy, spacing, colors and states at about 1000 px and about 700 px window widths. Native WPF-UI controls replace HTML ones. Section headers use native casing.
- Integration: toggles persist and call ICabinetService.SettingsChanged(). Sync now runs a real sync. Notifications are localized. Everything defaults off except the three defaults marked true in the contract. No background work runs while CabinetEnabled is false.
- dotnet build -c Release and dotnet test -c Release pass. The i18n check passes.
