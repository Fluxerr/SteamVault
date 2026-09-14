using SteamVault.Models;
using System.IO;
using System.IO.Compression;

namespace SteamVault.Services;

public class ImportService
{
    private readonly SettingsService _settings;
    private readonly LuaParserService _luaParser;
    private readonly SteamApiService _steamApi;

    public ImportService(
        SettingsService settings,
        LuaParserService luaParser,
        SteamApiService steamApi)
    {
        _settings = settings;
        _luaParser = luaParser;
        _steamApi = steamApi;
    }


    /// <summary>
    /// Legacy import method for backward compatibility. Imports files without game detection.
    /// </summary>
    public async Task<List<string>> ImportFilesAsync(string[] filePaths)
    {
        var importedGames = new List<string>();
        var outputPath = _settings.Settings.LuaOutputPath;

        if (string.IsNullOrWhiteSpace(outputPath) || !Directory.Exists(outputPath))
        {
            return importedGames;
        }

        foreach (var path in filePaths)
        {
            if (!File.Exists(path)) continue;

            var ext = Path.GetExtension(path).ToLowerInvariant();

            try
            {
                if (ext == ".lua")
                {
                    var dest = Path.Combine(outputPath, Path.GetFileName(path));
                    File.Copy(path, dest, true);
                    importedGames.Add(Path.GetFileNameWithoutExtension(path));
                }
                else if (ext == ".zip")
                {
                    // Extract all .lua and .manifest files from the zip
                    await Task.Run(() =>
                    {
                        using var archive = ZipFile.OpenRead(path);
                        foreach (var entry in archive.Entries)
                        {
                            var entryExt = Path.GetExtension(entry.FullName).ToLowerInvariant();
                            if (entryExt == ".lua")
                            {
                                var dest = Path.Combine(outputPath, entry.Name);
                                entry.ExtractToFile(dest, true);
                                importedGames.Add(Path.GetFileNameWithoutExtension(entry.Name));
                            }
                            else if (entryExt == ".manifest")
                            {
                                // Copy manifest to all depotcache folders (config/depotcache and depotcache)
                                var depotcachePaths = _settings.Settings.GetAllDepotcachePaths();
                                foreach (var depotCache in depotcachePaths)
                                {
                                    try
                                    {
                                        Directory.CreateDirectory(depotCache);
                                        var dest = Path.Combine(depotCache, entry.Name);
                                        entry.ExtractToFile(dest, true);
                                    }
                                    catch { }
                                }
                            }
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to import {path}: {ex.Message}");
            }
        }

        return importedGames;
    }

    /// <summary>
    /// Smart import of a single .lua file:
    /// 1. Parses the lua to extract the App ID
    /// 2. Resolves game name + header image from Steam API
    /// 3. Copies lua to the configured output folder
    /// 4. Downloads manifests from HubCap
    /// Returns a fully populated ImportedGameInfo.
    /// </summary>
    public async Task<ImportedGameInfo> SmartImportLuaAsync(
        string luaFilePath,
        Action<string>? onStatus = null)
    {
        var result = new ImportedGameInfo
        {
            LuaSourcePath = luaFilePath,
            Status = "Processing"
        };

        try
        {
            var outputPath = _settings.Settings.LuaOutputPath;
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                result.Status = "Failed";
                result.ErrorMessage = "Lua output path is not configured.";
                return result;
            }
            Directory.CreateDirectory(outputPath);

            // Step 1: Parse the lua to extract App ID
            result.Status = "Resolving";
            onStatus?.Invoke("Parsing Lua file...");

            var parsed = _luaParser.ParseLuaFile(luaFilePath);
            if (parsed == null || string.IsNullOrWhiteSpace(parsed.AppId))
            {
                // Fallback to filename
                result.AppId = Path.GetFileNameWithoutExtension(luaFilePath);
            }
            else
            {
                result.AppId = parsed.AppId;
                if (!string.IsNullOrWhiteSpace(parsed.Name) && parsed.Name != "Unknown")
                    result.GameName = parsed.Name;
            }

            // Step 2: Resolve game details from Steam API
            onStatus?.Invoke($"Looking up App ID {result.AppId} on Steam...");
            try
            {
                var gameInfo = await _steamApi.GetAppDetailsAsync(result.AppId);
                if (gameInfo != null)
                {
                    result.GameName = gameInfo.Name;
                    result.HeaderImageUrl = gameInfo.HeaderImageUrl;
                }
            }
            catch
            {
                // Non-fatal: we can still import without Steam metadata
            }

            // Step 3: Copy lua to output folder
            result.Status = "Processing";
            onStatus?.Invoke($"Installing {result.GameName}...");

            var destPath = Path.Combine(outputPath, Path.GetFileName(luaFilePath));
            // If the filename doesn't match the App ID pattern, rename it
            if (Path.GetFileNameWithoutExtension(luaFilePath) != result.AppId)
            {
                destPath = Path.Combine(outputPath, $"{result.AppId}.lua");
            }
            File.Copy(luaFilePath, destPath, true);

            result.ManifestCount = 0;
            result.Success = true;
            result.Status = "Complete";
            onStatus?.Invoke($"✓ {result.GameName} imported successfully!");

        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Status = "Failed";
            result.ErrorMessage = ex.Message;
            onStatus?.Invoke($"✗ Import failed: {ex.Message}");
        }

        return result;
    }

    /// <summary>
    /// Smart import of a .zip file containing multiple .lua (and optionally .manifest) files.
    /// Extracts all luas to a temp folder, processes each one individually, and returns
    /// a list of all imported games with full metadata.
    /// </summary>
    public async Task<List<ImportedGameInfo>> SmartImportZipAsync(
        string zipFilePath,
        Action<ImportedGameInfo>? onGameDiscovered = null,
        Action<string>? onStatus = null)
    {
        var results = new List<ImportedGameInfo>();
        var outputPath = _settings.Settings.LuaOutputPath;

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return results;
        }
        Directory.CreateDirectory(outputPath);

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SteamVault", "temp_import");

        try
        {
            // Clean up any previous temp folder
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            onStatus?.Invoke("Extracting zip archive...");

            // Step 1: Extract zip
            var luaFiles = new List<string>();
            var manifestFiles = new List<string>();

            await Task.Run(() =>
            {
                using var archive = ZipFile.OpenRead(zipFilePath);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Name)) continue;

                    var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                    if (ext == ".lua")
                    {
                        var dest = Path.Combine(tempDir, entry.Name);
                        entry.ExtractToFile(dest, true);
                        luaFiles.Add(dest);
                    }
                    else if (ext == ".manifest")
                    {
                        // Extract manifests directly to depotcache folders
                        var depotcachePaths = _settings.Settings.GetAllDepotcachePaths();
                        foreach (var depotCache in depotcachePaths)
                        {
                            try
                            {
                                Directory.CreateDirectory(depotCache);
                                var dest = Path.Combine(depotCache, entry.Name);
                                entry.ExtractToFile(dest, true);

                                // Set ReadOnly to protect from Steam deletion
                                try { new System.IO.FileInfo(dest).IsReadOnly = true; } catch { }
                            }
                            catch { }
                        }
                        manifestFiles.Add(entry.Name);
                    }
                }
            });

            onStatus?.Invoke($"Found {luaFiles.Count} Lua file(s) and {manifestFiles.Count} manifest(s).");

            // Step 2: Discover all games first (parse each lua for App ID and resolve from Steam)
            foreach (var luaFile in luaFiles)
            {
                var entry = new ImportedGameInfo
                {
                    LuaSourcePath = luaFile,
                    Status = "Queued"
                };

                // Quick parse to get App ID
                try
                {
                    var parsed = _luaParser.ParseLuaFile(luaFile);
                    if (parsed != null && !string.IsNullOrWhiteSpace(parsed.AppId))
                    {
                        entry.AppId = parsed.AppId;
                        if (!string.IsNullOrWhiteSpace(parsed.Name) && parsed.Name != "Unknown")
                            entry.GameName = parsed.Name;
                    }
                    else
                    {
                        entry.AppId = Path.GetFileNameWithoutExtension(luaFile);
                    }
                }
                catch
                {
                    entry.AppId = Path.GetFileNameWithoutExtension(luaFile);
                }

                results.Add(entry);
                onGameDiscovered?.Invoke(entry);
            }

            // Step 3: Process each game sequentially
            foreach (var game in results)
            {
                game.Status = "Resolving";

                // Resolve from Steam API
                try
                {
                    var gameInfo = await _steamApi.GetAppDetailsAsync(game.AppId);
                    if (gameInfo != null)
                    {
                        game.GameName = gameInfo.Name;
                        game.HeaderImageUrl = gameInfo.HeaderImageUrl;
                    }
                }
                catch { }

                // Copy lua to output folder
                game.Status = "Processing";
                try
                {
                    var destPath = Path.Combine(outputPath, $"{game.AppId}.lua");
                    File.Copy(game.LuaSourcePath, destPath, true);
                }
                catch (Exception ex)
                {
                    game.Status = "Failed";
                    game.ErrorMessage = $"Failed to copy lua: {ex.Message}";
                    game.Success = false;
                    continue;
                }

                game.ManifestCount = 0;
                game.Success = true;
                game.Status = "Complete";

            }
        }
        catch (Exception ex)
        {
            onStatus?.Invoke($"Zip import error: {ex.Message}");
        }
        finally
        {
            // Cleanup temp
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
            catch { }
        }

        return results;
    }
}
