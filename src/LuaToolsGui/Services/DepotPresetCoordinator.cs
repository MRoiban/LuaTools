namespace LuaToolsGui.Services;

/// <summary>Vault operations shared by the Depots GUI and the agent owner. No read reconciles.</summary>
public sealed class DepotPresetCoordinator(LuaVault vault)
{
    public void Reconcile(long appId)
    {
        vault.SyncDefaultFromLive(appId);
        vault.AdoptLooseBuildLuas(appId);
        vault.ApplyBuildIfNothingLive(appId);
    }
    public (bool Ok, string? Hash) Edit(long appId, LuaVariant? target, bool live, Func<string, string> edit)
    {
        string before = live || target is null ? vault.ReadLiveText(appId) : vault.ReadText(appId, target.Hash);
        if (string.IsNullOrEmpty(before)) return (false, null);
        string after = edit(before);
        if (after == before) return (true, target?.Hash);
        if (live || target is null)
        {
            RememberEditBase(appId);
            bool ok = vault.WriteLive(appId, after);
            if (ok) vault.SyncDefaultFromLive(appId);
            return (ok, null);
        }
        var updated = vault.UpdateVariant(appId, target.Hash, after);
        return (updated is not null, updated?.Hash);
    }
    public void RememberEditBase(long appId)
    {
        if (vault.GetActiveVariant(appId) is { Kind: not LuaVariantKind.Default } current)
            vault.SetEditBase(appId, current.Hash);
    }
    // Saving live edits routes back to their origin; UpdateVariant and Default sync retire bytes.
    public LuaVariant? SaveLive(long appId, string? expectedOrigin = null)
    {
        string? origin = vault.GetEditBase(appId);
        if (expectedOrigin is not null && origin != expectedOrigin) throw new InvalidOperationException("EDIT_ORIGIN_CHANGED");
        if (origin is null) { vault.SyncDefaultFromLive(appId); return vault.GetActiveVariant(appId); }
        return vault.UpdateVariant(appId, origin, vault.ReadLiveText(appId)) ?? vault.SaveText(appId, vault.ReadLiveText(appId), null);
    }
    public LuaVariant? SaveEdit(long appId, string text)
    {
        // Validate before even recording edit provenance.
        ManifestContentPolicy.ValidateLua(System.Text.Encoding.UTF8.GetBytes(text), appId);
        RememberEditBase(appId);
        return vault.WriteLive(appId, text) ? SaveLive(appId) : null;
    }
    public LuaVariant? Create(long appId, string text, string? name = null) => vault.SaveText(appId, text, name);
    public bool Apply(long appId, LuaVariant variant) => vault.Apply(appId, variant);
    public bool Delete(long appId, string hash) => vault.Delete(appId, hash);
    public void Rename(long appId, string hash, string? name) => vault.Rename(appId, hash, name);
}
