using SteamVault.Models;
using System.IO;
using System.IO.Compression;

namespace SteamVault.Services;

/// <summary>
/// Handles exporting all Lua configuration files from the user's Lua folder
/// into a timestamped ZIP archive at a user-specified location.
/// </summary>
public class ExportService
{
    private readonly SettingsService _settings;

    public ExportService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Exports all .lua files from the configured Lua directory into a ZIP file
    /// at the specified output path, named SteamVault_Backup_YYYYMMDD_HHmmss.zip.
    /// Returns the path to the created ZIP file, or null if no files.
    /// </summary>
    public async Task<ExportResult> ExportAllLuaFilesAsync(string outputDirectory, Action<string>? onStatus = null)
    {
        return await Task.Run(() => ExportAllLuaFiles(outputDirectory, onStatus));
    }

    private ExportResult ExportAllLuaFiles(string outputDirectory, Action<string>? onStatus)
    {
        var result = new ExportResult();

        try
        {
            var luaPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(luaPath) || !Directory.Exists(luaPath))
            {
                result.Error = "Lua output folder not found. Check your settings.";
                return result;
            }

            onStatus?.Invoke("Scanning Lua folder...");
            var luaFiles = Directory.GetFiles(luaPath, "*.lua", SearchOption.AllDirectories);

            if (luaFiles.Length == 0)
            {
                result.Error = "No .lua files found to export.";
                return result;
            }

            // Create backup file name
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var zipFileName = $"SteamVault_Backup_{timestamp}.zip";
            var zipFilePath = Path.Combine(outputDirectory, zipFileName);

            onStatus?.Invoke($"Creating ZIP: {zipFileName}...");

            using var zipStream = new FileStream(zipFilePath, FileMode.Create, FileAccess.Write);
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, true);

            // Add Lua files
            int added = 0;
            foreach (var file in luaFiles)
            {
                try
                {
                    var entryName = "lua/" + Path.GetFileName(file);
                    archive.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal);
                    added++;
                }
                catch
                {
                    // Skip files that can't be read
                }
            }

            result.Success = true;
            result.FilePath = zipFilePath;
            result.FileCount = added;
            result.ManifestCount = 0;
            result.TotalSizeBytes = new FileInfo(zipFilePath).Length;
        }
        catch (Exception ex)
        {
            result.Error = $"Export failed: {ex.Message}";
        }

        return result;
    }
}

public class ExportResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public int FileCount { get; set; }
    public int ManifestCount { get; set; }
    public long TotalSizeBytes { get; set; }

    public string SizeFormatted
    {
        get
        {
            if (TotalSizeBytes >= 1_048_576)
                return $"{TotalSizeBytes / 1_048_576.0:F1} MB";
            if (TotalSizeBytes >= 1_024)
                return $"{TotalSizeBytes / 1_024.0:F0} KB";
            return $"{TotalSizeBytes} B";
        }
    }
}