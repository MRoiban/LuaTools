namespace LuaToolsGui.Services;

/// <summary>
/// Compares a lua's <c>setManifestid(depot, "manifest")</c> pins with what Steam ships on the public
/// branch (<see cref="ContentDepot.PublicManifestId"/>). The Builds page's "pinned to an older build"
/// label and the Cabinet lua cache's freshness check share this one rule.
/// </summary>
public static class ManifestPins
{
    /// <summary>
    /// True when <paramref name="pin"/> names a manifest other than the one Steam currently ships. A
    /// missing pin or an unknown public manifest is not "behind": there is nothing to compare.
    /// </summary>
    public static bool IsBehind(string? pin, string? publicManifestId) =>
        pin is not null && publicManifestId is not null && pin != publicManifestId;

    /// <summary>
    /// True when every pinned depot that Steam reports a public manifest for is pinned to exactly that
    /// manifest. Depots Steam reports no public manifest for (shared redistributables, token-gated
    /// depots) cannot be compared and do not count against it. Without depot info at all, nothing can
    /// be shown to be current, so the answer is false.
    /// </summary>
    public static bool AreCurrent(IReadOnlyDictionary<long, string> pins, AppDepotInfo? info)
    {
        if (info is null) return false;
        foreach (var (depot, manifest) in pins)
        {
            string? shipped = info.Depots.FirstOrDefault(d => d.Id == depot)?.PublicManifestId;
            if (IsBehind(manifest, shipped)) return false;
        }
        return true;
    }
}
