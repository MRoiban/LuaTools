using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.Services;

/// <summary>Shared GUI/agent operations. Presentation and cloud-save offers belong to GUI adapters.</summary>
public static class AccountCabinetOperations
{
    public static Task RedeemCodeAsync(AuthService auth, string code, CancellationToken ct = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 6 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException(Resources.Strings.Settings_BotCode_Invalid);
        return auth.SignInWithCodeAsync(code, ct);
    }
    public static async Task<Models.HubcapStats?> ValidateHubcapAsync(HubcapService hubcap, string input, CancellationToken ct = default)
    {
        string key = input.Trim();
        if (!HubcapService.IsValidKeyFormat(key)) return null;
        return await hubcap.GetStatsAsync(key, ct);
    }
    public static Task ConnectAsync(ICabinetService cabinet, ICabinetCloud cloud, string input, string? server, CancellationToken ct = default)
    {
        RequireCabinet(cabinet);
        return cloud.ConnectAsync(input.Trim(), server, ct);
    }
    public static void RequireCabinet(ICabinetService cabinet)
    {
        if (!cabinet.Status.Enabled || cabinet.Status.StandaloneCabinetRunning || cabinet.Status.IsSyncing)
            throw new InvalidOperationException(Resources.Strings.Agent_Account_Unavailable);
    }
    public static Task SyncAsync(ICabinetService cabinet, CancellationToken ct = default)
    {
        RequireCabinet(cabinet);
        return cabinet.SyncNowAsync(ct);
    }
}
