using DiscordRPC;
using System;
using System.Diagnostics;

namespace SteamVault.Services;

public class DiscordService : IDisposable
{
    private static DiscordService? _instance;
    public static DiscordService Instance => _instance ??= new DiscordService();

    private DiscordRpcClient? _client;
    // Replace with your real Discord Developer Application ID
    private const string ClientId = "1257489247192837482"; 

    private DiscordService()
    {
    }

    public void Initialize()
    {
        try
        {
            _client = new DiscordRpcClient(ClientId);
            _client.Initialize();
            
            SetPresence("Exploring the Vault", "Idle");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Discord RPC init failed: {ex.Message}");
        }
    }

    public void SetPresence(string details, string state)
    {
        if (_client == null || !_client.IsInitialized) return;

        try
        {
            _client.SetPresence(new RichPresence()
            {
                Details = details,
                State = state,
                Assets = new Assets()
                {
                    LargeImageKey = "logo", // Ensure an asset named 'logo' is uploaded to your Discord Dev portal
                    LargeImageText = "SteamVault Offline Manager"
                },
                Timestamps = Timestamps.Now,
                Buttons = new DiscordRPC.Button[]
                {
                    new DiscordRPC.Button() { Label = "Download SteamVault", Url = "https://github.com/Fluxerr" }
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Discord RPC set presence failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
    }
}
