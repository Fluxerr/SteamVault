using Newtonsoft.Json;
using SteamVault.Models;
using System.IO;

namespace SteamVault.Services;

public class SettingsService
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SteamVault");
    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public AppSettings Settings { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            File.WriteAllText(SettingsFile, json);
        }
        catch { }
    }
}
