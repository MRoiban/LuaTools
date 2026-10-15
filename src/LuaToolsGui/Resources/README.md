# Translations

This fork ships English and French only. Help improve the French translation!

## How to contribute a translation

Edit `Strings.fr.resx` in this folder and open a pull request. Translate the `<value>` text of each
`<data>` entry, and leave everything else alone.

- Don't add or remove keys. Every language file must carry the exact same key set as the English
  source.

CI validates every PR that touches these files: `scripts/check-i18n.py` rejects any file that drops
a key, breaks the XML, or changes a placeholder.

## How it works

- **`Strings.resx`** is the English source of truth, the canonical list of keys.
- **`Strings.fr.resx`** is the complete French translation. No other locale files are allowed.
- The app uses the chosen language, or French for a French OS and English otherwise.
  Saved settings naming a removed language use automatic OS detection.

Every English key must have a French translation, including keys for evolving features.

## Rules to follow

1. **Keep placeholders exactly.** `{0}`, `{1}` are filled in at runtime (a name, a count, a path).
   Keep the same `{0}`/`{1}` in your translation; you may reorder them. Dropping one crashes that screen.
   - `"Page {0} of {1}"` → French `"Page {0} sur {1}"` ✓
2. **Don't translate these.** Leave them as-is:
   - Product names: **SteamTools**, **OpenSteamTools**, **CloudRedirect**, **Steam**, **Discord**, **Hubcap**.
   - Technical tokens: **App ID**, **DLC**, file paths like `steam\config\stplug-in`, extensions `.lua` / `.manifest` / `.zip`.
   - The picker filter string `Lua / manifest / zip|*.lua;...`: only translate the human labels
     ("All files"), never the `|` parts or `*.ext` patterns.
   - Stored filter/sort dropdown values. They're English keys the code compares against in
     switch/equality, and what the user sees is localized separately by
     `FilterOptionDisplayConverter`. Translating the stored value breaks the filter.
3. **Keep the brand puns playful.** `"Let's get Luing™!"` is a pun on **LuaTools** ("Lua" as a verb).
   Don't translate it literally; render the same upbeat "let's go add some Luas" spirit in your language,
   and keep the ™. ("Lua" stays as-is; it's the brand.)
4. **Match the tone.** Short labels stay short. The Mode descriptions are casual/jokey, so keep that voice.
5. **Escape XML:** `&` → `&amp;`, `<` → `&lt;`, `>` → `&gt;`. The parity check will fail on malformed
   XML, so run it before opening a PR (see "Checking your work" below).

## Checking your work

From the repo root, `python scripts/check-i18n.py` confirms all files have the full key set, valid XML,
and consistent placeholders. CI runs this automatically on every PR.
