using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using SteamVault.Models;

namespace SteamVault.Services;

public class FamilyVaultService
{
    private readonly SettingsService _settingsService;
    private readonly DepotKeyService _depotKeyService;
    private readonly SteamApiService _steamApiService;
    private readonly LuaGeneratorService _luaGeneratorService;

    public FamilyVaultManifest Manifest { get; private set; } = new();

    public FamilyVaultService(
        SettingsService settingsService,
        DepotKeyService depotKeyService,
        SteamApiService steamApiService,
        LuaGeneratorService luaGeneratorService)
    {
        _settingsService = settingsService;
        _depotKeyService = depotKeyService;
        _steamApiService = steamApiService;
        _luaGeneratorService = luaGeneratorService;

        LoadManifest();
    }

    private string GetManifestFilePath()
    {
        var steamDir = _settingsService.Settings.SteamDirectory;
        if (!string.IsNullOrWhiteSpace(steamDir) && Directory.Exists(steamDir))
        {
            var configDir = Path.Combine(steamDir, "config");
            Directory.CreateDirectory(configDir);
            return Path.Combine(configDir, "family_vault.json");
        }

        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamVault");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "family_vault.json");
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public void LoadManifest()
    {
        try
        {
            var path = GetManifestFilePath();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var manifest = JsonSerializer.Deserialize<FamilyVaultManifest>(json, _jsonOptions);
                if (manifest != null)
                {
                    Manifest = manifest;
                    Manifest.Members ??= new List<FamilyMember>();
                    Manifest.Games ??= new List<FamilyGameItem>();
                    EnsureDefaultUserMember();
                    return;
                }
            }
        }
        catch { }

        // Initialize new Family Vault Manifest if missing
        Manifest = new FamilyVaultManifest
        {
            HostUsername = _settingsService?.Settings?.Username ?? "VaultUser",
            Members = new List<FamilyMember>(),
            Games = new List<FamilyGameItem>()
        };
        EnsureDefaultUserMember();
        SaveManifest();
    }

