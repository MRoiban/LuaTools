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
    public bool Apply(long appId, LuaVariant variant) => vault.Apply(appId, variant);
    public bool Delete(long appId, string hash) => vault.Delete(appId, hash);
    public void Rename(long appId, string hash, string? name) => vault.Rename(appId, hash, name);
}
