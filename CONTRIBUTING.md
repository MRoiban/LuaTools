## Building from source / Developing

```powershell
dotnet build LuaToolsGui.sln
dotnet run --project src/LuaToolsGui/LuaToolsGui.csproj
```

For iterative development:

```powershell
dotnet watch --project src/LuaToolsGui/LuaToolsGui.csproj
```

### Test

```powershell
dotnet test
```

### Releases

Fork builds come from reviewed source, locked NuGet dependencies and passing tests.
See [SECURITY.md](SECURITY.md) for the build commands, trust boundary and manual release
review process. CI uploads review artifacts only; upstream binaries and Velopack
installers are not a source of fork releases.

### Layout

| Path | Contents |
|---|---|
| `src/LuaToolsGui/` | The application: `Views/` (XAML), `ViewModels/`, `Services/`, `Models/`, `Resources/` (localization) |
| `src/LuaToolsGui/AppConfig.cs` | All compiled-in endpoints, mirrors and public client values |
| `tests/LuaToolsGui.Tests/` | xUnit tests |
| `scripts/check-i18n.py` | Translation validator, run by CI on every RESX change |

## Contribution Guidelines

Three rules are non-negotiable.

### 1. Every user-facing string must be localized

The app ships 29 languages. **Never hardcode a visible string**, whether in XAML, a toast, a
`MessageBox`, or an empty-state label. Adding text means:

1. Add the key to the English source, `src/LuaToolsGui/Resources/Strings.resx`.
2. Add the accessor to the hand-maintained `Strings.Designer.cs`
   (`public static string Key => Get(nameof(Key));`).
3. Add the same key, with a real translation, to all `Strings.<tag>.resx` files. A key missing
   from any one of them is a bug.
4. Reference it. XAML: `Text="{x:Static res:Strings.Key}"`; C#: `Resources.Strings.Key` or
   `string.Format(Resources.Strings.Key, arg)`.

`.github/workflows/i18n-check.yml` runs `scripts/check-i18n.py` on every PR touching the RESX files.
Translation contributions are pull requests against those files. See
[`src/LuaToolsGui/Resources/README.md`](src/LuaToolsGui/Resources/README.md) for the translator
guide, including which terms to leave untranslated and why.

### 2. Keep the fork's network and dependency boundaries

Requests must use the approved HTTPS destination policy. System DNS is the default;
the original DNS settings allow explicit opt-in to bounded Cloudflare DNS-over-HTTPS. Do not add
unapproved mirrors, certificate bypasses, silent key sharing, analytics, automatic
upstream updates, executable downloads, or Steam injection startup hooks. GitHub data
requests use `GithubProxy`, which validates direct URLs and the original approved
capability-matched mirror origins.
See [SECURITY.md](SECURITY.md) before changing these boundaries.