    public void SaveManifest()
    {
        try
        {
            var path = GetManifestFilePath();
            var json = JsonSerializer.Serialize(Manifest, _jsonOptions);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private void EnsureDefaultUserMember()
    {
        Manifest.Members ??= new List<FamilyMember>();
        var user = _settingsService?.Settings?.Username;
        if (string.IsNullOrWhiteSpace(user)) user = "VaultUser";

        if (!Manifest.Members.Any(m => m != null && m.Name != null && m.Name.Equals(user, StringComparison.OrdinalIgnoreCase)))
        {
            Manifest.Members.Add(new FamilyMember
            {
                Name = user,
                IsHost = Manifest.Members.Count == 0,
                JoinedDate = DateTime.Now
            });
            SaveManifest();
        }
    }

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    static FamilyVaultService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
    }

    public async Task EnsureCloudRoomAsync()
    {
        if (string.IsNullOrWhiteSpace(Manifest.FamilyId))
        {
            Manifest.FamilyId = Guid.NewGuid().ToString("N")[..8].ToUpper();
        }
        Manifest.CloudRoomId = Manifest.FamilyId;
        SaveManifest();
        await PushCloudRoomUpdateAsync();
    }

    public async Task PushCloudRoomUpdateAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Manifest.FamilyId)) return;
            Manifest.CloudRoomId = Manifest.FamilyId;

            var json = JsonSerializer.Serialize(Manifest, _jsonOptions);
            await MqttRoomRelay.PublishRetainedAsync($"steamvault/family/{Manifest.FamilyId}", json);
        }
        catch { }
    }

    public async Task<bool> FetchCloudRoomUpdateAsync()
    {
        if (string.IsNullOrWhiteSpace(Manifest.FamilyId)) return false;

        try
        {
            var json = await MqttRoomRelay.FetchRetainedAsync($"steamvault/family/{Manifest.FamilyId}");
            if (string.IsNullOrWhiteSpace(json)) return false;

            var remote = JsonSerializer.Deserialize<FamilyVaultManifest>(json, _jsonOptions);
            if (remote == null || string.IsNullOrWhiteSpace(remote.FamilyId)) return false;

            remote.CloudRoomId = Manifest.FamilyId;

            var myUsername = _settingsService?.Settings?.Username ?? "VaultUser";
            bool iAmHost = Manifest.HostUsername.Equals(myUsername, StringComparison.OrdinalIgnoreCase);

            bool iWasRemoved = !iAmHost && remote.Members != null && !remote.Members.Any(m => m != null && m.Name != null && m.Name.Equals(myUsername, StringComparison.OrdinalIgnoreCase));

            if ((remote.IsDisbanded && !iAmHost) || iWasRemoved)
            {
                LeaveFamily();
                return true;
            }

            remote.Members ??= new List<FamilyMember>();
            remote.Games ??= new List<FamilyGameItem>();

            bool changed = (remote.Members.Count != Manifest.Members.Count) ||
                           (remote.Games.Count != Manifest.Games.Count) ||
                           !remote.Games.Select(g => g.AppId).SequenceEqual(Manifest.Games.Select(g => g.AppId)) ||
                           !remote.Members.Select(m => m.Name).SequenceEqual(Manifest.Members.Select(m => m.Name)) ||
                           remote.IsDisbanded != Manifest.IsDisbanded;

            if (changed)
            {
                Manifest = remote;
                EnsureDefaultUserMember();
                SaveManifest();
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }

    public bool AddGameToFamily(uint appId, string name, string headerImage, string addedBy)
    {
        if (Manifest.Games.Any(g => g.AppId == appId))
            return false; // Already in Family Pool

        Manifest.Games.Add(new FamilyGameItem
        {
            AppId = appId,
            Name = name,
            HeaderImage = headerImage,
            AddedBy = addedBy,
            AddedDate = DateTime.Now
        });

        SaveManifest();
        _ = Task.Run(async () => await PushCloudRoomUpdateAsync());
        return true;
    }

    public bool RemoveGameFromFamily(uint appId)
    {
        var game = Manifest.Games.FirstOrDefault(g => g.AppId == appId);
        if (game == null) return false;

        Manifest.Games.Remove(game);
        SaveManifest();
        _ = Task.Run(async () => await PushCloudRoomUpdateAsync());

        try
        {
            var luaOutDir = _settingsService.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaOutDir))
            {
                var steamDir = _settingsService.Settings.SteamDirectory;
                if (!string.IsNullOrWhiteSpace(steamDir))
                    luaOutDir = Path.Combine(steamDir, "config", "lua");
            }

            if (!string.IsNullOrWhiteSpace(luaOutDir))
            {
                var luaPath = Path.Combine(luaOutDir, $"{appId}.lua");
                if (File.Exists(luaPath))
                {
                    File.Delete(luaPath);
                }
            }
        }
        catch { }

        return true;
    }

    public bool AddMember(string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName)) return false;
        if (Manifest.Members.Any(m => m.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase)))
            return false; // Already a member

        Manifest.Members.Add(new FamilyMember
        {
            Name = memberName,
            IsHost = false,
            JoinedDate = DateTime.Now
        });

        SaveManifest();
        _ = Task.Run(async () => await PushCloudRoomUpdateAsync());
        return true;
    }

    public bool RemoveMember(string memberId)
    {
        var member = Manifest.Members.FirstOrDefault(m => m.Id == memberId);
        if (member == null) return false;

        Manifest.Members.Remove(member);
        SaveManifest();
        _ = Task.Run(async () => await PushCloudRoomUpdateAsync());
        return true;
    }

    public async Task<string> ExportFamilyTokenAsync()
    {
        await EnsureCloudRoomAsync();

        if (!string.IsNullOrWhiteSpace(Manifest.FamilyId))
        {
            return $"steamvault://join/{Manifest.FamilyId}";
        }

        var payload = new
        {
            CloudRoomId = Manifest.FamilyId,
            FamilyId = Manifest.FamilyId,
            HostUsername = Manifest.HostUsername,
            FullManifest = Manifest
        };

        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return Convert.ToBase64String(bytes);
    }

    public async Task<bool> ImportFamilyTokenAsync(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            
            var cleanToken = token.Trim().Trim('"', '\'', '`', '<', '>', '[', ']');

            // Strip protocol or link scheme prefixes
            if (cleanToken.StartsWith("steamvault://join/", StringComparison.OrdinalIgnoreCase))
            {
                cleanToken = cleanToken.Substring("steamvault://join/".Length).Trim();
            }
            else if (cleanToken.StartsWith("steamvault://join?", StringComparison.OrdinalIgnoreCase) || cleanToken.Contains("code="))
            {
                var match = System.Text.RegularExpressions.Regex.Match(cleanToken, @"code=([^&]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    cleanToken = match.Groups[1].Value.Trim();
                }
                else if (cleanToken.StartsWith("steamvault://join?", StringComparison.OrdinalIgnoreCase))
                {
                    cleanToken = cleanToken.Substring("steamvault://join?".Length).Trim();
                }
            }
            else if (cleanToken.StartsWith("steamvault://", StringComparison.OrdinalIgnoreCase))
            {
                cleanToken = cleanToken.Substring("steamvault://".Length).Trim();
            }
            else if (cleanToken.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || cleanToken.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(cleanToken);
                var lastSeg = uri.Segments.LastOrDefault()?.Trim('/');
                if (!string.IsNullOrWhiteSpace(lastSeg))
                {
                    cleanToken = lastSeg;
                }
            }

            if (cleanToken.Contains("?"))
            {
                var qIdx = cleanToken.IndexOf('?');
                cleanToken = cleanToken.Substring(0, qIdx).Trim();
            }
            cleanToken = cleanToken.TrimEnd('/');

            if (string.IsNullOrWhiteSpace(cleanToken)) return false;

            FamilyVaultManifest? imported = null;
            string targetFamilyId = "";

            if (!cleanToken.Contains("{") && !cleanToken.Contains(":") && cleanToken.Length >= 4 && cleanToken.Length <= 64)
            {
                targetFamilyId = cleanToken;
            }

            if (imported == null && string.IsNullOrWhiteSpace(targetFamilyId) && cleanToken.StartsWith("{"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(cleanToken);
                    if (doc.RootElement.TryGetProperty("FamilyId", out var idElem) || doc.RootElement.TryGetProperty("familyId", out idElem) || doc.RootElement.TryGetProperty("CloudRoomId", out idElem))
                    {
                        targetFamilyId = idElem.GetString() ?? "";
                    }

                    if (doc.RootElement.TryGetProperty("FullManifest", out var fullElem) || doc.RootElement.TryGetProperty("fullManifest", out fullElem))
                    {
                        imported = JsonSerializer.Deserialize<FamilyVaultManifest>(fullElem.GetRawText(), _jsonOptions);
                    }
                    else if (doc.RootElement.TryGetProperty("FamilyId", out _) || doc.RootElement.TryGetProperty("familyId", out _))
                    {
                        imported = JsonSerializer.Deserialize<FamilyVaultManifest>(cleanToken, _jsonOptions);
                    }
                }
                catch { }
            }

            if (imported == null && string.IsNullOrWhiteSpace(targetFamilyId))
            {
                try
                {
                    string base64 = cleanToken.Replace('-', '+').Replace('_', '/');
                    switch (base64.Length % 4)
                    {
                        case 2: base64 += "=="; break;
                        case 3: base64 += "="; break;
                    }

                    var bytes = Convert.FromBase64String(base64);
                    var json = System.Text.Encoding.UTF8.GetString(bytes);

                    if (json.StartsWith("{") && json.EndsWith("}"))
                    {
                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.TryGetProperty("FamilyId", out var idElem) || doc.RootElement.TryGetProperty("familyId", out idElem) || doc.RootElement.TryGetProperty("CloudRoomId", out idElem))
                        {
                            targetFamilyId = idElem.GetString() ?? "";
                        }

                        if (doc.RootElement.TryGetProperty("FullManifest", out var fullElem) || doc.RootElement.TryGetProperty("fullManifest", out fullElem))
                        {
                            imported = JsonSerializer.Deserialize<FamilyVaultManifest>(fullElem.GetRawText(), _jsonOptions);
                        }
                        else if (doc.RootElement.TryGetProperty("FamilyId", out _) || doc.RootElement.TryGetProperty("familyId", out _))
                        {
                            imported = JsonSerializer.Deserialize<FamilyVaultManifest>(json, _jsonOptions);
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(targetFamilyId) && imported == null && !cleanToken.Contains("{"))
            {
                targetFamilyId = cleanToken;
            }

            if (!string.IsNullOrWhiteSpace(targetFamilyId))
            {
                try
                {
                    var remoteJson = await MqttRoomRelay.FetchRetainedAsync($"steamvault/family/{targetFamilyId}");
                    if (!string.IsNullOrWhiteSpace(remoteJson))
                    {
                        var remote = JsonSerializer.Deserialize<FamilyVaultManifest>(remoteJson, _jsonOptions);
                        if (remote != null && !string.IsNullOrWhiteSpace(remote.FamilyId))
                        {
                            imported = remote;
                        }
                    }
                }
                catch { }
            }

            if (imported == null || string.IsNullOrWhiteSpace(imported.FamilyId)) return false;

            ClearAllLuaFiles();

            Manifest = imported;
            Manifest.CloudRoomId = Manifest.FamilyId;
            Manifest.Members ??= new List<FamilyMember>();
            Manifest.Games ??= new List<FamilyGameItem>();

            var myUsername = _settingsService?.Settings?.Username ?? "VaultUser";
            if (!Manifest.Members.Any(m => m != null && m.Name != null && m.Name.Equals(myUsername, StringComparison.OrdinalIgnoreCase)))
            {
                Manifest.Members.Add(new FamilyMember
                {
                    Name = myUsername,
                    IsHost = false,
                    JoinedDate = DateTime.Now
                });
            }

            SaveManifest();
            await PushCloudRoomUpdateAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void CloseFamily()
    {
        if (!string.IsNullOrWhiteSpace(Manifest.FamilyId))
        {
            var oldManifest = Manifest;
            oldManifest.IsDisbanded = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    var json = JsonSerializer.Serialize(oldManifest, _jsonOptions);
                    await MqttRoomRelay.PublishRetainedAsync($"steamvault/family/{oldManifest.FamilyId}", json);
                }
                catch { }
            });
        }

        ClearAllLuaFiles();

        var myUsername = _settingsService?.Settings?.Username ?? "VaultUser";
        Manifest = new FamilyVaultManifest
        {
            FamilyId = Guid.NewGuid().ToString("N")[..8].ToUpper(),
            CloudRoomId = "",
            FamilyName = $"{myUsername}'s Vault",
            HostUsername = myUsername,
            CreatedDate = DateTime.Now,
            Members = new List<FamilyMember>
            {
                new FamilyMember
                {
                    Name = myUsername,
                    IsHost = true,
                    JoinedDate = DateTime.Now
                }
            },
            Games = new List<FamilyGameItem>()
        };

        SaveManifest();
        _ = Task.Run(async () => await EnsureCloudRoomAsync());
    }

    public void LeaveFamily()
    {
        ClearAllLuaFiles();

        var myUsername = _settingsService?.Settings?.Username ?? "VaultUser";
        if (!string.IsNullOrWhiteSpace(Manifest.FamilyId))
        {
            var self = Manifest.Members.FirstOrDefault(m => m.Name != null && m.Name.Equals(myUsername, StringComparison.OrdinalIgnoreCase));
            if (self != null)
            {
                Manifest.Members.Remove(self);
                _ = Task.Run(async () => await PushCloudRoomUpdateAsync());
            }
        }

        Manifest = new FamilyVaultManifest
        {
            FamilyId = Guid.NewGuid().ToString("N")[..8].ToUpper(),
            CloudRoomId = "",
            FamilyName = $"{myUsername}'s Vault",
            HostUsername = myUsername,
            CreatedDate = DateTime.Now,
            Members = new List<FamilyMember>
            {
                new FamilyMember
                {
                    Name = myUsername,
                    IsHost = true,
                    JoinedDate = DateTime.Now
                }
            },
            Games = new List<FamilyGameItem>()
        };

        SaveManifest();
        _ = Task.Run(async () => await EnsureCloudRoomAsync());
    }

    public void ClearAllLuaFiles()
    {
        try
        {
            var luaOutDir = _settingsService.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaOutDir))
            {
                var steamDir = _settingsService.Settings.SteamDirectory;
                if (!string.IsNullOrWhiteSpace(steamDir))
                    luaOutDir = Path.Combine(steamDir, "config", "lua");
            }

            if (!string.IsNullOrWhiteSpace(luaOutDir) && Directory.Exists(luaOutDir) && Manifest?.Games != null)
            {
                foreach (var game in Manifest.Games)
                {
                    if (game == null) continue;
                    try
                    {
                        var luaPath = Path.Combine(luaOutDir, $"{game.AppId}.lua");
                        if (File.Exists(luaPath)) File.Delete(luaPath);
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    public async Task SyncAllFamilyGamesToLuaAsync(Action<string>? onStatus = null, Action<double>? onProgress = null)
    {
        await Task.Run(async () =>
        {
            var luaOutDir = _settingsService.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaOutDir))
            {
                var steamDir = _settingsService.Settings.SteamDirectory;
                if (!string.IsNullOrWhiteSpace(steamDir))
                    luaOutDir = Path.Combine(steamDir, "config", "lua");
            }

            if (string.IsNullOrWhiteSpace(luaOutDir)) return;

            Directory.CreateDirectory(luaOutDir);

            if (!_depotKeyService.IsLoaded)
            {
                onStatus?.Invoke("Loading decryption keys...");
                await _depotKeyService.LoadAsync();
            }

            var games = Manifest.Games?.Where(g => g != null).ToList() ?? new List<FamilyGameItem>();
            if (games.Count == 0)
            {
                onStatus?.Invoke("No games in Family Pool to sync.");
                onProgress?.Invoke(100);
                return;
            }

            for (int i = 0; i < games.Count; i++)
            {
                var game = games[i];
                var pct = (double)(i + 1) / games.Count * 100.0;
                onStatus?.Invoke($"Syncing Lua config ({i + 1}/{games.Count}): {game.Name}...");
                onProgress?.Invoke(pct);

                try
                {
                    var appIdStr = game.AppId.ToString();
                    var depots = _depotKeyService.FindDepotsForApp(appIdStr);
                    var depotEntries = depots.Select(k => new DepotLuaEntry
                    {
                        DepotId = k.DepotId,
                        DecryptionKey = k.DecryptionKey ?? ""
                    }).ToList();

                    _luaGeneratorService.GenerateLuaFile(
                        appIdStr,
                        game.Name,
                        depotEntries,
                        luaOutDir
                    );
                }
                catch { }

                await Task.Delay(50);
            }

            onStatus?.Invoke($"✓ Successfully synced all {games.Count} game config(s) to Steam!");
            onProgress?.Invoke(100);
        });
    }
}

