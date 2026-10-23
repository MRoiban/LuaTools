using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LuaToolsGui.Services.Cabinet;

namespace LuaToolsGui.Services;

internal sealed record CloudRedirectCabinetStatus(bool Configured, string? Server);

internal sealed class CloudRedirectCabinetConfig
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _directory;
    private readonly Func<byte[], byte[]> _protect;
    private readonly Func<byte[], byte[]> _unprotect;
    private string TokenPath => Path.Combine(_directory, "tokens_cabinet.json");
    private string ConfigPath => Path.Combine(_directory, "config.json");

    internal CloudRedirectCabinetConfig(string? directory = null, Func<byte[], byte[]>? protector = null,
        Func<byte[], byte[]>? unprotector = null)
    {
        _directory = Path.GetFullPath(directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudRedirect"));
        _protect = protector ?? (bytes => ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
        _unprotect = unprotector ?? (bytes => ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
    }

    internal void Apply(string server, string token)
    {
        server = CabinetConnectInput.NormalizeServer(server);
        lock (Gate)
        {
            Directory.CreateDirectory(_directory);
            var config = ReadConfig(backupInvalid: true) ?? new JsonObject();
            byte[] credentials = JsonSerializer.SerializeToUtf8Bytes(new { server, token });
            try { AtomicWrite(TokenPath, _protect(credentials)); }
            finally { CryptographicOperations.ZeroMemory(credentials); }
            config["provider"] = "cabinet";
            config["token_path"] = TokenPath;
            var paths = config["token_paths"] as JsonObject ?? new JsonObject();
            paths["cabinet"] = TokenPath;
            if (config["token_paths"] is not JsonObject) config["token_paths"] = paths;
            config["auto_update_dll"] = false;
            AtomicWrite(ConfigPath, Encoding.UTF8.GetBytes(config.ToJsonString(JsonOptions)));
        }
    }

    internal void Remove()
    {
        lock (Gate)
        {
            File.Delete(TokenPath);
            var config = ReadConfig(backupInvalid: false);
            if (config is null) return;
            if (config["token_paths"] is JsonObject paths) paths.Remove("cabinet");
            if (config["provider"] is JsonValue provider && provider.TryGetValue<string>(out var name) && name == "cabinet")
            {
                config.Remove("provider");
                config.Remove("token_path");
            }
            AtomicWrite(ConfigPath, Encoding.UTF8.GetBytes(config.ToJsonString(JsonOptions)));
        }
    }

    internal CloudRedirectCabinetStatus Status()
    {
        lock (Gate)
        {
            var config = ReadConfig(backupInvalid: false);
            bool configured = config?["provider"] is JsonValue provider
                && provider.TryGetValue<string>(out var name) && name == "cabinet"
                && config["token_path"] is JsonValue path && path.TryGetValue<string>(out var tokenPath)
                && tokenPath == TokenPath && File.Exists(TokenPath);
            if (!configured) return new(false, null);
            byte[]? credentials = null;
            try
            {
                credentials = _unprotect(File.ReadAllBytes(TokenPath));
                using var json = JsonDocument.Parse(credentials);
                if (string.IsNullOrEmpty(json.RootElement.GetProperty("token").GetString())) return new(false, null);
                string server = CabinetConnectInput.NormalizeServer(json.RootElement.GetProperty("server").GetString());
                return new(true, server);
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or KeyNotFoundException
                or InvalidOperationException or FormatException or CabinetConnectException)
            { return new(false, null); }
            finally { if (credentials is not null) CryptographicOperations.ZeroMemory(credentials); }
        }
    }

    private JsonObject? ReadConfig(bool backupInvalid)
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(ConfigPath)) as JsonObject
                ?? throw new JsonException("Expected a JSON object.");
        }
        catch (JsonException)
        {
            if (backupInvalid) File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);
            return null;
        }
    }

    private static void AtomicWrite(string path, byte[] bytes)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
