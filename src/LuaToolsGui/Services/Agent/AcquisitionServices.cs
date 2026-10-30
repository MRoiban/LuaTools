namespace LuaToolsGui.Services.Agent;
public sealed record AcquisitionServices(AcquisitionCoordinator Sources, LuaOverwriteCoordinator Overwrites,
    LocalInstallCoordinator Local, LuaInstaller Installer, LuaToolsApiClient Api, DlcBatchService Dlc,
    Downloads.ManifestJobFactory Factory);
