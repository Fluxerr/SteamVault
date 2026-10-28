using System;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SteamVault.Services
{
    public class SystemHardwareSpecs
    {
        public double TotalRamGB { get; set; }
        public string CpuName { get; set; } = "Unknown CPU";
        public string GpuName { get; set; } = "Unknown GPU";

        public string RamFormatted => $"{Math.Round(TotalRamGB, 1)} GB RAM";
    }

    public class CompatibilityResult
    {
        public bool IsCompatible { get; set; } = true;
        public string Rating { get; set; } = "Pass"; // "Pass", "Warning", "Fail"
        public string BadgeColor { get; set; } = "#34D399"; // Green, Orange, Red
        public double RequiredRamGB { get; set; }
        public string MinimumRequirementsText { get; set; } = string.Empty;
        public string RecommendationText { get; set; } = string.Empty;

        // User Specs
        public string UserRamText { get; set; } = string.Empty;
        public string UserCpuName { get; set; } = string.Empty;
        public string UserGpuName { get; set; } = string.Empty;

        // Game Requirements (Parsed from Steam API pc_requirements)
        public string GameReqRam { get; set; } = "Not specified";
        public string GameReqCpu { get; set; } = "Not specified";
        public string GameReqGpu { get; set; } = "Not specified";
    }

    public class HardwareInfoService
    {
        private static SystemHardwareSpecs? _cachedSpecs;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ulTotalPhys;
            public ulong ulAvailPhys;
            public ulong ulTotalPageFile;
            public ulong ulAvailPageFile;
            public ulong ulTotalVirtual;
            public ulong ulAvailVirtual;
            public ulong ulAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        public SystemHardwareSpecs GetSystemSpecs()
        {
            if (_cachedSpecs != null) return _cachedSpecs;

            var specs = new SystemHardwareSpecs();

            // 1. Detect RAM
            try
            {
                var memStatus = new MEMORYSTATUSEX();
                if (GlobalMemoryStatusEx(memStatus))
                {
                    specs.TotalRamGB = memStatus.ulTotalPhys / (1024.0 * 1024.0 * 1024.0);
                }
            }
            catch
            {
                specs.TotalRamGB = 8.0; // Fallback default
            }

            // 2. Detect CPU
            try
            {
                var cpuKey = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null);
                if (cpuKey != null)
                {
                    var rawCpu = cpuKey.ToString()?.Trim() ?? "";
                    specs.CpuName = Regex.Replace(rawCpu, @"\s+", " ");
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(specs.CpuName) || specs.CpuName == "Unknown CPU")
            {
                var envCpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                if (!string.IsNullOrWhiteSpace(envCpu))
                {
                    specs.CpuName = envCpu.Trim();
                }
            }

            // 3. Detect GPU via Registry
            try
            {
                for (int i = 0; i < 10; i++)
                {
                    var subKey = i.ToString("D4");
                    var gpuKey = Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{{4d36e968-e325-11ce-bfc1-08002be10318}}\{subKey}", "DriverDesc", null);
                    if (gpuKey != null)
                    {
                        var gpuName = gpuKey.ToString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(gpuName) &&
                            !gpuName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                            !gpuName.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) &&
                            !gpuName.Contains("Indirect", StringComparison.OrdinalIgnoreCase))
                        {
                            specs.GpuName = gpuName;
                            if (gpuName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                                gpuName.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                                gpuName.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                                gpuName.Contains("Arc", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch { }

            _cachedSpecs = specs;
            return specs;
        }

        public CompatibilityResult EvaluateRequirements(string? requirementsHtml)
        {
            var result = new CompatibilityResult();
            var userSpecs = GetSystemSpecs();

            result.UserRamText = userSpecs.RamFormatted;
            result.UserCpuName = userSpecs.CpuName;
            result.UserGpuName = userSpecs.GpuName;

            if (string.IsNullOrWhiteSpace(requirementsHtml))
            {
                result.IsCompatible = true;
                result.Rating = "Pass";
                result.BadgeColor = "#34D399"; // Green
                result.RecommendationText = $"System Specs: {userSpecs.RamFormatted}";
                result.GameReqRam = "Not specified";
                result.GameReqCpu = "Not specified";
                result.GameReqGpu = "Not specified";
                return result;
            }

            result.MinimumRequirementsText = requirementsHtml;

            // Split into Minimum and Recommended sections
            string minHtml = requirementsHtml;
            string recHtml = "";
            var recMatch = Regex.Match(requirementsHtml, @"<strong>\s*Recommended:?\s*</strong>|RECOMMENDED:", RegexOptions.IgnoreCase);
            if (recMatch.Success)
            {
                minHtml = requirementsHtml.Substring(0, recMatch.Index);
                recHtml = requirementsHtml.Substring(recMatch.Index);
            }

            // Extract Minimum Specs
            string minRamStr = ExtractSpec(minHtml, @"Memory|RAM");
            string minCpuStr = ExtractSpec(minHtml, @"Processor|CPU");
            string minGpuStr = ExtractSpec(minHtml, @"Graphics|Video Card|GPU");

            // Extract Recommended Specs
            string recRamStr = ExtractSpec(recHtml, @"Memory|RAM");
            string recCpuStr = ExtractSpec(recHtml, @"Processor|CPU");
            string recGpuStr = ExtractSpec(recHtml, @"Graphics|Video Card|GPU");

            double reqRam = ParseRamGb(minRamStr);
            if (reqRam <= 0) reqRam = ParseRamGb(requirementsHtml);
            result.RequiredRamGB = reqRam;

            // Format Game Requirements
            result.GameReqRam = !string.IsNullOrWhiteSpace(minRamStr)
                ? (!string.IsNullOrWhiteSpace(recRamStr) && !string.Equals(minRamStr, recRamStr, StringComparison.OrdinalIgnoreCase) ? $"Min: {minRamStr}\nRec: {recRamStr}" : minRamStr)
                : (reqRam > 0 ? $"{reqRam:0} GB RAM" : "4 GB RAM");

            result.GameReqCpu = !string.IsNullOrWhiteSpace(minCpuStr)
                ? (!string.IsNullOrWhiteSpace(recCpuStr) && !string.Equals(minCpuStr, recCpuStr, StringComparison.OrdinalIgnoreCase) ? $"Min: {minCpuStr}\nRec: {recCpuStr}" : minCpuStr)
                : "Standard 64-bit Processor";

            result.GameReqGpu = !string.IsNullOrWhiteSpace(minGpuStr)
                ? (!string.IsNullOrWhiteSpace(recGpuStr) && !string.Equals(minGpuStr, recGpuStr, StringComparison.OrdinalIgnoreCase) ? $"Min: {minGpuStr}\nRec: {recGpuStr}" : minGpuStr)
                : "DirectX 11 Graphics Card";

            if (reqRam > 0)
            {
                if (userSpecs.TotalRamGB >= reqRam)
                {
                    result.IsCompatible = true;
                    result.Rating = "Pass";
                    result.BadgeColor = "#34D399"; // Green
                    result.RecommendationText = $"Your system has {userSpecs.RamFormatted} (Meets {reqRam:0} GB requirement)";
                }
                else if (userSpecs.TotalRamGB >= reqRam * 0.75)
                {
                    result.IsCompatible = true;
                    result.Rating = "Warning";
                    result.BadgeColor = "#F59E0B"; // Orange
                    result.RecommendationText = $"Your system has {userSpecs.RamFormatted} (Below recommended {reqRam:0} GB requirement)";
                }
                else
                {
                    result.IsCompatible = false;
                    result.Rating = "Fail";
                    result.BadgeColor = "#EF4444"; // Red
                    result.RecommendationText = $"Requires {reqRam:0} GB RAM (Your system has {userSpecs.RamFormatted})";
                }
            }
            else
            {
                result.IsCompatible = true;
                result.Rating = "Pass";
                result.BadgeColor = "#34D399"; // Green
                result.RecommendationText = $"System Specs: {userSpecs.RamFormatted}";
            }

            return result;
        }

        private static string ExtractSpec(string html, string labelPattern)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            var match = Regex.Match(html, $@"(?:{labelPattern}):(?:\s*</[^>]+>)*\s*([^<\n\r]+)", RegexOptions.IgnoreCase);
            if (!match.Success) return "";
            var text = match.Groups[1].Value;
            text = Regex.Replace(text, @"<[^>]+>", "").Trim();
            text = System.Net.WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ");
            return text;
        }

        private static double ParseRamGb(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var match = Regex.Match(text, @"(\d+)\s*(GB|MB)", RegexOptions.IgnoreCase);
            if (match.Success && double.TryParse(match.Groups[1].Value, out double val))
            {
                var unit = match.Groups[2].Value.ToUpper();
                return unit == "MB" ? val / 1024.0 : val;
            }
            return 0;
        }
    }
}
