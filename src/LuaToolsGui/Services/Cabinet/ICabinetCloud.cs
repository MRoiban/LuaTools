namespace LuaToolsGui.Services.Cabinet;

public interface ICabinetCloud
{
    CabinetCloudStatus Status { get; }
    event EventHandler<CabinetCloudStatus>? StatusChanged;
    bool FriendLabels { get; set; }
    Task ConnectAsync(string input, string? server = null, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}