public static class MqttRoomRelay
{
    private static readonly string[] Brokers = new[] { "broker.hivemq.com", "broker.emqx.io" };

    public static async Task<bool> PublishRetainedAsync(string topic, string payloadJson)
    {
        foreach (var broker in Brokers)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(broker, 1883);
                if (await Task.WhenAny(connectTask, Task.Delay(4000)) != connectTask || !client.Connected)
                    continue;

                using var stream = client.GetStream();
                stream.ReadTimeout = 4000;
                stream.WriteTimeout = 4000;

                var clientId = "SV_" + Guid.NewGuid().ToString("N")[..8];
                var connPkt = BuildConnectPacket(clientId);
                await stream.WriteAsync(connPkt, 0, connPkt.Length);

                var buf = new byte[10];
                int read = await stream.ReadAsync(buf, 0, buf.Length);
                if (read < 4 || buf[0] != 0x20 || buf[3] != 0x00)
                    continue;

                var pubPkt = BuildPublishPacket(topic, payloadJson, retain: true);
                await stream.WriteAsync(pubPkt, 0, pubPkt.Length);
                await Task.Delay(100);
                return true;
            }
            catch { }
        }
        return false;
    }

    public static async Task<string?> FetchRetainedAsync(string topic)
    {
        foreach (var broker in Brokers)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(broker, 1883);
                if (await Task.WhenAny(connectTask, Task.Delay(4000)) != connectTask || !client.Connected)
                    continue;

                using var stream = client.GetStream();
                stream.ReadTimeout = 4000;
                stream.WriteTimeout = 4000;

                var clientId = "SV_Fetch_" + Guid.NewGuid().ToString("N")[..8];
                var connPkt = BuildConnectPacket(clientId);
                await stream.WriteAsync(connPkt, 0, connPkt.Length);

                var buf = new byte[10];
                int read = await stream.ReadAsync(buf, 0, buf.Length);
                if (read < 4 || buf[0] != 0x20 || buf[3] != 0x00)
                    continue;

                var subPkt = BuildSubscribePacket(topic, msgId: 1);
                await stream.WriteAsync(subPkt, 0, subPkt.Length);

                var payload = await ReadPublishPayloadAsync(stream, timeoutMs: 3000);
                if (!string.IsNullOrWhiteSpace(payload))
                    return payload;
            }
            catch { }
        }
        return null;
    }

    private static byte[] BuildConnectPacket(string clientId)
    {
        var cidBytes = System.Text.Encoding.UTF8.GetBytes(clientId);
        var vh = new byte[] { 0x00, 0x04, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', 0x04, 0x02, 0x00, 0x3C };
        var payload = new byte[2 + cidBytes.Length];
        payload[0] = (byte)(cidBytes.Length >> 8);
        payload[1] = (byte)(cidBytes.Length & 0xFF);
        Buffer.BlockCopy(cidBytes, 0, payload, 2, cidBytes.Length);

        var remLen = vh.Length + payload.Length;
        var remLenBytes = EncodeVariableLength(remLen);
        var pkt = new byte[1 + remLenBytes.Length + remLen];
        pkt[0] = 0x10;
        Buffer.BlockCopy(remLenBytes, 0, pkt, 1, remLenBytes.Length);
        Buffer.BlockCopy(vh, 0, pkt, 1 + remLenBytes.Length, vh.Length);
        Buffer.BlockCopy(payload, 0, pkt, 1 + remLenBytes.Length + vh.Length, payload.Length);
        return pkt;
    }

    private static byte[] BuildPublishPacket(string topic, string message, bool retain)
    {
        var tBytes = System.Text.Encoding.UTF8.GetBytes(topic);
        var mBytes = System.Text.Encoding.UTF8.GetBytes(message);
        var vh = new byte[2 + tBytes.Length];
        vh[0] = (byte)(tBytes.Length >> 8);
        vh[1] = (byte)(tBytes.Length & 0xFF);
        Buffer.BlockCopy(tBytes, 0, vh, 2, tBytes.Length);

        var remLen = vh.Length + mBytes.Length;
        var remLenBytes = EncodeVariableLength(remLen);
        byte cmd = (byte)(retain ? 0x31 : 0x30);
        var pkt = new byte[1 + remLenBytes.Length + remLen];
        pkt[0] = cmd;
        Buffer.BlockCopy(remLenBytes, 0, pkt, 1, remLenBytes.Length);
        Buffer.BlockCopy(vh, 0, pkt, 1 + remLenBytes.Length, vh.Length);
        Buffer.BlockCopy(mBytes, 0, pkt, 1 + remLenBytes.Length + vh.Length, mBytes.Length);
        return pkt;
    }

    private static byte[] BuildSubscribePacket(string topic, ushort msgId)
    {
        var tBytes = System.Text.Encoding.UTF8.GetBytes(topic);
        var vh = new byte[] { (byte)(msgId >> 8), (byte)(msgId & 0xFF) };
        var payload = new byte[2 + tBytes.Length + 1];
        payload[0] = (byte)(tBytes.Length >> 8);
        payload[1] = (byte)(tBytes.Length & 0xFF);
        Buffer.BlockCopy(tBytes, 0, payload, 2, tBytes.Length);
        payload[payload.Length - 1] = 0x00;

        var remLen = vh.Length + payload.Length;
        var remLenBytes = EncodeVariableLength(remLen);
        var pkt = new byte[1 + remLenBytes.Length + remLen];
        pkt[0] = 0x82;
        Buffer.BlockCopy(remLenBytes, 0, pkt, 1, remLenBytes.Length);
        Buffer.BlockCopy(vh, 0, pkt, 1 + remLenBytes.Length, vh.Length);
        Buffer.BlockCopy(payload, 0, pkt, 1 + remLenBytes.Length + vh.Length, payload.Length);
        return pkt;
    }

    private static byte[] EncodeVariableLength(int length)
    {
        using var ms = new MemoryStream();
        do
        {
            byte digit = (byte)(length % 128);
            length /= 128;
            if (length > 0)
                digit |= 0x80;
            ms.WriteByte(digit);
        } while (length > 0);
        return ms.ToArray();
    }

    private static async Task<string?> ReadPublishPayloadAsync(System.Net.Sockets.NetworkStream stream, int timeoutMs)
    {
        var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var headerBuf = new byte[1];
                int r = await stream.ReadAsync(headerBuf, 0, 1, cts.Token);
                if (r == 0) break;
                byte cmd = headerBuf[0];

                int remLen = await ReadVariableLengthAsync(stream, cts.Token);
                var data = new byte[remLen];
                int readTotal = 0;
                while (readTotal < remLen)
                {
                    int n = await stream.ReadAsync(data, readTotal, remLen - readTotal, cts.Token);
                    if (n == 0) break;
                    readTotal += n;
                }

                if ((cmd & 0xF0) == 0x30)
                {
                    ushort topicLen = (ushort)((data[0] << 8) | data[1]);
                    int payloadOffset = 2 + topicLen;
                    if (data.Length >= payloadOffset)
                    {
                        return System.Text.Encoding.UTF8.GetString(data, payloadOffset, data.Length - payloadOffset);
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task<int> ReadVariableLengthAsync(System.Net.Sockets.NetworkStream stream, CancellationToken token)
    {
        int multiplier = 1;
        int value = 0;
        byte[] buf = new byte[1];
        do
        {
            int r = await stream.ReadAsync(buf, 0, 1, token);
            if (r == 0) break;
            byte b = buf[0];
            value += (b & 127) * multiplier;
            multiplier *= 128;
            if ((b & 128) == 0) break;
        } while (true);
        return value;
    }
}

