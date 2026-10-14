# Managed package dependency audit

Review date: 2026-10-15. Reviewer: Codex. **Result: no known advisory was reported for either locked graph; this is a targeted source/integration and provenance review, not independent approval of the complete managed dependency graph.** No application files or allowlists were changed.

## Scope, provenance and checks

The application targets `net8.0-windows` and has seven direct PackageReferences, resolving to 40 packages for `net8.0-windows7.0`. The test project adds three direct references and fourteen additional unique packages, for 54 unique package IDs/versions across the two lock graphs. Runtime/platform framework assemblies and the .NET SDK are separate from this NuGet package inventory.

Read all PackageReference declarations and both `packages.lock.json` files. Read installed `.nuspec` source metadata and `.nupkg.metadata` from `C:\Users\Alex\.nuget\packages`. All 40 application cache metadata content hashes matched the lock. For **all 54 packages**, `dotnet nuget verify <cached.nupkg> --all` exited successfully, and its reported NuGet content hash matched the corresponding lock value. These are package identity/signature checks, not source-to-binary equivalence or a guarantee of benign behavior. A raw ZIP SHA-512 is not the NuGet signed-package content hash; it was not substituted for the verified NuGet content hash.

`dotnet list src/LuaToolsGui/LuaToolsGui.csproj package --vulnerable --include-transitive --no-restore` and the equivalent test-project command both completed successfully using `https://api.nuget.org/v3/index.json` and reported no vulnerable packages. This point-in-time advisory result cannot identify unpublished vulnerabilities or malicious package behavior.

Pinned source checkouts outside the workspace were inspected without building or running fetched code:

| Package | Installed version | Declared and checked-out source commit |
| --- | --- | --- |
| Markdig.Wpf | 0.5.0.1 | [f172c1337eacd80f5fd138b1eb2547e62690b243](https://github.com/Kryptos-FR/markdig.wpf/tree/f172c1337eacd80f5fd138b1eb2547e62690b243) |
| WPF-UI / WPF-UI.Abstractions | 4.3.0 | [153baadeaf475875a81a4605881ca5c2dbe53972](https://github.com/lepoco/wpfui/tree/153baadeaf475875a81a4605881ca5c2dbe53972) |
| VirtualizingWrapPanel | 2.5.1 | [306092e1a9f90ff70b8f2b48be615dc0a75f9a82](https://github.com/sbaeumlisberger/VirtualizingWrapPanel/tree/306092e1a9f90ff70b8f2b48be615dc0a75f9a82) |
| Velopack | 1.2.0 | [f2edcbcafb81da5b3c884aaea330e225ad91d8b6](https://github.com/velopack/velopack/tree/f2edcbcafb81da5b3c884aaea330e225ad91d8b6) |

The installed Markdig.Wpf `net5.0-windows7.0/Markdig.Wpf.dll` LinkInlineRenderer was also decompiled read-only using the pre-existing `ilspycmd` tool; the URL/image/hyperlink behavior matched the inspected source. This spot check is not a complete decompilation or reproducible-build comparison.

## Security findings and integration observations

### MP-01 — Informational, mitigated in current integration: Markdig.Wpf's default image renderer bypasses application HTTP controls

The package's [LinkInlineRenderer.cs lines 23–58](https://github.com/Kryptos-FR/markdig.wpf/blob/f172c1337eacd80f5fd138b1eb2547e62690b243/src/Markdig.Wpf/Renderers/Wpf/Inlines/LinkInlineRenderer.cs#L23-L58) accepts relative/absolute URIs and constructs a WPF `BitmapImage` directly from image URLs. Its hyperlinks also retain the supplied URL as command parameter and NavigateUri. This would be an unsafe default for untrusted remote Markdown if callers assumed their HTTP destination/credential controls covered it.

The application currently replaces this renderer with `SafeMarkdown.SafeLinkRenderer`, routes image loading through `SafeImageLoader`, binds the custom pipeline explicitly in `FixesView.xaml`, suppresses image-click and RequestNavigate actions, and permits only absolute HTTPS browser links with no userinfo. Thus this package default is **not an observed unmitigated bypass in the reviewed application integration**. SafeImageLoader applies byte, image dimension, timeout and concurrency limits and central HTTP policy. It also intentionally accepts local fixed/removable/RAM-drive image paths, rejecting UNC/reparse paths; if remote Markdown should never display a local image, separate that policy from local cover-image loading. No image-byte exfiltration was demonstrated.

Local evidence: `src/LuaToolsGui/Services/SafeMarkdown.cs`, `Services/SafeImageLoader.cs`, `Views/FixesView.xaml:486`, and `Views/FixesView.xaml.cs:23`. The package [MarkdownViewer.cs](https://github.com/Kryptos-FR/markdig.wpf/blob/f172c1337eacd80f5fd138b1eb2547e62690b243/src/Markdig.Wpf/MarkdownViewer.cs#L87-L90) calls ToFlowDocument with the selected pipeline; this path builds WPF objects, rather than loading attacker-provided XAML text. The separate XAML sample application's `XamlReader.Load` is not the application's renderer path.

### MP-02 — Informational, latent capability: WPF-UI HyperlinkButton shell-opens arbitrary absolute URI schemes

[HyperlinkButton.cs lines 33–50](https://github.com/lepoco/wpfui/blob/153baadeaf475875a81a4605881ca5c2dbe53972/src/Wpf.Ui/Controls/HyperlinkButton/HyperlinkButton.cs#L33-L50) calls `Process.Start` with UseShellExecute and a URI without a scheme allowlist. The application uses WPF-UI for FluentWindow, buttons, themes and SnackbarService; a search of application XAML found no HyperlinkButton, Anchor, WebView, or NavigateUri usage. This is a library capability to guard if later binding untrusted URLs, **not an active finding in current UI usage**. The inspected FluentWindow path uses Windows UI interop; the reviewed PInvoke declaration explicitly restricts USER32 lookup to System32. Gallery and FontMapper networking belongs to separate projects and was not attributed to the shipped UI assembly.

### MP-03 — Informational: VirtualizingWrapPanel's XamlReader use parses a constant template

Source search of `src/VirtualizingWrapPanel` identified [VirtualizingItemsControl.cs lines 15–32](https://github.com/sbaeumlisberger/VirtualizingWrapPanel/blob/306092e1a9f90ff70b8f2b48be615dc0a75f9a82/src/VirtualizingWrapPanel/VirtualizingItemsControl.cs#L15-L32) using `XamlReader.Parse`, but its input is a fixed source string, not downloaded data. The application's XAML uses VirtualizingWrapPanel as an items panel in ManageView and FixesView. The inspected production source search found no HTTP client, process launch, registry access, file I/O, or assembly-load primitive; this does not establish that every possible WPF interaction is harmless. Layout/scroll/recycling algorithms were not exhaustively reviewed or tested for correctness or denial of service.

### MP-04 — Informational: currently used Velopack runtime probe performs local directory inspection

The package contains powerful updater/process/installer APIs, but their presence is not evidence they are called. Application reference search found the operative usage in `SteamAutoCrackService.RuntimeInstalledAsync`: `Runtimes.GetRuntimeByName` followed by `CheckIsInstalled`. The pinned [RuntimeInfo.cs lines 205–222](https://github.com/velopack/velopack/blob/f2edcbcafb81da5b3c884aaea330e225ad91d8b6/src/lib-csharp/Windows/RuntimeInfo.cs#L205-L222) implementation for DotnetInfo enumerates local runtime-version directories and compares versions. This probe does not download or install a runtime. Current `EnsureRuntimeAsync` throws when the runtime is absent. Search found no application `VelopackApp` or `UpdateManager` call. `ProxiedFileDownloader` implements a Velopack interface but no construction/call site was found.

Do not infer an enabled Velopack updater or automatic runtime installer from old comments or unused helper types. The application's separately implemented signed updater is outside this package audit; no conclusion about that updater's correctness is made here. Full Velopack native updater, installer, packaging tool and bootstrapper audit remains out of scope because this review traced the application's currently used managed probe only.

### MP-05 — Informational: foundational Microsoft and CommunityToolkit packages have build-time and sensitive-state roles

`CommunityToolkit.Mvvm` provides observable properties/commands and ships Roslyn source-generator/analyzer assemblies plus imported build targets. These are build-time code execution inputs and belong in build provenance, even though not all analyzer assemblies ship in the final app. Installed package metadata declares source `CommunityToolkit/dotnet@35dfe7b12da0e64384bfaec7ebc0883196a89f9f`; generator implementations were not fully source-audited.

`Microsoft.Extensions.Hosting` is actively used through Host.CreateDefaultBuilder and dependency injection. The transitive configuration, logging, diagnostics, options and file-provider assemblies therefore are not merely hypothetical dependencies. Default hosting behavior can read local configuration/environment; there was no basis in this targeted review to label these normal facilities credential theft or remote executable updating.

`System.Security.Cryptography.ProtectedData` is used by AuthService and SettingsService with CurrentUser DPAPI for stored authentication/API-key material. This protects bytes at rest under the Windows account; it is not protection against arbitrary same-user code. `System.Text.Json` parses untrusted data throughout the application. All these installed 10.0.9 package nuspecs declare the dotnet/dotnet source revision listed below; their full source and native OS cryptography/JSON parser implementation were not audited in this pass. No specific exploitable defect was established.

## Approval recommendation and remaining gaps

No automatic dependency trust approvals or executable allowlist entries result from this report. Preserve locked restore and NuGet signature/content identity checks, retain the custom Markdown image/link controls, and rerun advisory checks when versions change. A release approval would additionally need a trusted SDK/build environment, review of imported targets/analyzers, independent source-to-binary provenance or reproducibility, and review of the entire transitive implementation and native/platform components. Current package metadata source-commit claims plus NuGet signatures establish substantially more provenance than an arbitrary downloaded DLL, but do not complete those steps.

Remaining gaps include full Markdig parser/resource-complexity review, all WPF UI/native interop paths, image codec vulnerabilities, full Microsoft Extensions/System.Text.Json/DPAPI sources, CommunityToolkit generator code, test-host/coverage/analyzer code and build scripts, source-to-package equivalence, and runtime framework/SDK servicing. No fetched source or package code was run for behavioral testing; package inspection, signature verification, source checkout/search and decompilation were read-only. No live application credentials, registry ticket data, or Steam installation was inspected.

## Complete pinned package inventory

The tables below enumerate every unique installed package from both lock graphs. `App/direct` identifies all seven application PackageReferences; `Test/direct` identifies all three test PackageReferences. All other rows are locked transitives. The authoritative SHA-512 NuGet content hashes remain in the two lockfiles and were matched with `dotnet nuget verify`. Repository/commit entries are the installed nuspec's declarations, not independently proven build equivalence; a missing source commit is stated explicitly.

| Package | Version | Scope | Declared source repository and commit |
| --- | --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.11.1 | Test/direct | [https://github.com/microsoft/vstest@58dbd027217ac035a4e9114c9213b11dc0e988bd](https://github.com/microsoft/vstest/tree/58dbd027217ac035a4e9114c9213b11dc0e988bd) |
| xunit | 2.9.2 | Test/direct | [https://github.com/xunit/xunit@82543a6df6f5f13b5b70f8a9f9ccb41cd676084f](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f) |
| xunit.runner.visualstudio | 2.8.2 | Test/direct | [https://github.com/xunit/visualstudio.xunit@699d445a1aab46a014bce946edc4995b6b46accf](https://github.com/xunit/visualstudio.xunit/tree/699d445a1aab46a014bce946edc4995b6b46accf) |
| CommunityToolkit.Mvvm | 8.4.2 | App/direct | [https://github.com/CommunityToolkit/dotnet@35dfe7b12da0e64384bfaec7ebc0883196a89f9f](https://github.com/CommunityToolkit/dotnet/tree/35dfe7b12da0e64384bfaec7ebc0883196a89f9f) |
| Markdig | 0.22.0 | App/transitive | [https://github.com/lunet-io/markdig@751c79fce4e92857f861d8af0854a8eff0b29071](https://github.com/lunet-io/markdig/tree/751c79fce4e92857f861d8af0854a8eff0b29071) |
| Markdig.Wpf | 0.5.0.1 | App/direct | [https://github.com/Kryptos-FR/markdig.wpf.git@f172c1337eacd80f5fd138b1eb2547e62690b243](https://github.com/Kryptos-FR/markdig.wpf/tree/f172c1337eacd80f5fd138b1eb2547e62690b243) |
| Microsoft.CodeCoverage | 17.11.1 | Test/transitive | [https://github.com/microsoft/vstest@58dbd027217ac035a4e9114c9213b11dc0e988bd](https://github.com/microsoft/vstest/tree/58dbd027217ac035a4e9114c9213b11dc0e988bd) |
| Microsoft.Extensions.Configuration | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.Binder | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.CommandLine | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.EnvironmentVariables | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.FileExtensions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.Json | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Configuration.UserSecrets | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.DependencyInjection | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Diagnostics | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Diagnostics.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.FileProviders.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.FileProviders.Physical | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.FileSystemGlobbing | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Hosting | 10.0.9 | App/direct | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Hosting.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.Abstractions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.Configuration | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.Console | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.Debug | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.EventLog | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Logging.EventSource | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Options | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Options.ConfigurationExtensions | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.Extensions.Primitives | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Microsoft.TestPlatform.ObjectModel | 17.11.1 | Test/transitive | [https://github.com/microsoft/vstest@58dbd027217ac035a4e9114c9213b11dc0e988bd](https://github.com/microsoft/vstest/tree/58dbd027217ac035a4e9114c9213b11dc0e988bd) |
| Microsoft.TestPlatform.TestHost | 17.11.1 | Test/transitive | [https://github.com/microsoft/vstest@58dbd027217ac035a4e9114c9213b11dc0e988bd](https://github.com/microsoft/vstest/tree/58dbd027217ac035a4e9114c9213b11dc0e988bd) |
| Newtonsoft.Json | 13.0.1 | Test/transitive | [https://github.com/JamesNK/Newtonsoft.Json@ae9fe44e1323e91bcbd185ca1a14099fba7c021f](https://github.com/JamesNK/Newtonsoft.Json/tree/ae9fe44e1323e91bcbd185ca1a14099fba7c021f) |
| System.Diagnostics.DiagnosticSource | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| System.Diagnostics.EventLog | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| System.IO.Pipelines | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| System.Reflection.Metadata | 1.6.0 | Test/transitive | No repository commit declared in nuspec |
| System.Security.Cryptography.ProtectedData | 10.0.9 | App/direct | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| System.Text.Encodings.Web | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| System.Text.Json | 10.0.9 | App/transitive | [https://github.com/dotnet/dotnet@901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353) |
| Velopack | 1.2.0 | App/direct | [https://github.com/velopack/velopack@f2edcbcafb81da5b3c884aaea330e225ad91d8b6](https://github.com/velopack/velopack/tree/f2edcbcafb81da5b3c884aaea330e225ad91d8b6) |
| VirtualizingWrapPanel | 2.5.1 | App/direct | [https://github.com/sbaeumlisberger/VirtualizingWrapPanel@306092e1a9f90ff70b8f2b48be615dc0a75f9a82](https://github.com/sbaeumlisberger/VirtualizingWrapPanel/tree/306092e1a9f90ff70b8f2b48be615dc0a75f9a82) |
| WPF-UI | 4.3.0 | App/direct | [https://github.com/lepoco/wpfui@153baadeaf475875a81a4605881ca5c2dbe53972](https://github.com/lepoco/wpfui/tree/153baadeaf475875a81a4605881ca5c2dbe53972) |
| WPF-UI.Abstractions | 4.3.0 | App/transitive | [https://github.com/lepoco/wpfui@153baadeaf475875a81a4605881ca5c2dbe53972](https://github.com/lepoco/wpfui/tree/153baadeaf475875a81a4605881ca5c2dbe53972) |
| xunit.abstractions | 2.0.3 | Test/transitive | No repository commit declared in nuspec |
| xunit.analyzers | 1.16.0 | Test/transitive | [https://github.com/xunit/xunit.analyzers@c399ff14191b5f0ae95f215e2674c8419d9c4505](https://github.com/xunit/xunit.analyzers/tree/c399ff14191b5f0ae95f215e2674c8419d9c4505) |
| xunit.assert | 2.9.2 | Test/transitive | [https://github.com/xunit/xunit@82543a6df6f5f13b5b70f8a9f9ccb41cd676084f](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f) |
| xunit.core | 2.9.2 | Test/transitive | [https://github.com/xunit/xunit@82543a6df6f5f13b5b70f8a9f9ccb41cd676084f](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f) |
| xunit.extensibility.core | 2.9.2 | Test/transitive | [https://github.com/xunit/xunit@82543a6df6f5f13b5b70f8a9f9ccb41cd676084f](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f) |
| xunit.extensibility.execution | 2.9.2 | Test/transitive | [https://github.com/xunit/xunit@82543a6df6f5f13b5b70f8a9f9ccb41cd676084f](https://github.com/xunit/xunit/tree/82543a6df6f5f13b5b70f8a9f9ccb41cd676084f) |
