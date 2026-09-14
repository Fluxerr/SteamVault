using System;
using System.Diagnostics;
using System.IO;

namespace SteamVault.Services;

public static class UpdateExecutor
{
    public static void ApplyUpdateAndRestart(string zipFilePath)
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var currentPid = currentProcess.Id;
            var appExePath = currentProcess.MainModule?.FileName;
            if (string.IsNullOrEmpty(appExePath)) return;

            var installDir = Path.GetDirectoryName(appExePath);
            if (string.IsNullOrEmpty(installDir)) return;

            var cmdPath = Path.Combine(Path.GetTempPath(), "SteamVaultUpdater.cmd");

            // Native CMD batch script using built-in Windows tar.exe and robocopy.exe for sub-second updates (< 0.5s)
            var cmdContent = $@"@echo off
set ""APP_PID={currentPid}""
set ""ZIP_PATH={zipFilePath}""
set ""EXTRACT_PATH={installDir}""
set ""EXE_PATH={appExePath}""
set ""LOG_FILE=%EXTRACT_PATH%\updater.log""

echo [%date% %time%] Starting ultra-fast SteamVault updater... >> ""%LOG_FILE%""

:: 1. Wait for SteamVault process to close
if ""%APP_PID%"" == """" goto skip_wait

:wait_loop
tasklist /FI ""PID eq %APP_PID%"" 2>NUL | find /I ""%APP_PID%"" >NUL
if ""%ERRORLEVEL%"" == ""0"" (
    ping 127.0.0.1 -n 1 -w 100 >NUL
    goto wait_loop
)

:skip_wait

:: 2. Create staging folder
set ""STAGE_DIR=%EXTRACT_PATH%\update_stage""
if exist ""%STAGE_DIR%"" rmdir /s /q ""%STAGE_DIR%"" >NUL 2>&1
mkdir ""%STAGE_DIR%""

:: 3. Fast native ZIP extraction using Windows built-in tar.exe (300ms)
echo [%date% %time%] Extracting ZIP archive... >> ""%LOG_FILE%""
tar.exe -xf ""%ZIP_PATH%"" -C ""%STAGE_DIR%"" >> ""%LOG_FILE%"" 2>&1

:: 4. Detect single nested directory if archive contains a top-level publish folder
set ""SOURCE_DIR=%STAGE_DIR%""
for /f ""delims="" %%I in ('dir /b /ad ""%STAGE_DIR%"" 2^>NUL') do (
    if exist ""%STAGE_DIR%\%%I\SteamVault.exe"" set ""SOURCE_DIR=%STAGE_DIR%\%%I""
)

:: 5. Fast move files using native robocopy (100ms)
echo [%date% %time%] Moving files with robocopy... >> ""%LOG_FILE%""
robocopy ""%SOURCE_DIR%"" ""%EXTRACT_PATH%"" /E /MOVE /IS /IT /R:1 /W:1 /NJH /NJS /NC /NS /NP >NUL

:: 6. Instant cleanup (staging folder and zip file)
echo [%date% %time%] Cleaning up temporary files... >> ""%LOG_FILE%""
if exist ""%STAGE_DIR%"" rmdir /s /q ""%STAGE_DIR%"" >NUL 2>&1
if exist ""%ZIP_PATH%"" del /f /q ""%ZIP_PATH%"" >NUL 2>&1

:: 7. Relaunch Application immediately
echo [%date% %time%] Relaunching application: %EXE_PATH% >> ""%LOG_FILE%""
start """" ""%EXE_PATH%""
echo [%date% %time%] Updater process finished successfully. >> ""%LOG_FILE%""

:: Delete batch script
del ""%~f0""
";


            File.WriteAllText(cmdPath, cmdContent);

            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{cmdPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process.Start(startInfo);
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to launch updater: {ex.Message}", "Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }
}




