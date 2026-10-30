param(
    [switch]$RunDowngradeOnly,
    # Which downgrade the elevated relaunch must run. The relaunch used to pass only
    # -RunDowngradeOnly, so a Nuclear relaunch silently ran Normal instead.
    [ValidateSet('Normal', 'Nuclear')][string]$DowngradeMethod = 'Normal'
)

# ==========================================
# VERSION
# ==========================================
$AppVersion = '1.0.0'
# Also exposed globally: the diagnostics header is built from a handler, and a
# global survives any scope the handler ends up running in.
$global:AppVersion = $AppVersion

# Where this script lives when it is fetched with "irm <url> | iex".
$ScriptRawUrl = 'https://raw.githubusercontent.com/LarkTheStar/fix-installer/main/install.ps1'

# Piped-in code has to be saved to a file so the GUI can run (and elevate) on its
# own, but the text a script was handed is not reliable: under "irm ... | iex" the
# scriptblock can describe the enclosing command instead of this script. Anything
# written to disk must pass this check first.
function Test-ScriptSourceLooksComplete {
    param([string]$Source)
    if ([string]::IsNullOrWhiteSpace($Source)) { return $false }
    if ($Source.Length -lt 20000) { return $false }
    return ($Source -match 'function Invoke-SteamDowngrade') -and ($Source -match 'Call of Duty MW4 Setup Utility')
}

# Invoke-WebRequest returns Content as a string for text/plain but as a byte array
# for other MIME types, and raw hosts differ, so decode both to script text (and
# drop a leading BOM so it cannot end up doubled when the copy is written).
function ConvertTo-ScriptText {
    param($Content)
    if ($null -eq $Content) { return '' }
    if ($Content -is [byte[]]) {
        try { return ([System.Text.Encoding]::UTF8.GetString($Content)).TrimStart([char]0xFEFF) } catch { return '' }
    }
    return ([string]$Content).TrimStart([char]0xFEFF)
}

# ==========================================
# BOOTSTRAP (own process + STA + a real file)
# ==========================================
# WPF needs an STA thread, and a GUI should own its process life. Two cases need a
# relaunch: started from a file on a non-STA host (PowerShell 7), or piped in with
# "irm ... | iex", where there is no file at all and the console belongs to the
# user's own terminal.
$global:ScriptPath = $PSCommandPath
if ([string]::IsNullOrWhiteSpace($global:ScriptPath)) { $global:ScriptPath = $MyInvocation.MyCommand.Path }
# "iex" run from inside another script still reports that caller's file here, so
# only keep a path whose file really is this utility. Otherwise the app would
# think it lives elsewhere and would relaunch (as administrator) the wrong file.
if (-not [string]::IsNullOrWhiteSpace($global:ScriptPath)) {
    $ownSource = ''
    try { $ownSource = [IO.File]::ReadAllText($global:ScriptPath) } catch { $ownSource = '' }
    if (-not (Test-ScriptSourceLooksComplete $ownSource)) { $global:ScriptPath = '' }
}

$needsOwnProcess = [string]::IsNullOrWhiteSpace($global:ScriptPath)
$needsSta = [System.Threading.Thread]::CurrentThread.ApartmentState -ne 'STA'

if ($needsOwnProcess -or $needsSta) {
    $launchFile = $global:ScriptPath
    if ($needsOwnProcess) {
        # No file to point at. Save a complete copy to a temp file so the GUI can
        # run on its own (and later relaunch itself elevated) and the terminal keeps
        # its prompt. The published copy is fetched first, because the text we were
        # handed can be the wrong thing entirely; the handed text is only a fallback.
        $source = ''
        try { $source = ConvertTo-ScriptText (Invoke-WebRequest -Uri $ScriptRawUrl -UseBasicParsing -TimeoutSec 60).Content } catch { $source = '' }
        if (-not (Test-ScriptSourceLooksComplete $source)) {
            $source = ''
            try { $source = ConvertTo-ScriptText ($MyInvocation.MyCommand.ScriptBlock.ToString()) } catch { }
        }
        if (Test-ScriptSourceLooksComplete $source) {
            $launchFile = Join-Path ([IO.Path]::GetTempPath()) ('codhq-setup-' + [guid]::NewGuid().ToString('N') + '.ps1')
            $source | Out-File -LiteralPath $launchFile -Encoding UTF8
            $global:ScriptPath = $launchFile
        } else {
            Write-Warning 'Could not obtain a complete copy of the setup utility; running it inside this session instead.'
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($launchFile)) {
        # Forward the parameters, otherwise a launch without -STA (double-click, "Run
        # with PowerShell") restarts the GUI and silently forgets the requested work.
        $forwarded = ''
        if ($PSBoundParameters.ContainsKey('RunDowngradeOnly')) { $forwarded += ' -RunDowngradeOnly' }
        if ($PSBoundParameters.ContainsKey('DowngradeMethod')) { $forwarded += " -DowngradeMethod $DowngradeMethod" }
        Start-Process powershell.exe -ArgumentList "-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launchFile`"$forwarded"
        return
    }
    # Could not prepare a relaunch: fall through and try to run here instead.
}

# ==========================================
# HIDE THE CONSOLE WINDOW (GUI ONLY)
# ==========================================
# The utility is a GUI, so the hosting console is hidden as soon as it starts.
# This is best-effort: hosting under Windows Terminal or an IDE uses a virtual
# console that has no window of its own, and nothing breaks when Hide() is a no-op.
function Hide-ConsoleWindow {
    try {
        if (-not ('CodebuffConsoleHost' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CodebuffConsoleHost {
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    public static bool Hide() {
        IntPtr handle = GetConsoleWindow();
        if (handle == IntPtr.Zero) { return false; }
        return ShowWindow(handle, 0); // SW_HIDE
    }
}
'@ -ErrorAction Stop
        }
        [void][CodebuffConsoleHost]::Hide()
    } catch { }
}

# Only hide a console this script actually owns. With "irm ... | iex" the console
# belongs to the user's terminal, where hiding it would make their window vanish.
if (-not [string]::IsNullOrWhiteSpace($global:ScriptPath)) { Hide-ConsoleWindow }

Add-Type -AssemblyName PresentationFramework
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName System.Windows.Forms

# ==========================================
# CONFIGURATION & VARIABLES
# ==========================================
$ErrorActionPreference = 'Stop'
$global:IsProcessing = $false

# $global:ScriptPath was captured in the bootstrap, before any handler can run:
# inside a WPF click handler $MyInvocation.MyCommand.Definition is the handler's own
# code block, not the file path, so elevation must use the bootstrap value.

# Folder chosen in this session (set even if writing the settings file fails).
$global:SessionGameFolder = $null

$AppId = '1938090'
$ReleaseTag = '1.0.0'
$ZipFileName = 'Call.of.Duty.Modern.Warfare.4.fix.v2.zip'
$ZipUrl = "https://github.com/LarkTheStar/fix-installer/releases/download/$ReleaseTag/$ZipFileName"
$LuaDownloadUrl = "https://github.com/LarkTheStar/fix-installer/releases/download/ManifestLua-1.0.0/1938090_25909941.lua"
$ZipDownloadUrl = "https://github.com/LarkTheStar/fix-installer/releases/download/ManifestLua-1.0.0/MW4_25909941_manifests.zip"
$TargetFileName     = "1938090.lua"
$SourceFileName     = "1938090_25909941.lua"
$SteamConfigSubPath = "config\stplug-in"
$AppManifestFile    = "appmanifest_1938090.acf"

# Remembered game folder, so a manual selection is reused next time the utility
# (or a later PowerShell session) starts.
$SettingsDirectory  = Join-Path $env:LOCALAPPDATA 'CallOfDutyHQ-SetupUtility'
$SettingsFile       = Join-Path $SettingsDirectory 'settings.json'

# ==========================================
# THEMES
# ==========================================
# Every semantic colour used by the window, the prompt dialogs, the status pill
# and the activity log lives here. The XAML ships the dark values; switching
# themes replaces the window's brush resources in place, and DynamicResource
# references update live.
$ThemePalettes = @{
    dark = @{
        BgDark = '#191916'; BgSidebar = '#151511'; BgCard = '#22221D'; BgCardHover = '#2C2B24'
        Accent = '#C7A878'; AccentHover = '#D9BB8D'; AccentPressed = '#A88758'
        TextPrimary = '#F3F0E8'; TextSecondary = '#B8B4A8'; TextMuted = '#858176'
        Green = '#92B49A'; Red = '#D77D70'; Yellow = '#D8B66C'; Magenta = '#C6A2B8'
        Border = '#39382F'; InputBg = '#1C1C18'; PopupBg = '#24231E'
        PopupItemHover = '#302F27'; PopupItemSelected = '#39362B'; PopupArrow = '#A8A394'
        ScrollThumb = '#555348'; ScrollThumbHover = '#706D60'
        CardHoverDanger = '#382622'; HintBg = '#1E1E19'
        ButtonBg = '#302F28'; ButtonHoverBg = '#3A3930'; ButtonPressedBg = '#282720'; ButtonBorder = '#48463A'
        SmallButtonBg = '#292820'; SmallButtonText = '#E3DED1'; TextOnAccent = '#211E18'
        StatusReadyBg = '#252D25'; StatusReadyBorder = '#465847'
        StatusBusyBg = '#2C2A22'; StatusBusyBorder = '#57503D'
        StatusFailedBg = '#352522'; StatusFailedBorder = '#60413A'
        StatusCancelledBg = '#332E20'; StatusCancelledBorder = '#5D5235'
    }
    light = @{
        BgDark = '#F3F0E8'; BgSidebar = '#EAE5D9'; BgCard = '#FBF9F3'; BgCardHover = '#F0ECE2'
        Accent = '#8A714A'; AccentHover = '#A18456'; AccentPressed = '#705A3A'
        TextPrimary = '#282720'; TextSecondary = '#625F54'; TextMuted = '#858071'
        Green = '#48765A'; Red = '#A9473C'; Yellow = '#86651F'; Magenta = '#80586F'
        Border = '#D9D3C5'; InputBg = '#F8F5ED'; PopupBg = '#FBF9F3'
        PopupItemHover = '#F0ECE2'; PopupItemSelected = '#E8E0CF'; PopupArrow = '#777263'
        ScrollThumb = '#C6BEAD'; ScrollThumbHover = '#AAA18F'
        CardHoverDanger = '#F5E6E1'; HintBg = '#F7F4EC'
        ButtonBg = '#EEE9DE'; ButtonHoverBg = '#E5DFD2'; ButtonPressedBg = '#DCD5C7'; ButtonBorder = '#D5CEBF'
        SmallButtonBg = '#FBF9F3'; SmallButtonText = '#3D3A31'; TextOnAccent = '#FFFFFF'
        StatusReadyBg = '#E8EFE7'; StatusReadyBorder = '#C7D8C5'
        StatusBusyBg = '#F1EBDD'; StatusBusyBorder = '#DED0AF'
        StatusFailedBg = '#F5E7E2'; StatusFailedBorder = '#E5C7BE'
        StatusCancelledBg = '#F3EEDC'; StatusCancelledBorder = '#DDD2A9'
    }
}

# Accent presets: layered on top of whichever theme is active, so every preset
# works in both dark and light mode. Dark/Light are @(Accent, AccentHover, AccentPressed).
$AccentPresets = [ordered]@{
    Steam   = @{ Label = 'SteamVault gold'; Swatch = '#C7A878'; Dark = @('#C7A878', '#D9BB8D', '#A88758'); Light = @('#8A714A', '#A18456', '#705A3A') }
    Teal    = @{ Label = 'Teal';       Swatch = '#12B5A0'; Dark = @('#12B5A0', '#38D6C1', '#0E8F7F'); Light = @('#0C8C7C', '#11A896', '#076A5E') }
    Violet  = @{ Label = 'Violet';     Swatch = '#8B7BFF'; Dark = @('#8B7BFF', '#A79AFF', '#6F5FE0'); Light = @('#6C57D8', '#8875EA', '#523FBD') }
    Amber   = @{ Label = 'Amber';      Swatch = '#F2A03D'; Dark = @('#F2A03D', '#FFB75E', '#CE8427'); Light = @('#B26C10', '#CC8120', '#8C540B') }
    Crimson = @{ Label = 'Crimson';    Swatch = '#F2586C'; Dark = @('#F2586C', '#FF7387', '#CC4054'); Light = @('#C22F44', '#DB4358', '#9C2338') }
}

$global:ThemeName = 'dark'
$global:AccentName = 'Steam'
$global:ReduceMotion = $false
$global:StatusState = 'Ready'
$global:GameCardState = $null
$global:CurrentPage = 0
$global:LogEntries = [System.Collections.Generic.List[object]]::new()
$global:LogFilter = 'all'
$global:LogAutoScroll = $true
# A long downgrade can out-log the screen many times over, so the transcript is
# capped; Save says so in its header when lines were dropped.
$global:LogMaxLines = 5000
$global:LogTrimmed = $false
$global:LastUiPumpUtc = [DateTime]::MinValue
$global:IsUiPumping = $false
$global:SuppressUiPump = $false
# Set right before a Close() the app itself initiates (the elevation hand-off), so
# the "do not close while working" guard does not block it.
$global:AllowProgrammaticClose = $false
$global:StatusPulseTriggers = $null
# Watchdog for the elevation hand-off (see Start-WorkerWatcher).
$global:WorkerWatcher = $null
$global:WorkerWatcherPid = 0
$global:WorkerWatcherFallbackPid = 0
$global:WorkerWatcherAnnounced = $false
$global:WorkerWatcherMethod = 'Normal'
$global:WorkerWatcherStartedAt = [DateTime]::MinValue

$codDepotIds = "1938091 1938092 1938093 1938094 1938095 1938096 1938097 1938098 1938099 1962660 1962661 1962662 1962663 1962664 1962665 1962666 1962667 1962668 1962669 2014030 2014031 2014032 2014033 2014034 2014035 2014036 2014037 2014038 2014039 2014040 2014041 2014042 2014043 2014044 2014045 2014046 2014047 2014048 2014049 2014050 2014051 2014052 2014053 2014054 2014055 2014056 2014057 2014058 2014059 2014060 2014061 2014062 2014063 2014064 2014065 2014066 2014067 2014068 2014069 2014070 2014071 2014072 2014073 2014074 2014075 2014076 2014077 2014078 2014079 2014080 2014081 2014082 2014083 2014084 2014085 2014086 2014087 2014088 2014089 2014261 2014262 2014263 2014264 2014265 2014266 2014267 2014268 2014269 2021992 2021993 2021994 2021995 2021996 2021997 2021998 2021999 2053672 2053673 2053674 2053675 2053676 2053677 2053678 2053679 2062851 2062852 2062853 2062854 2062855 2062856 2062857 2062858 2062859 2120531 2120532 2120533 2120534 2120535 2120536 2120537 2120538 2120539 2127792 2127793 2127794 2127795 2127796 2127797 2127798 2127799 2134251 2134252 2134253 2134254 2134255 2134256 2134257 2134258 2134259 2137911 2137912 2137913 2137914 2137915 2137916 2137917 2137918 2137919 2139273 2139274 2139275 2139276 2139277 2139278 2139279 2170979 2228971 2228972 2228973 2228974 2228975 2228976 2228977 2228978 2228979 2278281 2278282 2278283 2278284 2278285 2278286 2278287 2278288 2278289 2316851 2519060 2625839 2790671 2790672 2790673 2790674 2790675 2790676 2790677 2790678 2790679 2790690 2790691 2790692 2790693 2790694 2790695 2790696 2790697 2790698 2790699 3180215 3180216 3180217 3180218 3180219 3203381 3203382 3203383 3203384 3203385 3203386 3203387 3203388 3203389 3203391 3203392 3203393 3203394 3203395 3203396 3203397 3203398 3203399 3203401 3203402 3203403 3203404 3203405 3203406 3203407 3203408 3203409 3203411 3203412 3203413 3203414 3203415 3203416 3203417 3203418 3203419 4085076 4085077 4085078 4085079 4085081 4085082 4085083 4085084 4085085 4085086 4085087 4085088 4085089 4085091 4085092 4085093 4085094 4085095 4085096 4085097 4085098 4085099 4085111 4085112 4085113 4085114 4085115 4085116 4085117 4085118 4085119 4085121 4085122 4085123 4085124 4085125 4085126 4085127 4085128 4085129 4085131 4085132 4085133 4085134 4085135 4085136 4085137 4085138 4085139 4098661 4098662 4098663 4098664 4098665 4098666 4098667 4098668 4098669 4435490 4435500 4435510 5092423 5092424 5092425 5092426 5092427 5092428 5092429 5092431 5092432 5092433 5092434 5092435 5092436 5092437 5092438 5092439 5092441 5092442 5092443 5092444 5092445 5092446 5092447 5092448 5092449 5092461 5092462 5092463 5092464 5092465 5092466 5092467 5092468 5092469 5092491 5092492 5092493 5092494 5092495 5092496 5092497 5092498 5092499 5092511 5092512 5092513 5092514 5092515 5092516 5092517 5092518 5092519 5177101 5177102 5177103 5177104 5177105 5177106 5177107 5177108 5177109" -split ' '

# ==========================================
# FUNCTIONS
# ==========================================
function Write-Info([string]$Message) { Write-Host "[INFO] $Message" -ForegroundColor Cyan }
function Write-Warn([string]$Message) { Write-Host "[WARN] $Message" -ForegroundColor Yellow }
function Write-Ok([string]$Message) { Write-Host "[OK]   $Message" -ForegroundColor Green }
function Stop-Installer([string]$Message) { throw $Message }

# A user-driven cancel is not a failure. It is reported neutrally by the GUI so
# answering "No" never looks like a crash.
function Stop-Cancelled([string]$Message = 'Installation cancelled.') { throw [System.OperationCanceledException]::new($Message) }

# Stops a Windows service without letting sc.exe write to the real console.
# A service that is already stopped makes sc print "ControlService FAILED 1062",
# which used to leak into a visible PowerShell window; output is captured and
# discarded here, and failures are non-fatal just like before.
function Stop-ServiceQuietly {
    param([Parameter(Mandatory)][string]$Name)
    try {
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = Join-Path $env:SystemRoot 'System32\sc.exe'
        $startInfo.Arguments = "stop `"$Name`""
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $process = [System.Diagnostics.Process]::Start($startInfo)
        try {
            $null = $process.StandardOutput.ReadToEnd()
            $null = $process.StandardError.ReadToEnd()
            $process.WaitForExit()
        } finally { $process.Dispose() }
    } catch { }
}

function Add-UniquePath {
    param([System.Collections.Generic.List[string]]$List, [string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $expanded = [Environment]::ExpandEnvironmentVariables($Path.Trim().Trim('"'))
    if (Test-Path -LiteralPath $expanded -PathType Container) {
        $full = [IO.Path]::GetFullPath($expanded).TrimEnd('\')
        foreach ($existing in $List) { if ($existing.Equals($full, [StringComparison]::OrdinalIgnoreCase)) { return } }
        $List.Add($full)
    }
}

function Get-SteamLibraries {
    $libraries = [System.Collections.Generic.List[string]]::new()
    $registryKeys = @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')
    foreach ($key in $registryKeys) {
        try {
            $item = Get-ItemProperty -LiteralPath $key -ErrorAction Stop
            if ($item.SteamPath) { Add-UniquePath $libraries $item.SteamPath }
            if ($item.InstallPath) { Add-UniquePath $libraries $item.InstallPath }
        } catch {}
    }
    Add-UniquePath $libraries "${env:ProgramFiles(x86)}\Steam"
    Add-UniquePath $libraries "$env:ProgramFiles\Steam"
    $steamRoots = @($libraries.ToArray())
    foreach ($steamRoot in $steamRoots) {
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf -PathType Leaf) {
            $contents = Get-Content -LiteralPath $vdf -Raw
            foreach ($match in [regex]::Matches($contents, '"path"\s*"([^"]+)"')) {
                $libraryPath = $match.Groups[1].Value -replace '\\\\', '\'
                Add-UniquePath $libraries $libraryPath
            }
        }
    }
    return $libraries.ToArray()
}

function Find-CallOfDutyHQ {
    $matches = [System.Collections.Generic.List[string]]::new()
    foreach ($library in (Get-SteamLibraries)) {
        $manifest = Join-Path $library "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { continue }
        $contents = Get-Content -LiteralPath $manifest -Raw
        $installMatch = [regex]::Match($contents, '"installdir"\s*"([^"]+)"')
        if (-not $installMatch.Success) { continue }
        $installDir = $installMatch.Groups[1].Value
        if ([IO.Path]::IsPathRooted($installDir) -or $installDir.Contains('..') -or $installDir.Contains(':')) { continue }
        $candidate = [IO.Path]::GetFullPath((Join-Path $library "steamapps\common\$installDir"))
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            if (-not ($matches | Where-Object { $_.Equals($candidate, [StringComparison]::OrdinalIgnoreCase) })) { $matches.Add($candidate) }
        }
    }
    return $matches.ToArray()
}

# ------------------------------------------
# Settings (game folder, theme, window state)
# ------------------------------------------
function New-DefaultSettings {
    [pscustomobject]@{
        GameFolder = ''
        Theme = 'dark'
        Accent = 'Steam'
        ReduceMotion = $false
        WindowWidth = 0
        WindowHeight = 0
        WindowLeft = 0
        WindowTop = 0
        WindowMaximized = $false
        LastPage = 0
        SavedAt = ''
        AppId = $AppId
    }
}

function Get-AppSettings {
    $settings = New-DefaultSettings
    try {
        if (Test-Path -LiteralPath $SettingsFile -PathType Leaf) {
            $saved = Get-Content -LiteralPath $SettingsFile -Raw | ConvertFrom-Json
            foreach ($name in @('GameFolder', 'Theme', 'Accent', 'ReduceMotion', 'WindowWidth', 'WindowHeight', 'WindowLeft', 'WindowTop', 'WindowMaximized', 'LastPage')) {
                $property = $saved.PSObject.Properties[$name]
                if ($null -ne $property -and $null -ne $property.Value) { $settings.$name = $property.Value }
            }
        }
    } catch { }
    return $settings
}

function Save-AppSettings {
    param([Parameter(Mandatory)]$Settings)
    $tempFile = $null
    try {
        if (-not (Test-Path -LiteralPath $SettingsDirectory -PathType Container)) { $null = New-Item -ItemType Directory -Path $SettingsDirectory -Force }
        $Settings.SavedAt = (Get-Date).ToString('o')
        $Settings.AppId = $AppId
        $tempFile = "$SettingsFile.$([guid]::NewGuid().ToString('N')).tmp"
        [IO.File]::WriteAllText($tempFile, ($Settings | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tempFile -Destination $SettingsFile -Force -ErrorAction Stop
        return $true
    } catch {
        if ($tempFile) { Remove-Item -LiteralPath $tempFile -Force -ErrorAction SilentlyContinue }
        Write-Warn "Could not save settings: $($_.Exception.Message)"
        return $false
    }
}

# Returns the remembered game folder, or $null when there is no valid saved one.
function Get-SavedGameFolder {
    try {
        $folder = [string](Get-AppSettings).GameFolder
        if ([string]::IsNullOrWhiteSpace($folder)) { return $null }
        $folder = [IO.Path]::GetFullPath($folder.Trim().Trim('"')).TrimEnd('\')
        if (Test-Path -LiteralPath $folder -PathType Container) { return $folder }
    } catch { }
    return $null
}

# Persists the selected game folder for later runs. Returns $true on success.
function Save-GameFolder {
    param([Parameter(Mandatory)][string]$Folder)
    try {
        $folder = [IO.Path]::GetFullPath($Folder.Trim().Trim('"')).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $folder -PathType Container)) { throw "The folder does not exist: $folder" }
        $settings = Get-AppSettings
        $settings.GameFolder = $folder
        if (Save-AppSettings -Settings $settings) {
            $global:SessionGameFolder = $folder
            return $true
        }
        return $false
    } catch {
        Write-Warn "Could not save the game folder: $($_.Exception.Message)"
        return $false
    }
}

function Get-SavedTheme {
    $theme = [string](Get-AppSettings).Theme
    if ($ThemePalettes.ContainsKey($theme)) { return $theme }
    return 'dark'
}

function Save-ThemeChoice {
    param([string]$Name)
    if (-not $ThemePalettes.ContainsKey($Name)) { return $false }
    $settings = Get-AppSettings
    $settings.Theme = $Name
    return (Save-AppSettings -Settings $settings)
}

function Get-SavedAccent {
    $accent = [string](Get-AppSettings).Accent
    if ($AccentPresets.Contains($accent)) { return $accent }
    return 'Steam'
}

function Save-AccentChoice {
    param([string]$Name)
    if (-not $AccentPresets.Contains($Name)) { return $false }
    $settings = Get-AppSettings
    $settings.Accent = $Name
    return (Save-AppSettings -Settings $settings)
}

function Get-SavedMotionPreference {
    try { return [bool](Get-AppSettings).ReduceMotion } catch { return $false }
}

function Save-MotionChoice {
    param([bool]$Reduce)
    $settings = Get-AppSettings
    $settings.ReduceMotion = $Reduce
    return (Save-AppSettings -Settings $settings)
}

function Save-WindowPreferences {
    param([int]$Page, [double]$Width, [double]$Height, [double]$Left, [double]$Top, [bool]$Maximized)
    $settings = Get-AppSettings
    $settings.LastPage = $Page
    $settings.WindowMaximized = $Maximized
    if (-not $Maximized -and $Width -gt 0 -and $Height -gt 0) {
        $settings.WindowWidth = $Width
        $settings.WindowHeight = $Height
        $settings.WindowLeft = $Left
        $settings.WindowTop = $Top
    }
    return (Save-AppSettings -Settings $settings)
}

# ------------------------------------------
# Theme colour lookup
# ------------------------------------------
function Get-ThemeColor {
    param([string]$Key)
    $palette = $ThemePalettes[$global:ThemeName]
    if ($null -eq $palette) { $palette = $ThemePalettes['dark'] }
    if ($palette.ContainsKey($Key)) { return [string]$palette[$Key] }
    return '#FF00FF'
}

function Get-ThemeBrush {
    param([string]$Key)
    return [System.Windows.Media.BrushConverter]::new().ConvertFromString((Get-ThemeColor $Key))
}

# Older call sites pass the original dark palette hex values; map them to semantic
# keys so the activity log stays readable in both themes.
$LogColorAliases = @{
    '#A0A0B0' = 'TextSecondary'
    '#606070' = 'TextMuted'
    '#FFFFFF' = 'TextPrimary'
    '#4EC9B0' = 'Green'
    '#FF4D4D' = 'Red'
    '#FFD700' = 'Yellow'
    '#00A8FF' = 'Accent'
    '#33BFFF' = 'AccentHover'
    '#C586C0' = 'Magenta'
    '#B5CEA8' = 'Yellow'
}

function Resolve-ThemeColor {
    param([string]$Color)
    if ([string]::IsNullOrWhiteSpace($Color)) { return (Get-ThemeColor 'TextSecondary') }
    $key = $Color.ToUpperInvariant()
    if ($LogColorAliases.ContainsKey($key)) { return (Get-ThemeColor $LogColorAliases[$key]) }
    return $Color
}

# ------------------------------------------
# Game folder shape check (used for manual picks)
# ------------------------------------------
# Best-effort check for files a Call of Duty HQ install normally contains. It does
# not inspect Steam manifests or block anything: it only produces a warning the
# user can override.
function Test-GameFolder {
    param([Parameter(Mandatory)][string]$Folder)
    $checks = @(
        @{ Relative = '!start_sp26.bat'; Label = '!start_sp26.bat' },
        @{ Relative = 'sp26\steam_settings\configs.user.ini'; Label = 'sp26\steam_settings\configs.user.ini' },
        @{ Relative = 'sp26'; Label = 'sp26 folder' },
        @{ Relative = 'cod.exe'; Label = 'cod.exe' },
        @{ Relative = 'steam_api64.dll'; Label = 'steam_api64.dll' }
    )
    $found = [System.Collections.Generic.List[string]]::new()
    $missing = [System.Collections.Generic.List[string]]::new()
    $full = $Folder
    try { $full = [IO.Path]::GetFullPath($Folder).TrimEnd('\') } catch { }
    foreach ($check in $checks) {
        if (Test-Path -LiteralPath (Join-Path $full $check.Relative)) { $found.Add($check.Label) }
        else { $missing.Add($check.Label) }
    }
    $leaf = ''
    try { $leaf = Split-Path -Leaf $full } catch { }
    $looksValid = ($found.Count -gt 0) -or ($leaf -eq 'Call of Duty HQ')
    return [pscustomobject]@{
        Folder = $full
        LooksValid = $looksValid
        Found = @($found.ToArray())
        Missing = @($missing.ToArray())
        Leaf = $leaf
    }
}

function Select-TargetFolder {
    # A folder the user picked earlier wins over detection, so the prompt only
    # appears when nothing has been saved yet (or the saved folder is gone).
    if ($global:SessionGameFolder -and (Test-Path -LiteralPath $global:SessionGameFolder -PathType Container)) {
        Write-Info 'Using your selected game folder:'
        Write-Host "  $global:SessionGameFolder"
        return $global:SessionGameFolder
    }
    $saved = Get-SavedGameFolder
    if ($saved) {
        Write-Info 'Using your saved game folder:'
        Write-Host "  $saved"
        return $saved
    }

    $found = @(Find-CallOfDutyHQ)
    if ($found.Count -eq 1) {
        $target = $found[0]
        Write-Info "Detected Steam Call of Duty HQ installation:"
        Write-Host "  $target"
        $answer = Read-Host "Detected path:`n$target`n`nUse this folder? [Y/n]"
        if ([string]::IsNullOrWhiteSpace($answer) -or $answer -match '^(y|yes)$') {
            $null = Save-GameFolder -Folder $target
            return $target
        }
    } elseif ($found.Count -gt 1) {
        Write-Info 'Multiple Call of Duty HQ installations were found:'
        for ($i = 0; $i -lt $found.Count; $i++) { Write-Host "  [$($i + 1)] $($found[$i])" }
        while ($true) {
            $choice = Read-Host "Choose a folder (1-$($found.Count)), or enter a different path"
            if ([string]::IsNullOrWhiteSpace($choice)) { Stop-Cancelled 'Installation cancelled.' }
            $number = 0
            if ([int]::TryParse($choice, [ref]$number) -and $number -ge 1 -and $number -le $found.Count) {
                $null = Save-GameFolder -Folder $found[$number - 1]
                return $found[$number - 1]
            }
            if (Test-Path -LiteralPath $choice -PathType Container) {
                $chosen = [IO.Path]::GetFullPath($choice)
                $null = Save-GameFolder -Folder $chosen
                return $chosen
            }
            Write-Warn 'That selection is not a valid folder.'
        }
    } else {
        Write-Warn "Could not automatically locate appmanifest_$AppId.acf and its install folder."
        Write-Info 'Pick your Call of Duty HQ folder in the window that opens; it will be remembered.'
    }

    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = "Select the Call of Duty HQ game folder"
    $dialog.ShowNewFolderButton = $false
    
    if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
        $full = [IO.Path]::GetFullPath($dialog.SelectedPath)
        $layout = Test-GameFolder -Folder $full
        if (-not $layout.LooksValid) {
            $missing = ($layout.Missing -join ', ')
            $confirm = Read-Host "This folder doesn't look like Call of Duty HQ:`n$full`n`nNone of these were found: $missing`n`nUse it anyway? [y/N]"
            if ($confirm -notmatch '^(y|yes)$') { Stop-Cancelled 'Installation cancelled.' }
        }
        $null = Save-GameFolder -Folder $full
        return $full
    } else {
        Stop-Cancelled 'Installation cancelled.'
    }
}

function Get-ValidatedRelativePath([string]$EntryName) {
    $name = $EntryName -replace '/', '\'
    if ([string]::IsNullOrWhiteSpace($name)) { Stop-Installer 'ZIP contains an empty path.' }
    if ($name.StartsWith('\') -or [IO.Path]::IsPathRooted($name) -or $name -match '^[A-Za-z]:') { Stop-Installer "ZIP contains an absolute path: $EntryName" }
    $segments = $name.Split('\')
    foreach ($segment in $segments) {
        if ($segment -eq '..' -or $segment -eq '.' -or $segment.Contains(':') -or $segment.EndsWith(' ') -or $segment.EndsWith('.')) { Stop-Installer "ZIP contains an unsafe path: $EntryName" }
        if ($segment -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)') { Stop-Installer "ZIP contains a reserved Windows filename: $EntryName" }
    }
    if ($name -match '^(?i:og files|language files)(\\|$)') { Stop-Installer "ZIP attempts to write into the reserved backup folder: $EntryName" }
    return $name
}

function Download-FileWithProgress {
    param([Parameter(Mandatory)][string]$Url, [Parameter(Mandatory)][string]$Destination, [Parameter(Mandatory)][string]$Label)
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch {}
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $true
    $request.UserAgent = 'CallOfDutyHQ-SetupUtility/1.1'
    $response = $null; $inputStream = $null; $outputStream = $null
    try {
        $response = $request.GetResponse()
        $total = [int64]$response.ContentLength
        $inputStream = $response.GetResponseStream()
        $outputStream = [System.IO.File]::Open($Destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $buffer = New-Object byte[] (1024 * 1024)
        [int64]$downloaded = 0; $lastPercent = -1
        while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $outputStream.Write($buffer, 0, $read)
            $downloaded += $read
            if ($total -gt 0) {
                $percent = [int][Math]::Min(100, [Math]::Floor(($downloaded * 100.0) / $total))
                $downloadedMB = [Math]::Round($downloaded / 1MB, 1)
                $totalMB = [Math]::Round($total / 1MB, 1)
                if ($percent -ne $lastPercent) { Write-Progress -Activity $Label -Status "$downloadedMB MB of $totalMB MB ($percent%)" -PercentComplete $percent; $lastPercent = $percent }
            } else { Write-Progress -Activity $Label -Status ("Downloaded {0:N1} MB" -f ($downloaded / 1MB)) -PercentComplete 0 }
        }
        $outputStream.Flush()
        if ($downloaded -le 0) { throw 'The server returned an empty download.' }
        if ($total -gt 0 -and $downloaded -ne $total) { throw "Download appears incomplete: received $downloaded of $total bytes." }
        Write-Progress -Activity $Label -Completed
        Write-Ok ("Download complete: {0:N1} MB" -f ($downloaded / 1MB))
    } catch {
        Write-Progress -Activity $Label -Completed
        if (Test-Path -LiteralPath $Destination -PathType Leaf) { Remove-Item -LiteralPath $Destination -Force -ErrorAction SilentlyContinue }
        throw "Download failed: $($_.Exception.Message)"
    } finally {
        if ($outputStream) { $outputStream.Dispose() }
        if ($inputStream) { $inputStream.Dispose() }
        if ($response) { $response.Dispose() }
    }
}

function Install-Fix {
    $workRoot = Join-Path $env:TEMP ("codhq-fix-installer-" + [guid]::NewGuid().ToString('N'))
    $zipPath = Join-Path $workRoot $ZipFileName
    $stageRoot = Join-Path $workRoot 'staged'
    $archive = $null
    try {
        Write-Host ''
        Write-Host 'SteamVault - Call of Duty HQ Fix Installer' -ForegroundColor White
        Write-Host 'This installer will replace matching files in the selected game folder.' -ForegroundColor White
        Write-Host ''
        $targetRoot = Select-TargetFolder
        $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
        if (-not (Test-Path -LiteralPath $targetRoot -PathType Container)) { Stop-Installer "Target folder does not exist: $targetRoot" }
        Write-Host ''
        Write-Host "Target folder: $targetRoot" -ForegroundColor Yellow
        Write-Warn 'Existing files with matching names will be overwritten. Replaced originals will be backed up in a timestamped "og files" folder inside the game directory.'
        $proceed = Read-Host 'Continue? [y/N]'
        if ($proceed -notmatch '^(y|yes)$') { Stop-Cancelled 'Installation cancelled.' }
        
        # NEW: Kill game processes and stop anti-cheat services to release file locks
        Write-Info "Closing running game processes and anti-cheat services to release file locks..."
        $services = @("randgrid", "EasyAntiCheat", "EasyAntiCheat_EOS", "BEService")
        foreach ($svc in $services) {
            $service = Get-Service -Name $svc -ErrorAction SilentlyContinue
            # Only ask to stop services that are actually running, and keep sc.exe's
            # output out of any console window that happens to be visible.
            if ($service -and $service.Status -ne 'Stopped') { Stop-ServiceQuietly -Name $svc }
        }
        $gameProcs = @("cod", "modernwarfare", "randgrid", "easyanticheat")
        foreach ($proc in $gameProcs) {
            Get-Process -Name $proc -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Seconds 3

        New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
        New-Item -ItemType Directory -Path $stageRoot -Force | Out-Null
        Write-Info "Downloading release asset $ZipFileName ..."
        Download-FileWithProgress -Url $ZipUrl -Destination $zipPath -Label "Downloading fix: $ZipFileName"
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or (Get-Item -LiteralPath $zipPath).Length -le 0) { Stop-Installer 'The ZIP download is missing or empty.' }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        if ($archive.Entries.Count -eq 0) { Stop-Installer 'The downloaded ZIP is empty.' }
        if ($archive.Entries.Count -gt 20000) { Stop-Installer 'The ZIP contains an unexpectedly large number of entries; refusing to continue.' }
        $planned = [System.Collections.Generic.List[object]]::new()
        $totalUncompressed = [int64]0
        foreach ($entry in $archive.Entries) {
            $relative = Get-ValidatedRelativePath $entry.FullName
            $isDirectory = $entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')
            if ($isDirectory) { continue }
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000) { Stop-Installer "ZIP contains a symbolic link, which is not allowed: $($entry.FullName)" }
            $totalUncompressed += [int64]$entry.Length
            if ($totalUncompressed -gt 5GB) { Stop-Installer 'The ZIP expands to more than 5 GB; refusing to continue.' }
            $stageRootFull = [IO.Path]::GetFullPath($stageRoot).TrimEnd([char[]]@('\', '/'))
            $stagePath = [IO.Path]::GetFullPath((Join-Path $stageRootFull $relative))
            $stagePrefix = $stageRootFull + [IO.Path]::DirectorySeparatorChar
            if (-not $stagePath.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { Stop-Installer "ZIP path escapes the staging folder: $($entry.FullName)" }
            $targetRootFull = [IO.Path]::GetFullPath($targetRoot).TrimEnd([char[]]@('\', '/'))
            $targetPath = [IO.Path]::GetFullPath((Join-Path $targetRootFull $relative))
            $targetPrefix = $targetRootFull + [IO.Path]::DirectorySeparatorChar
            if (-not $targetPath.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) { Stop-Installer "ZIP path escapes the game folder: $($entry.FullName)" }
            $planned.Add([pscustomobject]@{ Relative = $relative; StagePath = $stagePath; TargetPath = $targetPath; Entry = $entry })
        }
        if ($planned.Count -eq 0) { Stop-Installer 'The ZIP contains no files to install.' }
        Write-Info "Checking and staging $($planned.Count) files..."
        foreach ($item in $planned) {
            $parent = Split-Path -Parent $item.StagePath
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
            $inputStream = $item.Entry.Open()
            try {
                $outputStream = [IO.File]::Open($item.StagePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose() }
            } finally { $inputStream.Dispose() }
        }
        $archive.Dispose()
        $archive = $null
        $backupBase = Join-Path $targetRoot 'og files'
        if (Test-Path -LiteralPath $backupBase) {
            $backupBaseItem = Get-Item -LiteralPath $backupBase -Force
            if (-not $backupBaseItem.PSIsContainer -or (($backupBaseItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { Stop-Installer "The reserved backup path is not a normal directory: $backupBase" }
        }
        $backupRoot = Join-Path $backupBase ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
        $createdTargets = [System.Collections.Generic.List[string]]::new()
        $backedUpTargets = [System.Collections.Generic.List[object]]::new()
        try {
            foreach ($item in $planned) {
                $destination = $item.TargetPath
                $parent = Split-Path -Parent $destination
                $cursor = $parent
                while ($cursor -and $cursor.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) {
                    if (Test-Path -LiteralPath $cursor) {
                        $attributes = [IO.File]::GetAttributes($cursor)
                        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Installer "A junction or symbolic link exists in the destination path: $cursor" }
                    }
                    if ($cursor.Equals($targetRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
                    $cursor = Split-Path -Parent $cursor
                }
                if (-not (Test-Path -LiteralPath $parent -PathType Container)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
                if (Test-Path -LiteralPath $destination) {
                    $existingItem = Get-Item -LiteralPath $destination -Force
                    if ($existingItem.PSIsContainer) { Stop-Installer "A destination file conflicts with an existing directory: $destination" }
                    if (($existingItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Stop-Installer "A symbolic link or reparse point exists at the destination file: $destination" }
                    $relative = $item.Relative
                    $backupPath = Join-Path $backupRoot $relative
                    $backupParent = Split-Path -Parent $backupPath
                    if (-not (Test-Path -LiteralPath $backupParent -PathType Container)) { New-Item -ItemType Directory -Path $backupParent -Force | Out-Null }
                    Copy-Item -LiteralPath $destination -Destination $backupPath
                    $backedUpTargets.Add([pscustomobject]@{ Target = $destination; Backup = $backupPath })
                } else { $createdTargets.Add($destination) }
                Copy-Item -LiteralPath $item.StagePath -Destination $destination -Force
            }
        } catch {
            Write-Warn 'Installation failed. Attempting to restore replaced files...'
            foreach ($newFile in $createdTargets) { try { if (Test-Path -LiteralPath $newFile -PathType Leaf) { Remove-Item -LiteralPath $newFile -Force } } catch {} }
            for ($i = $backedUpTargets.Count - 1; $i -ge 0; $i--) {
                $record = $backedUpTargets[$i]
                try { Copy-Item -LiteralPath $record.Backup -Destination $record.Target -Force } catch { Write-Warn "Could not automatically restore: $($record.Target)" }
            }
            throw # Re-throw the exact exception
        }
        Write-Ok 'Installation completed successfully.'
        if ($backedUpTargets.Count -gt 0) {
            Write-Info "Backed up $($backedUpTargets.Count) replaced file(s) to:"
            Write-Host "  $backupRoot"
        } else { Write-Info 'No existing files needed to be backed up.' }
        $launcherPath = Join-Path $targetRoot '!start_sp26.bat'
        Write-Host ''
        if (Test-Path -LiteralPath $launcherPath -PathType Leaf) {
            $launchAnswer = Read-Host 'Installation succeeded. Run !start_sp26.bat now? [Y/n]'
            if ([string]::IsNullOrWhiteSpace($launchAnswer) -or $launchAnswer -match '^(y|yes)$') {
                Write-Info 'Launching !start_sp26.bat ...'
                Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $launcherPath + '"')) -WorkingDirectory $targetRoot
                Write-Ok 'Launch command started.'
            } else { Write-Info 'Launch skipped. You can run !start_sp26.bat from the game folder later.' }
        } else {
            Write-Warn "Installation succeeded, but !start_sp26.bat was not found in the game root:"
            Write-Host "  $launcherPath"
        }
    } finally {
        if ($archive) { try { $archive.Dispose() } catch {} }
        if (Test-Path -LiteralPath $workRoot) { try { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {} }
    }
}

# Native is display-only: it never takes part in selection, download, or state logic.
# Native names below are written as \uXXXX escapes and decoded at startup so this
# file stays pure ASCII. That is not cosmetic: "irm ... | iex" hands PowerShell the
# script as a string, and a UTF-8 byte-order mark at the start of that string makes
# Windows PowerShell 5.1 fail to parse the param block ("Cannot convert value
# "System.String" ... SwitchParameter"), so the utility never started.
function ConvertFrom-UnicodeEscape {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    # Not $text: PowerShell variable names are case-insensitive, so that would be
    # the same variable as $Text and would replace the input with the builder.
    $builder = New-Object System.Text.StringBuilder
    $index = 0
    while ($index -lt $Text.Length) {
        if ($Text[$index] -eq '\' -and ($index + 5) -lt $Text.Length -and $Text[$index + 1] -eq 'u') {
            $code = 0
            if ([int]::TryParse($Text.Substring($index + 2, 4), [Globalization.NumberStyles]::HexNumber, [Globalization.CultureInfo]::InvariantCulture, [ref]$code)) {
                [void]$builder.Append([char]$code)
                $index += 6
                continue
            }
        }
        [void]$builder.Append($Text[$index])
        $index++
    }
    return $builder.ToString()
}

$LanguagePacks = [ordered]@{
    'Arabic' = [pscustomobject]@{ File = 'Arabic.zip'; Value = 'arabic'; Native = (ConvertFrom-UnicodeEscape '\u0627\u0644\u0639\u0631\u0628\u064a\u0629'); Assets = @('Arabic.zip') }
    'French' = [pscustomobject]@{ File = 'French.zip'; Value = 'french'; Native = (ConvertFrom-UnicodeEscape '\u0046\u0072\u0061\u006e\u00e7\u0061\u0069\u0073'); Assets = @('French.zip') }
    'German' = [pscustomobject]@{ File = 'German.zip'; Value = 'german'; Native = 'Deutsch'; Assets = @('German.zip') }
    'Italian' = [pscustomobject]@{ File = 'Italian.zip'; Value = 'italian'; Native = 'Italiano'; Assets = @('Italian.zip') }
    'Japanese' = [pscustomobject]@{ File = 'Japanese.zip'; Value = 'japanese'; Native = (ConvertFrom-UnicodeEscape '\u65e5\u672c\u8a9e'); Assets = @('Japanese.zip.part001', 'Japanese.zip.part002') }
    'Korean' = [pscustomobject]@{ File = 'Korean.zip'; Value = 'koreana'; Native = (ConvertFrom-UnicodeEscape '\ud55c\uad6d\uc5b4'); Assets = @('Korean.zip.part001', 'Korean.zip.part002') }
    'Polish' = [pscustomobject]@{ File = 'Polish.zip'; Value = 'polish'; Native = 'Polski'; Assets = @('Polish.zip') }
    'Portuguese (Brazil)' = [pscustomobject]@{ File = 'Portuguese.-.Brazil.zip'; Value = 'brazilian'; Native = (ConvertFrom-UnicodeEscape '\u0050\u006f\u0072\u0074\u0075\u0067\u0075\u00ea\u0073\u0020\u0028\u0042\u0072\u0061\u0073\u0069\u006c\u0029'); Assets = @('Portuguese.-.Brazil.zip') }
    'Russian' = [pscustomobject]@{ File = 'Russian.zip'; Value = 'russian'; Native = (ConvertFrom-UnicodeEscape '\u0420\u0443\u0441\u0441\u043a\u0438\u0439'); Assets = @('Russian.zip') }
    'Simplified Chinese' = [pscustomobject]@{ File = 'Simplified-Chinese.zip'; Value = 'schinese'; Native = (ConvertFrom-UnicodeEscape '\u7b80\u4f53\u4e2d\u6587'); Assets = @('Simplified-Chinese.zip.part001', 'Simplified-Chinese.zip.part002') }
    'Spanish (Latin America)' = [pscustomobject]@{ File = 'Spanish.-.Latin.America.zip'; Value = 'latam'; Native = (ConvertFrom-UnicodeEscape '\u0045\u0073\u0070\u0061\u00f1\u006f\u006c\u0020\u0028\u004c\u0061\u0074\u0069\u006e\u006f\u0061\u006d\u00e9\u0072\u0069\u0063\u0061\u0029'); Assets = @('Spanish.-.Latin.America.zip') }
    'Spanish (Spain)' = [pscustomobject]@{ File = 'Spanish.-.Spain.zip'; Value = 'spanish'; Native = (ConvertFrom-UnicodeEscape '\u0045\u0073\u0070\u0061\u00f1\u006f\u006c\u0020\u0028\u0045\u0073\u0070\u0061\u00f1\u0061\u0029'); Assets = @('Spanish.-.Spain.zip') }
    'Traditional Chinese' = [pscustomobject]@{ File = 'Traditional.Chinese.zip'; Value = 'tchinese'; Native = (ConvertFrom-UnicodeEscape '\u7e41\u9ad4\u4e2d\u6587'); Assets = @('Traditional.Chinese.zip') }
}

# Friendly label for menus: "English name - native name".
function Get-LanguageLabel {
    param([string]$Name)
    $pack = $LanguagePacks[$Name]
    if ($null -ne $pack -and -not [string]::IsNullOrWhiteSpace([string]$pack.Native)) { return ($Name + ' ' + [char]0x2014 + ' ' + $pack.Native) }
    return $Name
}

function Get-LanguageManagerRoot { param([Parameter(Mandatory)][string]$TargetRoot) return (Join-Path $TargetRoot 'language files\language-pack-manager') }
function Get-LanguageStatePath { param([Parameter(Mandatory)][string]$ManagerRoot) return (Join-Path $ManagerRoot 'state.json') }

function Get-ConfigLanguageValue {
    param([Parameter(Mandatory)][string]$IniPath)
    if (-not (Test-Path -LiteralPath $IniPath -PathType Leaf)) { throw "Configuration file not found: $IniPath" }
    $content = [IO.File]::ReadAllText($IniPath)
    $matches = [regex]::Matches($content, '(?im)^[ \t]*language[ \t]*=[ \t]*(.*?)[ \t]*\r?$')
    if ($matches.Count -ne 1) { throw "Expected exactly one existing language= line in '$IniPath'; found $($matches.Count). The file was not changed." }
    return $matches[0].Groups[1].Value.Trim()
}

function Set-ConfigLanguageValue {
    param([Parameter(Mandatory)][string]$IniPath, [Parameter(Mandatory)][string]$Language)
    if (-not (Test-Path -LiteralPath $IniPath -PathType Leaf)) { throw "Configuration file not found: $IniPath" }
    $content = [IO.File]::ReadAllText($IniPath)
    $matches = [regex]::Matches($content, '(?im)^[ \t]*language[ \t]*=[^\r\n]*')
    if ($matches.Count -ne 1) { throw "Expected exactly one existing language= line in '$IniPath'; found $($matches.Count). The file was not changed." }
    $m = $matches[0]
    $updated = $content.Substring(0, $m.Index) + "language=$Language" + $content.Substring($m.Index + $m.Length)
    $tempIni = "$IniPath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($tempIni, $updated, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tempIni -Destination $IniPath -Force -ErrorAction Stop
    } catch { Remove-Item -LiteralPath $tempIni -Force -ErrorAction SilentlyContinue; throw }
}

function Save-LanguageManagerState {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)][string]$ManagerRoot)
    $statePath = Get-LanguageStatePath -ManagerRoot $ManagerRoot
    $tempPath = "$statePath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $json = $State | ConvertTo-Json -Depth 20
        [IO.File]::WriteAllText($tempPath, $json, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $tempPath -Destination $statePath -Force -ErrorAction Stop
    } catch { Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue; throw }
}

function Get-LanguageManagerState {
    param([Parameter(Mandatory)][string]$TargetRoot, [Parameter(Mandatory)][string]$IniPath, [switch]$Create)
    $managerRoot = Get-LanguageManagerRoot -TargetRoot $TargetRoot
    $statePath = Get-LanguageStatePath -ManagerRoot $managerRoot
    if (Test-Path -LiteralPath $statePath -PathType Leaf) {
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if (-not ([string]$state.TargetRoot).Equals($TargetRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Language manager state belongs to a different game folder: $($state.TargetRoot)" }
        if (-not (Test-Path -LiteralPath ([string]$state.OriginalIniBackup) -PathType Leaf)) { throw "The manager's original configuration backup is missing: $($state.OriginalIniBackup). State was not changed." }
        return $state
    }
    if (-not $Create) { return $null }
    $legacyRoots = @()
    if (Test-Path -LiteralPath $managerRoot -PathType Container) { $legacyRoots += $managerRoot }
    $legacyOldRoot = Join-Path $TargetRoot 'og files\language-pack-manager'
    if (Test-Path -LiteralPath $legacyOldRoot -PathType Container) { $legacyRoots += $legacyOldRoot }
    $legacy = @($legacyRoots | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter 'manifest.json' -File -Recurse -ErrorAction SilentlyContinue })
    if ($legacy.Count -gt 0) { throw "Legacy per-install language records were found under '$managerRoot' or '$legacyOldRoot'. For safety, this manager will not mix them with its new shared-file ledger." }
    $null = New-Item -ItemType Directory -Path $managerRoot -Force
    $originalIniBackup = Join-Path $managerRoot 'configs.user.ini.original.bak'
    if (Test-Path -LiteralPath $originalIniBackup -PathType Leaf) { throw "An original config backup exists but state.json does not: $originalIniBackup. Refusing to guess whether it belongs to this game state." }
    $currentValue = Get-ConfigLanguageValue -IniPath $IniPath
    Copy-Item -LiteralPath $IniPath -Destination $originalIniBackup -ErrorAction Stop
    $state = [pscustomobject]@{ Version = 2; TargetRoot = $TargetRoot; IniPath = $IniPath; OriginalIniBackup = $originalIniBackup; InitialLanguageValue = $currentValue; ActiveValue = $currentValue; Packs = @(); Paths = @() }
    Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
    return $state
}

function Get-ManagerPathState { param($State, [string]$Relative) foreach ($pathState in @($State.Paths)) { if ([string]$pathState.Relative -ieq $Relative) { return $pathState } } return $null }
function Get-ManagerPack { param($State, [string]$LanguageName) foreach ($packState in @($State.Packs)) { if ([string]$packState.Name -ieq $LanguageName) { return $packState } } return $null }

function Copy-FileAtomically {
    param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Destination)
    $parent = Split-Path -Parent $Destination
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) { $null = New-Item -ItemType Directory -Path $parent -Force }
    $temp = "$Destination.$([guid]::NewGuid().ToString('N')).tmp"
    try { Copy-Item -LiteralPath $Source -Destination $temp -ErrorAction Stop; Move-Item -LiteralPath $temp -Destination $Destination -Force -ErrorAction Stop }
    catch { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue; throw }
}

function Test-SafeGameDestination {
    param([Parameter(Mandatory)][string]$TargetRoot, [Parameter(Mandatory)][string]$Destination)
    $parent = Split-Path -Parent $Destination
    $cursor = $parent
    while ($cursor -and $cursor.StartsWith($TargetRoot, [StringComparison]::OrdinalIgnoreCase)) {
        if (Test-Path -LiteralPath $cursor) {
            $attrs = [IO.File]::GetAttributes($cursor)
            if (($attrs -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "A junction/symlink exists in the destination path: $cursor" }
        }
        if ($cursor.Equals($TargetRoot, [StringComparison]::OrdinalIgnoreCase)) { break }
        $cursor = Split-Path -Parent $cursor
    }
    if (Test-Path -LiteralPath $Destination) {
        $item = Get-Item -LiteralPath $Destination -Force
        if ($item.PSIsContainer -or (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "Destination is a directory or reparse point; refusing to alter it: $Destination" }
    }
}

function Download-LanguageArchive {
    param([Parameter(Mandatory)]$Pack, [Parameter(Mandatory)][string]$WorkRoot, [Parameter(Mandatory)][string]$LanguageName)
    $baseUrl = 'https://github.com/LarkTheStar/fix-installer/releases/download/Language-1.0.0'
    $localAssets = [System.Collections.Generic.List[string]]::new()
    $assetIndex = 0
    $assetCount = @($Pack.Assets).Count
    foreach ($assetName in @($Pack.Assets)) {
        $assetIndex++
        $destination = Join-Path $WorkRoot $assetName
        $url = "$baseUrl/$assetName"
        Write-Info "Downloading asset $assetIndex/${assetCount}: $assetName"
        Download-FileWithProgress -Url $url -Destination $destination -Label "Downloading $LanguageName - $assetName"
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf) -or (Get-Item -LiteralPath $destination).Length -le 0) { throw "Downloaded asset is missing or empty: $assetName" }
        $localAssets.Add($destination)
    }
    if ($localAssets.Count -eq 1) { return $localAssets[0] }
    $assembled = Join-Path $WorkRoot $Pack.File
    $out = [IO.File]::Open($assembled, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        foreach ($partPath in $localAssets) { $partStream = [IO.File]::OpenRead($partPath); try { $partStream.CopyTo($out) } finally { $partStream.Dispose() } }
        $out.Flush()
    } finally { $out.Dispose() }
    Write-Ok ("Reassembled {0} from {1} ordered parts ({2:N2} GiB)." -f $Pack.File, $localAssets.Count, ((Get-Item -LiteralPath $assembled).Length / 1GB))
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $testArchive = $null
    try {
        $testArchive = [IO.Compression.ZipFile]::OpenRead($assembled)
        if ($testArchive.Entries.Count -eq 0) { throw 'The reassembled ZIP contains no entries.' }
    } catch { throw "Reassembled ZIP failed its initial open test: $($_.Exception.Message)" }
    finally { if ($testArchive) { $testArchive.Dispose() } }
    foreach ($partPath in $localAssets) { Remove-Item -LiteralPath $partPath -Force -ErrorAction Stop }
    Write-Info 'Temporary split parts were removed after successful reassembly; the reconstructed ZIP is retained until staging finishes.'
    return $assembled
}

function Install-LanguagePack {
    $targetRoot = Select-TargetFolder
    $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
    $iniPath = Join-Path $targetRoot 'sp26\steam_settings\configs.user.ini'
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { throw "Could not find configs.user.ini at '$iniPath'. No language files were installed." }
    Write-Host ''
    Write-Host 'Available language packs:' -ForegroundColor White
    $names = @($LanguagePacks.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) { Write-Host "  [$($i + 1)] $(Get-LanguageLabel $names[$i])" }
    Write-Host '  [0] Cancel'
    $choice = Read-Host 'Choose a language to install'
    if ($choice -eq '0' -or [string]::IsNullOrWhiteSpace($choice)) { Write-Info 'Language installation cancelled.'; return }
    $number = 0
    if (-not [int]::TryParse($choice, [ref]$number) -or $number -lt 1 -or $number -gt $names.Count) { throw 'Invalid language selection.' }
    $languageName = $names[$number - 1]
    $pack = $LanguagePacks[$languageName]
    $managerRoot = Get-LanguageManagerRoot -TargetRoot $targetRoot
    $state = Get-LanguageManagerState -TargetRoot $targetRoot -IniPath $iniPath -Create
    if (@($state.Packs).Count -eq 0) {
        $baselineValue = Get-ConfigLanguageValue -IniPath $iniPath
        Copy-Item -LiteralPath $iniPath -Destination ([string]$state.OriginalIniBackup) -Force -ErrorAction Stop
        $state.InitialLanguageValue = $baselineValue
        $state.ActiveValue = $baselineValue
        Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
    }
    if ($null -ne (Get-ManagerPack -State $state -LanguageName $languageName)) { Write-Warn "$languageName is already tracked as installed. Use Active Language Preset to switch to it."; return }
    Write-Host "`nSelected: $languageName (language=$($pack.Value))" -ForegroundColor Yellow
    Write-Info 'Other installed language packs will be retained. Identical shared files will be reference-counted in the manager state.'
    $confirm = Read-Host 'Download and install this language pack? [y/N]'
    if ($confirm -notmatch '^(y|yes)$') { Write-Info 'Language installation cancelled.'; return }
    $workRoot = Join-Path $env:TEMP ('codhq-language-' + [guid]::NewGuid().ToString('N'))
    $stageRoot = Join-Path $workRoot 'staged'
    $archive = $null
    $committed = $false
    $undo = [System.Collections.Generic.List[object]]::new()
    $createdDirs = [System.Collections.Generic.List[string]]::new()
    $beforeIni = Join-Path $workRoot 'configs.user.ini.before-install.bak'
    try {
        $null = New-Item -ItemType Directory -Path $stageRoot -Force
        Copy-Item -LiteralPath $iniPath -Destination $beforeIni -ErrorAction Stop
        $zipPath = Download-LanguageArchive -Pack $pack -WorkRoot $workRoot -LanguageName $languageName
        if (-not (Test-Path -LiteralPath $zipPath -PathType Leaf) -or (Get-Item -LiteralPath $zipPath).Length -le 0) { throw 'The ZIP is missing or empty.' }
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        if ($archive.Entries.Count -eq 0 -or $archive.Entries.Count -gt 20000) { throw 'The ZIP is empty or contains too many entries.' }
        $fileEntries = @($archive.Entries | Where-Object { -not ($_.FullName.EndsWith('/') -or $_.FullName.EndsWith('\')) })
        if ($fileEntries.Count -eq 0) { throw 'The ZIP contains no files.' }
        $firstSegments = @($fileEntries | ForEach-Object { ($_.FullName -replace '\\','/').Split('/')[0] } | Select-Object -Unique)
        $stripRoot = $false
        if ($firstSegments.Count -eq 1 -and @($fileEntries | Where-Object { (($_.FullName -replace '\\','/').Split('/').Count) -lt 2 }).Count -eq 0) { $stripRoot = $true }
        $totalSize = [int64]0
        $planned = [System.Collections.Generic.List[object]]::new()
        $seen = @{}
        $entryIndex = 0
        foreach ($entry in $fileEntries) {
            $entryIndex++
            $entryName = $entry.FullName -replace '/', '\'
            if ([IO.Path]::IsPathRooted($entryName) -or $entryName.StartsWith('\') -or $entryName -match '^[A-Za-z]:' -or $entryName.Split('\') -contains '..') { throw "Unsafe path in ZIP: $($entry.FullName)" }
            $relative = $entryName
            if ($stripRoot) {
                $segments = $relative.Split('\')
                if ($segments.Count -lt 2) { throw "Unexpected archive structure: $($entry.FullName)" }
                $relative = ($segments | Select-Object -Skip 1) -join '\'
            }
            $badSegments = @($relative.Split('\') | Where-Object { $_ -in @('', '.', '..') -or $_.Contains(':') -or $_.EndsWith(' ') -or $_.EndsWith('.') })
            if ([string]::IsNullOrWhiteSpace($relative) -or $badSegments.Count -gt 0) { throw "Unsafe path in ZIP: $($entry.FullName)" }
            if ($relative -match '^(?i:og files|language files)(\\|$)') { throw "ZIP tries to write into the reserved manager/backup area: $($entry.FullName)" }
            if ($relative -match '(^|\\)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)') { throw "ZIP contains a reserved Windows filename: $($entry.FullName)" }
            $key = $relative.ToLowerInvariant()
            if ($seen.ContainsKey($key)) { throw "ZIP has duplicate/case-colliding paths: $relative" }
            $seen[$key] = $true
            $unixMode = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixMode -eq 0xA000) { throw "Symbolic links are not allowed: $($entry.FullName)" }
            $totalSize += [int64]$entry.Length
            if ($totalSize -gt 5GB) { throw 'The ZIP expands to more than 5 GB; refusing to continue.' }
            $stagePath = [IO.Path]::GetFullPath((Join-Path $stageRoot $relative))
            $stagePrefix = [IO.Path]::GetFullPath($stageRoot).TrimEnd('\') + '\'
            if (-not $stagePath.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "ZIP path escapes staging: $($entry.FullName)" }
            $targetPath = [IO.Path]::GetFullPath((Join-Path $targetRoot $relative))
            $targetPrefix = $targetRoot.TrimEnd('\') + '\'
            if (-not $targetPath.StartsWith($targetPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "ZIP path escapes game folder: $($entry.FullName)" }
            $planned.Add([pscustomobject]@{ Relative=$relative; StagePath=$stagePath; TargetPath=$targetPath; Entry=$entry })
        }
        Write-Info "Validating and staging $($planned.Count) files..."
        $stageIndex = 0
        foreach ($item in $planned) {
            $stageIndex++
            $parent = Split-Path -Parent $item.StagePath
            $null = New-Item -ItemType Directory -Path $parent -Force
            $entryStream = $item.Entry.Open()
            try {
                $output = [IO.File]::Open($item.StagePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $entryStream.CopyTo($output) } finally { $output.Dispose() }
            } finally { $entryStream.Dispose() }
            $actualLength = (Get-Item -LiteralPath $item.StagePath).Length
            if ([int64]$actualLength -ne [int64]$item.Entry.Length) { throw "Entry length mismatch while extracting: $($item.Relative)" }
            $item | Add-Member -NotePropertyName SHA256 -NotePropertyValue ((Get-FileHash -LiteralPath $item.StagePath -Algorithm SHA256).Hash.ToLowerInvariant())
            Write-Progress -Activity "Staging $languageName" -Status "$stageIndex / $($planned.Count) files" -PercentComplete ([int](100 * $stageIndex / [Math]::Max(1,$planned.Count)))
        }
        Write-Progress -Activity "Staging $languageName" -Completed
        $archive.Dispose(); $archive = $null
        $archiveHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $newPaths = [System.Collections.Generic.List[object]]::new()
        foreach ($existingPathState in @($state.Paths)) { $newPaths.Add($existingPathState) }
        $pathIndexByKey = @{}
        for ($i = 0; $i -lt $newPaths.Count; $i++) { $existingKey = ([string]$newPaths[$i].Relative).ToLowerInvariant(); $pathIndexByKey[$existingKey] = $i }
        $toInstall = [System.Collections.Generic.List[object]]::new()
        $originalFilesRoot = Join-Path $managerRoot 'original-files'
        foreach ($item in $planned) {
            Test-SafeGameDestination -TargetRoot $targetRoot -Destination $item.TargetPath
            $key = $item.Relative.ToLowerInvariant()
            if ($pathIndexByKey.ContainsKey($key)) {
                $idx = [int]$pathIndexByKey[$key]
                $prior = $newPaths[$idx]
                if ([string]$prior.Hash -ne [string]$item.SHA256) { throw "This pack's file differs from a file already managed at '$($item.Relative)'. Nothing was committed, to avoid corrupting a shared path." }
                if (-not (Test-Path -LiteralPath $item.TargetPath -PathType Leaf)) { throw "Managed shared file is missing: $($item.TargetPath)" }
                $currentHash = (Get-FileHash -LiteralPath $item.TargetPath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($currentHash -ne [string]$prior.Hash) { throw "Managed shared file was changed outside the manager: $($item.TargetPath). Restore it before installing another pack." }
                $owners = @($prior.Owners)
                if ($owners -notcontains $languageName) { $owners += $languageName }
                $newPaths[$idx] = [pscustomobject]@{ Relative=[string]$prior.Relative; Hash=[string]$prior.Hash; Owners=@($owners); OriginalExisted=[bool]$prior.OriginalExisted; OriginalBackupRelative=[string]$prior.OriginalBackupRelative }
            } else {
                $destExists = Test-Path -LiteralPath $item.TargetPath -PathType Leaf
                $backupRelative = ''
                if ($destExists) { $backupRelative = $item.Relative }
                $newPaths.Add([pscustomobject]@{ Relative=$item.Relative; Hash=$item.SHA256; Owners=@($languageName); OriginalExisted=[bool]$destExists; OriginalBackupRelative=$backupRelative })
                $pathIndexByKey[$key] = $newPaths.Count - 1
                $toInstall.Add($item)
            }
        }
        foreach ($item in $toInstall) {
            $dest = $item.TargetPath
            $destExists = Test-Path -LiteralPath $dest -PathType Leaf
            $backupPath = Join-Path $originalFilesRoot $item.Relative
            if ($destExists) {
                $null = New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force
                Copy-Item -LiteralPath $dest -Destination $backupPath -Force -ErrorAction Stop
            }
            $undo.Add([pscustomobject]@{ Destination=$dest; HadOriginal=$destExists; BackupPath=$backupPath })
            $parent = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
                $walk = $parent; $missingDirs = [System.Collections.Generic.List[string]]::new()
                while ($walk -and -not (Test-Path -LiteralPath $walk)) { $missingDirs.Add($walk); $walk = Split-Path -Parent $walk }
                $null = New-Item -ItemType Directory -Path $parent -Force
                foreach ($dir in $missingDirs) { $createdDirs.Add($dir) }
            }
            Copy-FileAtomically -Source $item.StagePath -Destination $dest
        }
        Set-ConfigLanguageValue -IniPath $iniPath -Language $pack.Value
        $newPacks = [System.Collections.Generic.List[object]]::new()
        foreach ($installedPack in @($state.Packs)) { $newPacks.Add($installedPack) }
        $newPacks.Add([pscustomobject]@{ Name=$languageName; Value=$pack.Value; InstalledAt=(Get-Date).ToString('o'); ArchiveSHA256=$archiveHash; Files=@($planned | ForEach-Object { [string]$_.Relative }) })
        $state.Paths = @($newPaths.ToArray())
        $state.Packs = @($newPacks.ToArray())
        $state.ActiveValue = $pack.Value
        Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
        $committed = $true
        Write-Ok "$languageName language pack installed."
        Write-Info "Active language set to '$($pack.Value)'."
        Write-Info "Installed files owned by this pack: $($planned.Count)."
    } catch {
        Write-Warn 'Language-pack installation failed. Attempting to roll back newly written files and configuration...'
        for ($i = $undo.Count - 1; $i -ge 0; $i--) {
            $record = $undo[$i]
            try {
                if ($record.HadOriginal -and (Test-Path -LiteralPath $record.BackupPath -PathType Leaf)) { Copy-FileAtomically -Source $record.BackupPath -Destination $record.Destination }
                elseif (-not $record.HadOriginal -and (Test-Path -LiteralPath $record.Destination -PathType Leaf)) { Remove-Item -LiteralPath $record.Destination -Force -ErrorAction Stop }
            } catch { Write-Warn "Could not roll back '$($record.Destination)': $($_.Exception.Message)" }
        }
        if (Test-Path -LiteralPath $beforeIni -PathType Leaf) { try { Copy-FileAtomically -Source $beforeIni -Destination $iniPath } catch { Write-Warn "Could not restore pre-install config: $($_.Exception.Message)" } }
        foreach ($dir in @($createdDirs | Sort-Object Length -Descending)) { try { if ((Test-Path -LiteralPath $dir -PathType Container) -and -not (Get-ChildItem -LiteralPath $dir -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $dir -Force } } catch {} }
        throw # Re-throw the exact exception
    } finally {
        if ($archive) { try { $archive.Dispose() } catch {} }
        if (Test-Path -LiteralPath $workRoot) { try { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue } catch {} }
    }
}

function Set-ActiveLanguagePreset {
    $targetRoot = Select-TargetFolder
    $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
    $iniPath = Join-Path $targetRoot 'sp26\steam_settings\configs.user.ini'
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { throw "Could not find configs.user.ini at '$iniPath'." }
    $managerRoot = Get-LanguageManagerRoot -TargetRoot $targetRoot
    $state = Get-LanguageManagerState -TargetRoot $targetRoot -IniPath $iniPath
    $currentValue = Get-ConfigLanguageValue -IniPath $iniPath
    $options = [System.Collections.Generic.List[object]]::new()
    $options.Add([pscustomobject]@{ Name='English (included with the game)'; Value='english' })
    if ($null -ne $state) {
        foreach ($installedPack in @($state.Packs)) { $options.Add([pscustomobject]@{ Name=[string]$installedPack.Name; Value=[string]$installedPack.Value }) }
    } else {
        $legacyRoots = @()
        if (Test-Path -LiteralPath $managerRoot -PathType Container) { $legacyRoots += $managerRoot }
        $legacyOldRoot = Join-Path $targetRoot 'og files\language-pack-manager'
        if (Test-Path -LiteralPath $legacyOldRoot -PathType Container) { $legacyRoots += $legacyOldRoot }
        $legacy = @($legacyRoots | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter 'manifest.json' -File -Recurse -ErrorAction SilentlyContinue })
        if ($legacy.Count -gt 0) { throw "Legacy language-pack records exist under '$managerRoot' or '$legacyOldRoot'. Remove/restore them before switching presets with this manager." }
    }
    Write-Host ''
    Write-Host 'Active Language Preset' -ForegroundColor White
    Write-Host "Current configs.user.ini value: $currentValue"
    for ($i=0; $i -lt $options.Count; $i++) { Write-Host "  [$($i+1)] $(Get-LanguageLabel $options[$i].Name) (language=$($options[$i].Value))" }
    Write-Host '  [0] Cancel'
    $choice = Read-Host 'Choose the active language preset'
    if ($choice -eq '0' -or [string]::IsNullOrWhiteSpace($choice)) { Write-Info 'Preset change cancelled.'; return }
    $number=0
    if (-not [int]::TryParse($choice,[ref]$number) -or $number -lt 1 -or $number -gt $options.Count) { throw 'Invalid preset selection.' }
    $selected = $options[$number-1]
    if ($currentValue -eq [string]$selected.Value) {
        if (($null -ne $state) -and ([string]$state.ActiveValue -ne [string]$selected.Value)) { $state.ActiveValue = [string]$selected.Value; Save-LanguageManagerState -State $state -ManagerRoot $managerRoot }
        Write-Info "Preset is already active: $($selected.Name)."
        return
    }
    Set-ConfigLanguageValue -IniPath $iniPath -Language ([string]$selected.Value)
    if ($null -ne $state) { $state.ActiveValue = [string]$selected.Value; Save-LanguageManagerState -State $state -ManagerRoot $managerRoot }
    Write-Ok "Active language preset changed to $($selected.Name) ($($selected.Value))."
    Write-Info 'Only the existing language= value was changed; language files were not installed, removed, or overwritten.'
}

function Remove-LanguagePack {
    $targetRoot = Select-TargetFolder
    $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
    $iniPath = Join-Path $targetRoot 'sp26\steam_settings\configs.user.ini'
    if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { throw "Could not find configs.user.ini at '$iniPath'." }
    $managerRoot = Get-LanguageManagerRoot -TargetRoot $targetRoot
    $state = Get-LanguageManagerState -TargetRoot $targetRoot -IniPath $iniPath
    if ($null -eq $state) {
        Write-Warn 'No language packs tracked by the multi-language manager were found.'
        return
    }
    if (@($state.Packs).Count -eq 0) { Write-Warn 'No language packs are currently installed according to the manager state.'; return }
    $installed = @($state.Packs)
    Write-Host ''
    Write-Host 'Installed language packs:' -ForegroundColor White
    for ($i=0; $i -lt $installed.Count; $i++) { Write-Host "  [$($i+1)] $(Get-LanguageLabel $installed[$i].Name) (language=$($installed[$i].Value), files tracked=$(@($installed[$i].Files).Count))" }
    Write-Host '  [0] Cancel'
    $choice = Read-Host 'Choose a language pack to remove'
    if ($choice -eq '0' -or [string]::IsNullOrWhiteSpace($choice)) { Write-Info 'Removal cancelled.'; return }
    $number=0
    if (-not [int]::TryParse($choice,[ref]$number) -or $number -lt 1 -or $number -gt $installed.Count) { throw 'Invalid selection.' }
    $packState = $installed[$number-1]
    $languageName = [string]$packState.Name
    $files = @($packState.Files)
    if ($files.Count -eq 0) { throw "The state record for '$languageName' has no tracked files. Refusing to guess how to remove it." }
    Write-Warn "This removes $languageName. Files shared with other installed packs will remain. Original files are restored only after the final owning language is removed."
    $confirm = Read-Host 'Continue? [y/N]'
    if ($confirm -notmatch '^(y|yes)$') { Write-Info 'Removal cancelled.'; return }
    foreach ($relative in $files) {
        $pathState = Get-ManagerPathState -State $state -Relative ([string]$relative)
        if ($null -eq $pathState) { throw "Missing ownership state for '$relative'. No files were changed." }
        if (@($pathState.Owners) -notcontains $languageName) { throw "Ownership record does not list '$languageName' for '$relative'. No files were changed." }
        $dest = [IO.Path]::GetFullPath((Join-Path $targetRoot ([string]$relative)))
        $prefix = $targetRoot.TrimEnd('\') + '\'
        if (-not $dest.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe tracked path in state file: $relative" }
        Test-SafeGameDestination -TargetRoot $targetRoot -Destination $dest
        if (-not (Test-Path -LiteralPath $dest -PathType Leaf)) { throw "Tracked language file is missing: $dest. No files were changed." }
        $item = Get-Item -LiteralPath $dest -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Tracked file is a reparse point: $dest. No files were changed." }
        $actualHash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualHash -ne [string]$pathState.Hash) { throw "Tracked file was changed outside the manager: $dest. No files were changed; restore the expected file before retrying." }
        if (@($pathState.Owners).Count -eq 1 -and [bool]$pathState.OriginalExisted) {
            $original = Join-Path (Join-Path $managerRoot 'original-files') ([string]$pathState.OriginalBackupRelative)
            if (-not (Test-Path -LiteralPath $original -PathType Leaf)) { throw "Original backup is missing: $original. No files were changed." }
        }
    }
    $currentValue = Get-ConfigLanguageValue -IniPath $iniPath
    $isActivePack = ($currentValue -eq [string]$packState.Value)
    if ($isActivePack) {
        Set-ConfigLanguageValue -IniPath $iniPath -Language 'english'
        $state.ActiveValue = 'english'
        Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
        Write-Info 'The removed language was active; switched the preset to English before removing its files.'
    }
    foreach ($relative in $files) {
        $relative = [string]$relative
        $pathState = Get-ManagerPathState -State $state -Relative $relative
        $dest = Join-Path $targetRoot $relative
        $owners = @($pathState.Owners | Where-Object { [string]$_ -ine $languageName })
        if ($owners.Count -gt 0) {
            $replacement = [pscustomobject]@{ Relative=[string]$pathState.Relative; Hash=[string]$pathState.Hash; Owners=@($owners); OriginalExisted=[bool]$pathState.OriginalExisted; OriginalBackupRelative=[string]$pathState.OriginalBackupRelative }
            $newPaths = @()
            foreach ($p in @($state.Paths)) { if ([string]$p.Relative -ieq $relative) { $newPaths += $replacement } else { $newPaths += $p } }
            $state.Paths = @($newPaths)
        } else {
            if ([bool]$pathState.OriginalExisted) {
                $original = Join-Path (Join-Path $managerRoot 'original-files') ([string]$pathState.OriginalBackupRelative)
                Copy-FileAtomically -Source $original -Destination $dest
            } else { Remove-Item -LiteralPath $dest -Force -ErrorAction Stop }
            $state.Paths = @($state.Paths | Where-Object { [string]$_.Relative -ine $relative })
        }
        $currentPack = Get-ManagerPack -State $state -LanguageName $languageName
        $currentPack.Files = @(@($currentPack.Files) | Where-Object { [string]$_ -ine $relative })
        Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
    }
    $state.Packs = @($state.Packs | Where-Object { [string]$_.Name -ine $languageName })
    Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
    Write-Ok "$languageName was removed. Shared files required by other installed packs were retained."
    if (@($state.Packs).Count -eq 0) {
        $now = Get-ConfigLanguageValue -IniPath $iniPath
        if ($now -eq [string]$state.ActiveValue) {
            $baselineValue = Get-ConfigLanguageValue -IniPath ([string]$state.OriginalIniBackup)
            Set-ConfigLanguageValue -IniPath $iniPath -Language $baselineValue
            $state.ActiveValue = $baselineValue
            Save-LanguageManagerState -State $state -ManagerRoot $managerRoot
            Write-Info "No language packs remain; restored the original language setting '$baselineValue'."
        } else { Write-Warn 'No language packs remain, but the config language value changed outside the manager. It was left untouched.' }
    }
}

# Backup folders inside 'og files' that look like a usable fix backup: a real
# directory (not a link), non-empty and under 20 MB, which is what an install
# writes. Shared by Remove-Fix and the pre-downgrade cleanup, so both agree on
# what counts as a backup.
function Get-FixBackupCandidates {
    param([Parameter(Mandatory)][string]$TargetRoot)
    $candidates = [System.Collections.Generic.List[object]]::new()
    $backupBase = Join-Path $TargetRoot 'og files'
    if (-not (Test-Path -LiteralPath $backupBase -PathType Container)) { return $candidates }
    foreach ($dir in (Get-ChildItem -LiteralPath $backupBase -Directory -Force -ErrorAction SilentlyContinue)) {
        if (($dir.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $bytes = (Get-ChildItem -LiteralPath $dir.FullName -File -Recurse -Force -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        if ($bytes -gt 0 -and $bytes -lt 20MB) { $candidates.Add([pscustomobject]@{ Directory = $dir; Bytes = $bytes }) }
    }
    return $candidates
}

function Remove-Fix {
    # -SkipPrompt is used by the pre-downgrade cleanup, which asks its own
    # question (and explains that the backups are deleted afterwards too).
    param([switch]$SkipPrompt)
    $targetRoot = Select-TargetFolder
    $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
    $backupBase = Join-Path $targetRoot 'og files'
    if (-not (Test-Path -LiteralPath $backupBase -PathType Container)) { throw "The backup directory was not found: $backupBase" }
    $candidates = @(Get-FixBackupCandidates -TargetRoot $targetRoot)
    if ($candidates.Count -eq 0) { throw 'No non-empty "og files" backup folder smaller than 20 MB was found. Nothing was changed.' }
    $candidate = $candidates | Sort-Object { $_.Directory.CreationTimeUtc } | Select-Object -First 1
    $backupRoot = $candidate.Directory.FullName
    Write-Warn "Selected original-file backup: $backupRoot"
    if (-not $SkipPrompt) {
        $confirm = Read-Host "Selected backup:`n$backupRoot`n`nRemove the fix and restore originals? [y/N]"
        if ($confirm -notmatch '^(y|yes)$') { Write-Info 'Fix removal cancelled.'; return }
    }
    $workRoot = Join-Path $env:TEMP ('codhq-fix-removal-' + [guid]::NewGuid().ToString('N'))
    $zipPath = Join-Path $workRoot $ZipFileName
    $archive = $null
    try {
        $null = New-Item -ItemType Directory -Path $workRoot -Force
        Download-FileWithProgress -Url $ZipUrl -Destination $zipPath -Label "Downloading fix manifest for removal"
        Add-Type -AssemblyName System.IO.Compression
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
        $relativeFiles = [System.Collections.Generic.List[string]]::new()
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) { continue }
            $relativeFiles.Add((Get-ValidatedRelativePath $entry.FullName))
        }
        $restored = 0; $removed = 0
        foreach ($relative in $relativeFiles) {
            $dest = Join-Path $targetRoot $relative
            $backup = Join-Path $backupRoot $relative
            if (Test-Path -LiteralPath $backup -PathType Leaf) {
                $null = New-Item -ItemType Directory -Path (Split-Path -Parent $dest) -Force
                Copy-Item -LiteralPath $backup -Destination $dest -Force -ErrorAction Stop
                $restored++
            } elseif (Test-Path -LiteralPath $dest -PathType Leaf) {
                Remove-Item -LiteralPath $dest -Force -ErrorAction Stop
                $removed++
            }
        }
        Write-Ok 'Fix removal completed.'
        Write-Info "Restored original files: $restored"
        Write-Info "Removed fix-added files: $removed"
    } finally {
        if ($archive) { try { $archive.Dispose() } catch {} }
        if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# Resolves the game folder without ever opening a picker: a downgrade should not be
# interrupted by a folder dialog just to look for fix backups.
function Resolve-GameFolderQuiet {
    if ($global:SessionGameFolder -and (Test-Path -LiteralPath $global:SessionGameFolder -PathType Container)) {
        try { return ([IO.Path]::GetFullPath($global:SessionGameFolder).TrimEnd('\')) } catch { }
    }
    $saved = Get-SavedGameFolder
    if ($saved) { return $saved }
    $detected = @(Find-CallOfDutyHQ)
    if ($detected.Count -eq 1) { return $detected[0] }
    return $null
}

# A downgrade changes which game version is installed, so a fix applied for the
# current version (and the originals it backed up) no longer belongs to the
# install. This reverts the fix and then deletes "og files" entirely, before the
# downgrade touches anything. Declining cancels the downgrade, because carrying on
# would leave originals and fix files mixed across two game versions.
function Clear-FixBeforeDowngrade {
    param([string]$Method = 'Normal')

    $targetRoot = Resolve-GameFolderQuiet
    if (-not $targetRoot) {
        Add-LogEntry -Text '[WARN] The game folder is unknown, so a previously applied fix could not be checked or reverted.' -Color '#FFD700'
        return
    }
    $backupBase = Join-Path $targetRoot 'og files'
    if (-not (Test-Path -LiteralPath $backupBase -PathType Container)) {
        Add-LogEntry -Text '[OK]   No fix backup in the game folder, so there is nothing to revert before the downgrade.' -Color '#4EC9B0'
        return
    }

    $candidates = @(Get-FixBackupCandidates -TargetRoot $targetRoot)
    $canRevert = $candidates.Count -gt 0
    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add('"og files" is present in your game folder, so the fix is still applied.')
    $lines.Add('')
    $lines.Add($targetRoot)
    $lines.Add('')
    $lines.Add("The $Method downgrade changes the game version, so these files and backups no")
    $lines.Add('longer match the install. Before anything else this will:')
    $lines.Add('')
    if ($canRevert) {
        $lines.Add('  1. restore the originals from the newest backup (revert the fix)')
        $lines.Add('  2. delete the whole "og files" folder, backups included - not reversible')
    } else {
        $lines.Add('  1. delete the whole "og files" folder, backups included - not reversible')
        $lines.Add('     (no usable backup was found inside it, so nothing is restored first)')
    }
    $lines.Add('')
    $lines.Add('Revert the fix and delete "og files", then continue with the downgrade? [y/N]')
    if ((Read-Host ($lines -join "`n")) -notmatch '^(y|yes)$') {
        Stop-Cancelled 'Downgrade cancelled: the fix is still applied and its backups were left untouched.'
    }

    if ($canRevert) {
        Write-Host ''
        Write-Info 'Reverting the applied fix before the downgrade...'
        Remove-Fix -SkipPrompt
        Write-Ok 'Fix reverted to the original files.'
    } else {
        Write-Warn 'No usable backup was found in "og files"; only the folder itself will be removed.'
    }

    # Delete the backups. Guarded: exactly an "og files" directory directly under
    # the game folder, and never a link, so a stray path cannot be wiped.
    if ((Split-Path -Leaf $backupBase) -ne 'og files') { throw "Refusing to delete an unexpected path: $backupBase" }
    $item = Get-Item -LiteralPath $backupBase -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        Write-Ok '"og files" is already gone.'
        return
    }
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing to delete a linked folder: $backupBase" }
    try {
        Remove-Item -LiteralPath $backupBase -Recurse -Force -ErrorAction Stop
    } catch {
        throw ('The downgrade was not started because "og files" could not be deleted: ' + $_.Exception.Message)
    }
    Write-Ok ('Deleted "og files" and every backup inside it: ' + $backupBase)
}

function Launch-BatchFile {
    $targetRoot = Select-TargetFolder
    $targetRoot = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
    $launcherPath = Join-Path $targetRoot '!start_sp26.bat'
    if (-not (Test-Path -LiteralPath $launcherPath -PathType Leaf)) {
        Write-Warn "!start_sp26.bat was not found in the selected game folder:"
        Write-Host "  $launcherPath"
        return
    }
    Write-Host "Batch file: $launcherPath"
    $confirm = Read-Host 'Launch !start_sp26.bat now? [y/N]'
    if ($confirm -notmatch '^(y|yes)$') { Write-Info 'Launch cancelled.'; return }
    Start-Process -FilePath $env:ComSpec -ArgumentList @('/c', ('"' + $launcherPath + '"')) -WorkingDirectory $targetRoot
    Write-Ok 'Launch command started.'
}


# ==========================================
# SCRIPT 2 FUNCTIONS (Downgrade Tool)
# ==========================================
function Get-SteamInstallationPath {
    $registryPaths = @(
        @{ Path = "HKLM:\SOFTWARE\Wow6432Node\Valve\Steam"; Name = "InstallPath" },
        @{ Path = "HKCU:\Software\Valve\Steam";             Name = "SteamPath"   },
        @{ Path = "HKLM:\SOFTWARE\Valve\Steam";             Name = "InstallPath" }
    )
    foreach ($reg in $registryPaths) {
        try {
            $steamPath = (Get-ItemProperty -Path $reg.Path -Name $reg.Name -ErrorAction Stop).$($reg.Name)
            if ($steamPath -and (Test-Path $steamPath -PathType Container)) { return $steamPath }
        } catch { }
    }
    $fallbackPaths = @("${env:ProgramFiles(x86)}\Steam", "${env:ProgramFiles}\Steam", "${env:HOME}\Steam")
    foreach ($path in $fallbackPaths) { if (Test-Path $path -PathType Container) { return $path } }
    return $null
}

function Download-WithProgress {
    param([string]$Url, [string]$OutFile, [string]$Activity = "Downloading file")
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $request = [System.Net.HttpWebRequest]::Create($Url)
    $request.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PowerShell"
    $request.AllowAutoRedirect = $true
    $response = $request.GetResponse()
    $totalBytes = $response.ContentLength
    $stream = $response.GetResponseStream()
    $fileStream = [System.IO.File]::Create($OutFile)
    $buffer = New-Object byte[] 8192
    $bytesRead = 0; $bytesTotal = 0; $lastPercent = -1
    try {
        while (($bytesRead = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $fileStream.Write($buffer, 0, $bytesRead)
            $bytesTotal += $bytesRead
            $percent = if ($totalBytes -gt 0) { [int][Math]::Floor(($bytesTotal / $totalBytes) * 100) } else { 0 }
            if ($percent -ne $lastPercent) {
                $downloadedMB = [Math]::Round($bytesTotal / 1MB, 2)
                $totalMB = if ($totalBytes -gt 0) { [Math]::Round($totalBytes / 1MB, 2) } else { "?" }
                Write-Progress -Activity $Activity -Status "$downloadedMB MB / $totalMB MB" -PercentComplete $percent
                $lastPercent = $percent
            }
        }
    } finally { $fileStream.Close(); $stream.Close(); $response.Close(); Write-Progress -Activity $Activity -Completed }
}

function Expand-ZipOverwrite {
    param([string]$ZipPath, [string]$DestinationPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (-not (Test-Path $DestinationPath)) { New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    $total = $zip.Entries.Count; $current = 0
    try {
        foreach ($entry in $zip.Entries) {
            $current++; $percent = [int][Math]::Floor(($current / $total) * 100)
            Write-Progress -Activity "Extracting manifests" -Status $entry.FullName -PercentComplete $percent
            $entryPath = $entry.FullName -replace '/', '\'
            if ([string]::IsNullOrEmpty($entryPath)) { continue }
            if ([string]::IsNullOrEmpty($entry.Name)) {
                $dirPath = Join-Path $DestinationPath $entryPath
                if (-not (Test-Path $dirPath)) { New-Item -ItemType Directory -Path $dirPath -Force | Out-Null }
                continue
            }
            $destFile = Join-Path $DestinationPath $entryPath
            $destDir = Split-Path $destFile -Parent
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destFile, $true)
        }
    } finally { $zip.Dispose(); Write-Progress -Activity "Extracting manifests" -Completed }
}

function Wait-SteamReady {
    param([int]$Timeout = 180)
    $steamReady = $false
    $elapsed = 0
    Write-Host "Waiting for Steam to fully open (Do not close this window)..." -ForegroundColor Yellow
    Write-Host "!!  DO NOT CLOSE THIS POWERSHELL WINDOW YET  !!" -ForegroundColor Red
    
    while (-not $steamReady -and $elapsed -lt $Timeout) {
        Start-Sleep -Seconds 2
        $elapsed += 2
        Write-Host "." -NoNewline -ForegroundColor DarkGray
        
        try {
            $webHelper = Get-Process -Name "steamwebhelper" -ErrorAction SilentlyContinue
            if ($webHelper) { $steamReady = $true }
        } catch { }
    }
    
    Write-Host "" 
    
    if ($steamReady) {
        Write-Host "[OK] Steam client is fully loaded and ready!" -ForegroundColor Green
        Start-Sleep -Seconds 3 
    } else {
        Write-Host "[WARN] Timed out waiting for Steam to load completely, but proceeding..." -ForegroundColor Yellow
    }
}

function Invoke-SteamDowngrade {
    param([string]$Method)
    
    Write-Host "`n==================================================" -ForegroundColor Cyan
    Write-Host "       SteamVault | MW4 Version Downgrade" -ForegroundColor Cyan
    Write-Host "==================================================" -ForegroundColor Cyan

    $steamRoot = Get-SteamInstallationPath
    if (-not $steamRoot) { Write-Error "Steam installation not found."; return }
    Write-Host "Steam found at: $steamRoot" -ForegroundColor Green
    $plugInDir = Join-Path -Path $steamRoot -ChildPath $SteamConfigSubPath
    if (-not (Test-Path $plugInDir -PathType Container)) { Write-Error "Folder not found: $plugInDir"; return }

    if ($Method -eq "Nuclear") {
        Write-Host "`n=== INITIATING NUCLEAR METHOD ===" -ForegroundColor Magenta
        Write-Host "`nClosing all Steam processes..." -ForegroundColor Yellow
        $steamProcesses = Get-Process -Name steam* -ErrorAction SilentlyContinue
        if ($steamProcesses) { $steamProcesses | Stop-Process -Force; Start-Sleep -Seconds 3; Write-Host "Steam closed." -ForegroundColor Green }
        Write-Host "Cleaning Steam installation folder (this may take a moment)..." -ForegroundColor Yellow
        $safeItems = @("appcache", "config", "steam", "steamapps", "userdata", "steam.exe")
        $itemsToDelete = Get-ChildItem -Path $steamRoot -Force | Where-Object { $safeItems -notcontains $_.Name }
        foreach ($item in $itemsToDelete) {
            Write-Host "  Removing: $($item.Name)" -ForegroundColor DarkGray
            Remove-Item -Path $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
        Write-Host "Cleanup complete." -ForegroundColor Green
        $steamExe = Join-Path -Path $steamRoot -ChildPath "steam.exe"
        Write-Host "`nStarting Steam..." -ForegroundColor Yellow
        Start-Process -FilePath $steamExe
        Wait-SteamReady
        Write-Host "`nClosing Steam to run installer..." -ForegroundColor Yellow
        $steamProcesses = Get-Process -Name steam* -ErrorAction SilentlyContinue
        if ($steamProcesses) { $steamProcesses | Stop-Process -Force; Start-Sleep -Seconds 3 }
        Write-Host "`nDownloading BetterSteamTools (irm https://ps.lua.tools/install-ost.ps1 | iex)..." -ForegroundColor Yellow
        try {
            # Same execution as before (Invoke-Expression, same scope and error
            # preference); the redirects only make the output visible in the log.
            Invoke-Expression (Invoke-RestMethod -Uri "https://ps.lua.tools/install-ost.ps1") *>&1 | ForEach-Object {
                if ($_ -is [System.Management.Automation.ErrorRecord]) { Add-LogEntry -Text (Format-ErrorRecord $_) -Color '#FF4D4D' }
                elseif ($_ -is [System.Management.Automation.WarningRecord]) { Add-LogEntry -Text ('[WARN] ' + $_.Message) -Color '#FFD700' }
                elseif ($null -ne $_) { Add-LogEntry -Text ([string]$_) -Color '#A0A0B0' }
            }
        } catch { Write-Warning "Failed to run the installer script: $($_.Exception.Message)" }
        Write-Host "`nRestarting Steam (via BetterSteamTools)..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
        if (-not (Get-Process -Name steam -ErrorAction SilentlyContinue)) { Start-Process -FilePath $steamExe }
        Wait-SteamReady
        Write-Host "`nNuclear method phase complete. Proceeding to normal installation..." -ForegroundColor Magenta
    }

    Write-Host "`nClosing all Steam processes..." -ForegroundColor Yellow
    $steamProcesses = Get-Process -Name steam* -ErrorAction SilentlyContinue
    if ($steamProcesses) { $steamProcesses | Stop-Process -Force; Start-Sleep -Seconds 3; Write-Host "Steam closed." -ForegroundColor Green }

    Write-Host "`nDeleting old Lua files..." -ForegroundColor Yellow
    foreach ($file in @("1938090.lua", "1938090_25909941.lua")) {
        $fullPath = Join-Path -Path $plugInDir -ChildPath $file
        if (Test-Path $fullPath) { Remove-Item -Path $fullPath -Force; Write-Host "Deleted: $file" -ForegroundColor Green }
    }

    $tempLua = Join-Path -Path $plugInDir -ChildPath $SourceFileName
    Write-Host "`nDownloading version locked Lua file..." -ForegroundColor Yellow
    Download-WithProgress -Url $LuaDownloadUrl -OutFile $tempLua -Activity "Downloading version locked Lua file"
    Rename-Item -Path $tempLua -NewName $TargetFileName -Force
    Write-Host "Downloaded and renamed to: $TargetFileName" -ForegroundColor Green

    $depotCacheDir = Join-Path -Path $steamRoot -ChildPath "depotcache"
    if (Test-Path $depotCacheDir) {
        Write-Host "`nCleaning old manifests from depotcache to prevent Steam conflicts..." -ForegroundColor Yellow
        $manifestFiles = Get-ChildItem -Path $depotCacheDir -Filter "*.manifest"
        $deletedCount = 0
        foreach ($manifest in $manifestFiles) {
            foreach ($id in $codDepotIds) {
                if ($manifest.Name.StartsWith("$($id)_")) { Remove-Item -Path $manifest.FullName -Force; $deletedCount++; break }
            }
        }
        Write-Host "Removed $deletedCount old manifest files." -ForegroundColor Green
    } else { Write-Host "`nDepotcache folder not found. It will be created by extraction." -ForegroundColor Gray }

    $tempZip = Join-Path $env:TEMP "MW4_25909941_manifests.zip"
    Write-Host "`nDownloading manifests ZIP..." -ForegroundColor Yellow
    Download-WithProgress -Url $ZipDownloadUrl -OutFile $tempZip -Activity "Downloading manifests ZIP"
    Write-Host "Extracting downgrade manifests into Steam root: $steamRoot" -ForegroundColor Gray
    Expand-ZipOverwrite -ZipPath $tempZip -DestinationPath $steamRoot
    Remove-Item -Path $tempZip -Force -ErrorAction SilentlyContinue
    Write-Host "Downgrade manifests extracted successfully." -ForegroundColor Green

    $acfPath = Join-Path -Path $steamRoot -ChildPath "steamapps\$AppManifestFile"
    $acfFixed = $false
    if (Test-Path $acfPath) {
        Write-Host "`nModifying $AppManifestFile to bypass disk space check..." -ForegroundColor Yellow
        try {
            Set-ItemProperty -Path $acfPath -Name IsReadOnly -Value $false
            $content = Get-Content -Path $acfPath -Raw
            if ($content -match '"StateFlags"') {
                $content = $content -replace '"StateFlags"\s*"\d+"', '"StateFlags"		"4"'
                $content = $content -replace '"BytesToDownload"\s*"\d+"', '"BytesToDownload"		"0"'
                $content = $content -replace '"BytesDownloaded"\s*"\d+"', '"BytesDownloaded"		"0"'
                $content = $content -replace '"UpdateResult"\s*"\d+"', '"UpdateResult"		"0"'
                $content = $content -replace '"SizeOnDisk"\s*"\d+"', '"SizeOnDisk"		"0"'
                $content = $content -replace '"buildid"\s*"\d+"', '"buildid"		"1"'
                $content | Set-Content -Path $acfPath -NoNewline
                $acfFixed = $true
                Write-Host "AppManifest modified successfully. Steam will treat the game as already installed." -ForegroundColor Green
            } else { Write-Warning "The AppManifest file is severely corrupted (missing StateFlags)." }
        } catch { Write-Warning "Failed to modify the appmanifest file: $_" }
    }

    if (-not $acfFixed -and (Test-Path $acfPath)) {
        Write-Host "`nDeleting corrupted AppManifest to force a full rebuild..." -ForegroundColor Yellow
        try {
            Set-ItemProperty -Path $acfPath -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue
            Remove-Item -Path $acfPath -Force -ErrorAction Stop
            Write-Host "Deleted $AppManifestFile successfully." -ForegroundColor Green
            Write-Host "NOTE: Because the file was deleted, the 'Install' button might be greyed out if you are low on disk space." -ForegroundColor Red
        } catch { Write-Warning "Could not delete the appmanifest file. Please delete it manually: $acfPath" }
    }

    $steamExe = Join-Path -Path $steamRoot -ChildPath "steam.exe"
    if (Test-Path $steamExe) {
        Write-Host "`nLaunching Steam..." -ForegroundColor Yellow
        Start-Process -FilePath $steamExe
        Wait-SteamReady
        Write-Host "`nTriggering automatic validation..." -ForegroundColor Cyan
        Start-Process "steam://validate/$AppId"
        Write-Host ">> IMPORTANT: Steam should now begin validating your files." -ForegroundColor Cyan
    } else { Write-Warning "steam.exe was not found at the expected path: $steamExe" }
    
    Write-Host "`n=== Operation Complete ===" -ForegroundColor Cyan
}

# ==========================================
# WPF GUI DEFINITION (SteamVault)
# ==========================================
[xml]$xaml = @"
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="SteamVault - MW4 Companion" Height="760" Width="1260"
        MinWidth="980" MinHeight="620"
        WindowStartupLocation="CenterScreen" ResizeMode="CanResize"
        UseLayoutRounding="True" TextOptions.TextFormattingMode="Display"
        Background="{DynamicResource BgDark}" Foreground="{DynamicResource TextPrimary}" FontFamily="Segoe UI">
    <Window.Resources>
        <!-- Colors -->
        <SolidColorBrush x:Key="BgDark" Color="#191916"/>
        <SolidColorBrush x:Key="BgSidebar" Color="#151511"/>
        <SolidColorBrush x:Key="BgCard" Color="#22221D"/>
        <SolidColorBrush x:Key="BgCardHover" Color="#2C2B24"/>
        <SolidColorBrush x:Key="Accent" Color="#C7A878"/>
        <SolidColorBrush x:Key="AccentHover" Color="#D9BB8D"/>
        <SolidColorBrush x:Key="TextPrimary" Color="#F3F0E8"/>
        <SolidColorBrush x:Key="TextSecondary" Color="#B8B4A8"/>
        <SolidColorBrush x:Key="TextMuted" Color="#858176"/>
        <SolidColorBrush x:Key="Green" Color="#92B49A"/>
        <SolidColorBrush x:Key="Red" Color="#D77D70"/>
        <SolidColorBrush x:Key="Yellow" Color="#D8B66C"/>
        <SolidColorBrush x:Key="Border" Color="#39382F"/>
        <SolidColorBrush x:Key="AccentPressed" Color="#A88758"/>
        <SolidColorBrush x:Key="TextOnAccent" Color="#211E18"/>
        <SolidColorBrush x:Key="Magenta" Color="#C6A2B8"/>
        <SolidColorBrush x:Key="InputBg" Color="#1C1C18"/>
        <SolidColorBrush x:Key="PopupBg" Color="#24231E"/>
        <SolidColorBrush x:Key="PopupItemHover" Color="#302F27"/>
        <SolidColorBrush x:Key="PopupItemSelected" Color="#39362B"/>
        <SolidColorBrush x:Key="PopupArrow" Color="#A8A394"/>
        <SolidColorBrush x:Key="ScrollThumb" Color="#555348"/>
        <SolidColorBrush x:Key="ScrollThumbHover" Color="#706D60"/>
        <SolidColorBrush x:Key="CardHoverDanger" Color="#382622"/>
        <SolidColorBrush x:Key="HintBg" Color="#1E1E19"/>
        <SolidColorBrush x:Key="ButtonBg" Color="#302F28"/>
        <SolidColorBrush x:Key="ButtonHoverBg" Color="#3A3930"/>
        <SolidColorBrush x:Key="ButtonPressedBg" Color="#282720"/>
        <SolidColorBrush x:Key="ButtonBorder" Color="#48463A"/>
        <SolidColorBrush x:Key="SmallButtonBg" Color="#292820"/>
        <SolidColorBrush x:Key="SmallButtonText" Color="#E3DED1"/>
        <SolidColorBrush x:Key="StatusReadyBg" Color="#252D25"/>
        <SolidColorBrush x:Key="StatusReadyBorder" Color="#465847"/>
        <SolidColorBrush x:Key="StatusBusyBg" Color="#2C2A22"/>
        <SolidColorBrush x:Key="StatusBusyBorder" Color="#57503D"/>
        <SolidColorBrush x:Key="StatusFailedBg" Color="#352522"/>
        <SolidColorBrush x:Key="StatusFailedBorder" Color="#60413A"/>
        <SolidColorBrush x:Key="StatusCancelledBg" Color="#332E20"/>
        <SolidColorBrush x:Key="StatusCancelledBorder" Color="#5D5235"/>

        <!-- ToolTip Style -->
        <Style TargetType="ToolTip">
            <Setter Property="Background" Value="{DynamicResource PopupBg}"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="10,5"/>
            <Setter Property="FontSize" Value="12"/>
        </Style>

        <!-- Sidebar Button Style -->
        <Style x:Key="NavButton" TargetType="RadioButton">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
            <Setter Property="Padding" Value="16,13"/>
            <Setter Property="Margin" Value="12,4"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Grid>
                            <Border x:Name="border" Background="{TemplateBinding Background}" CornerRadius="9" Padding="{TemplateBinding Padding}">
                                <ContentPresenter VerticalAlignment="Center"/>
                            </Border>
                            <Border x:Name="activeMark" Width="3" HorizontalAlignment="Left" Margin="0,9,0,9"
                                    Background="{DynamicResource Accent}" CornerRadius="2" Opacity="0"/>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="border" Property="Background" Value="{DynamicResource BgCardHover}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="border" Property="Background" Value="{DynamicResource BgCardHover}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                                <Setter Property="FontWeight" Value="SemiBold"/>
                                <Setter TargetName="activeMark" Property="Opacity" Value="1"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Action Button Style (shared look; the hover lift lives in ActionButton) -->
        <Style x:Key="ActionButtonBase" TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource BgCard}"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
            <Setter Property="Padding" Value="20"/>
            <Setter Property="Margin" Value="5,0,5,12"/>
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <!-- Hover motion is a small vertical shift, not a scale: scaling made the
                 card grow past its own bounds (over the scrollbar and the page edge). -->
            <Setter Property="RenderTransform">
                <Setter.Value>
                    <TranslateTransform X="0" Y="0"/>
                </Setter.Value>
            </Setter>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="border" Property="Background" Value="{DynamicResource BgCardHover}"/>
                                <Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="border" Property="Opacity" Value="0.4"/>
                                <Setter Property="Cursor" Value="Arrow"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Hover lift in its own style so "reduce motion" can swap it off at runtime. -->
        <Style x:Key="ActionButton" TargetType="Button" BasedOn="{StaticResource ActionButtonBase}">
            <Style.Triggers>
                <EventTrigger RoutedEvent="MouseEnter">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="-3" Duration="0:0:0.16">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
                <EventTrigger RoutedEvent="MouseLeave">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="0" Duration="0:0:0.18">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
            </Style.Triggers>
        </Style>

        <!-- Same card, no motion: used when "reduce motion" is on. -->
        <Style x:Key="ActionButtonMotionless" TargetType="Button" BasedOn="{StaticResource ActionButtonBase}"/>

        <!-- Larger, vertically composed dashboard actions. -->
        <Style x:Key="ActionTileButton" TargetType="Button" BasedOn="{StaticResource ActionButton}">
            <Setter Property="MinHeight" Value="138"/>
            <Setter Property="VerticalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="22"/>
        </Style>
        <Style x:Key="ActionTileButtonMotionless" TargetType="Button" BasedOn="{StaticResource ActionButtonMotionless}">
            <Setter Property="MinHeight" Value="138"/>
            <Setter Property="VerticalContentAlignment" Value="Stretch"/>
            <Setter Property="Padding" Value="22"/>
        </Style>

        <!-- Play card: green accent at rest, and the whole card fills green on hover. -->
        <ControlTemplate x:Key="PlayButtonTemplate" TargetType="Button">
            <Border x:Name="border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="{TemplateBinding VerticalContentAlignment}"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="border" Property="Background" Value="{DynamicResource Green}"/>
                    <Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource Green}"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="border" Property="Opacity" Value="0.4"/>
                    <Setter Property="Cursor" Value="Arrow"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>

        <Style x:Key="PlayButtonBase" TargetType="Button" BasedOn="{StaticResource ActionButtonBase}">
            <Setter Property="Template" Value="{StaticResource PlayButtonTemplate}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Green}"/>
        </Style>

        <Style x:Key="PlayButton" TargetType="Button" BasedOn="{StaticResource PlayButtonBase}">
            <Style.Triggers>
                <EventTrigger RoutedEvent="MouseEnter">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="-3" Duration="0:0:0.16">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
                <EventTrigger RoutedEvent="MouseLeave">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="0" Duration="0:0:0.18">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
            </Style.Triggers>
        </Style>

        <Style x:Key="PlayButtonMotionless" TargetType="Button" BasedOn="{StaticResource PlayButtonBase}"/>

        <!-- Play card content: green at rest, dark on the green fill so it stays readable. -->
        <Style x:Key="PlayIcon" TargetType="TextBlock">
            <Setter Property="FontFamily" Value="Segoe MDL2 Assets"/>
            <Setter Property="FontSize" Value="26"/>
            <Setter Property="Foreground" Value="{DynamicResource Green}"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="Margin" Value="0,0,20,0"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=IsMouseOver}" Value="True">
                    <Setter Property="Foreground" Value="#06140F"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <Style x:Key="PlayTitle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="19"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="Foreground" Value="{DynamicResource Green}"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=IsMouseOver}" Value="True">
                    <Setter Property="Foreground" Value="#06140F"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <Style x:Key="PlaySubtitle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
            <Setter Property="Margin" Value="0,5,0,0"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=IsMouseOver}" Value="True">
                    <Setter Property="Foreground" Value="#0B2A20"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <!-- Danger Action Button Style -->
        <ControlTemplate x:Key="DangerButtonTemplate" TargetType="Button">
            <Border x:Name="border" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="13" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="border" Property="Background" Value="{DynamicResource CardHoverDanger}"/>
                    <Setter TargetName="border" Property="BorderBrush" Value="{DynamicResource Red}"/>
                </Trigger>
                <Trigger Property="IsEnabled" Value="False">
                    <Setter TargetName="border" Property="Opacity" Value="0.4"/>
                    <Setter Property="Cursor" Value="Arrow"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>

        <Style x:Key="DangerButtonBase" TargetType="Button" BasedOn="{StaticResource ActionButtonBase}">
            <Setter Property="Template" Value="{StaticResource DangerButtonTemplate}"/>
        </Style>

        <Style x:Key="DangerButton" TargetType="Button" BasedOn="{StaticResource DangerButtonBase}">
            <Style.Triggers>
                <EventTrigger RoutedEvent="MouseEnter">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="-3" Duration="0:0:0.16">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
                <EventTrigger RoutedEvent="MouseLeave">
                    <BeginStoryboard>
                        <Storyboard>
                            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(TranslateTransform.Y)" To="0" Duration="0:0:0.18">
                                <DoubleAnimation.EasingFunction><CubicEase EasingMode="EaseOut"/></DoubleAnimation.EasingFunction>
                            </DoubleAnimation>
                        </Storyboard>
                    </BeginStoryboard>
                </EventTrigger>
            </Style.Triggers>
        </Style>

        <Style x:Key="DangerButtonMotionless" TargetType="Button" BasedOn="{StaticResource DangerButtonBase}"/>

        <!-- Card Style -->
        <Style x:Key="Card" TargetType="Border">
            <Setter Property="Background" Value="{DynamicResource BgCard}"/>
            <Setter Property="CornerRadius" Value="14"/>
            <Setter Property="Padding" Value="22"/>
            <Setter Property="Margin" Value="5,0,5,18"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
        </Style>

        <!-- Card Icon Style -->
        <Style x:Key="CardIcon" TargetType="TextBlock">
            <Setter Property="FontFamily" Value="Segoe MDL2 Assets"/>
            <Setter Property="FontSize" Value="24"/>
            <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
            <Setter Property="Margin" Value="0,0,0,15"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=IsMouseOver}" Value="True">
                    <Setter Property="Foreground" Value="{DynamicResource AccentHover}"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <!-- Icon used at the left of a full-width action button -->
        <Style x:Key="ActionIcon" TargetType="TextBlock">
            <Setter Property="FontFamily" Value="Segoe MDL2 Assets"/>
            <Setter Property="FontSize" Value="26"/>
            <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
            <Setter Property="VerticalAlignment" Value="Center"/>
            <Setter Property="Margin" Value="0,0,20,0"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding RelativeSource={RelativeSource AncestorType=Button}, Path=IsMouseOver}" Value="True">
                    <Setter Property="Foreground" Value="{DynamicResource AccentHover}"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>

        <!-- Spinner Animation -->
        <Storyboard x:Key="SpinStoryboard" RepeatBehavior="Forever">
            <DoubleAnimation Storyboard.TargetProperty="(UIElement.RenderTransform).(RotateTransform.Angle)" From="0" To="360" Duration="0:0:1"/>
        </Storyboard>

        <!-- Flat text button used for the log toolbar (Copy / Clear) -->
        <Style x:Key="LogLinkButton" TargetType="Button">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Foreground" Value="{DynamicResource TextMuted}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Padding" Value="6,4"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border Background="Transparent" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ============================== -->
        <!-- DARK THEME SCROLLBAR           -->
        <!-- ============================== -->
        <Style x:Key="DarkScrollThumb" TargetType="Thumb">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Thumb">
                        <Border x:Name="grip" Background="{DynamicResource ScrollThumb}" CornerRadius="3" Margin="3"/>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="grip" Property="Background" Value="{DynamicResource ScrollThumbHover}"/>
                            </Trigger>
                            <Trigger Property="IsDragging" Value="True">
                                <Setter TargetName="grip" Property="Background" Value="{DynamicResource Accent}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="DarkScrollBar" TargetType="ScrollBar">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Width" Value="12"/>
            <Setter Property="MinWidth" Value="12"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ScrollBar">
                        <Grid Background="{TemplateBinding Background}" SnapsToDevicePixels="True">
                            <Track x:Name="PART_Track" Focusable="False" IsDirectionReversed="True"
                                   Minimum="{TemplateBinding Minimum}" Maximum="{TemplateBinding Maximum}"
                                   Value="{TemplateBinding Value}" ViewportSize="{TemplateBinding ViewportSize}">
                                <Track.DecreaseRepeatButton>
                                    <RepeatButton x:Name="DecreaseButton" Command="ScrollBar.PageUpCommand" Focusable="False" IsTabStop="False">
                                        <RepeatButton.Template>
                                            <ControlTemplate TargetType="RepeatButton">
                                                <Border Background="Transparent"/>
                                            </ControlTemplate>
                                        </RepeatButton.Template>
                                    </RepeatButton>
                                </Track.DecreaseRepeatButton>
                                <Track.Thumb>
                                    <Thumb Style="{StaticResource DarkScrollThumb}"/>
                                </Track.Thumb>
                                <Track.IncreaseRepeatButton>
                                    <RepeatButton x:Name="IncreaseButton" Command="ScrollBar.PageDownCommand" Focusable="False" IsTabStop="False">
                                        <RepeatButton.Template>
                                            <ControlTemplate TargetType="RepeatButton">
                                                <Border Background="Transparent"/>
                                            </ControlTemplate>
                                        </RepeatButton.Template>
                                    </RepeatButton>
                                </Track.IncreaseRepeatButton>
                            </Track>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="Orientation" Value="Horizontal">
                                <Setter Property="Width" Value="Auto"/>
                                <Setter Property="MinWidth" Value="0"/>
                                <Setter Property="Height" Value="12"/>
                                <Setter Property="MinHeight" Value="12"/>
                                <Setter TargetName="PART_Track" Property="IsDirectionReversed" Value="False"/>
                                <Setter TargetName="DecreaseButton" Property="Command" Value="{x:Static ScrollBar.PageLeftCommand}"/>
                                <Setter TargetName="IncreaseButton" Property="Command" Value="{x:Static ScrollBar.PageRightCommand}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Implicit: every scrollbar in the window (log, pages, popups) follows the dark theme -->
        <Style TargetType="ScrollBar" BasedOn="{StaticResource DarkScrollBar}"/>

        <!-- ============================== -->
        <!-- DARK THEME DROPDOWN (ComboBox) -->
        <!-- ============================== -->
        <Style x:Key="DarkComboBoxItem" TargetType="ComboBoxItem">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="Padding" Value="12,9"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ComboBoxItem">
                        <Border x:Name="itemBorder" Background="{TemplateBinding Background}" CornerRadius="4"
                                Padding="{TemplateBinding Padding}" Margin="2,1">
                            <ContentPresenter HorizontalAlignment="Left" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="itemBorder" Property="Background" Value="{DynamicResource PopupItemHover}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="itemBorder" Property="Background" Value="{DynamicResource PopupItemSelected}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                                <Setter Property="FontWeight" Value="SemiBold"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="DarkComboBox" TargetType="ComboBox">
            <Setter Property="OverridesDefaultStyle" Value="True"/>
            <Setter Property="Background" Value="{DynamicResource InputBg}"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="12,8"/>
            <Setter Property="FontSize" Value="14"/>
            <Setter Property="MaxDropDownHeight" Value="320"/>
            <Setter Property="SnapsToDevicePixels" Value="True"/>
            <Setter Property="HorizontalContentAlignment" Value="Left"/>
            <Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ComboBox">
                        <Grid x:Name="root" SnapsToDevicePixels="True">
                            <Border x:Name="frame" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6"/>

                            <Grid>
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="38"/>
                                </Grid.ColumnDefinitions>

                                <!-- Clicking anywhere opens the list, like a normal dropdown -->
                                <ToggleButton x:Name="ToggleButton" Grid.ColumnSpan="2" Focusable="False" ClickMode="Press"
                                              IsChecked="{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}">
                                    <ToggleButton.Template>
                                        <ControlTemplate TargetType="ToggleButton">
                                            <Grid Background="Transparent">
                                                <TextBlock x:Name="arrow" Text="&#xE70D;" FontFamily="Segoe MDL2 Assets" FontSize="10"
                                                           Foreground="{DynamicResource PopupArrow}" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,14,0"/>
                                            </Grid>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="arrow" Property="Foreground" Value="{DynamicResource AccentHover}"/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </ToggleButton.Template>
                                </ToggleButton>

                                <ContentPresenter x:Name="SelectionBox" Grid.Column="0" Margin="{TemplateBinding Padding}" IsHitTestVisible="False"
                                                  HorizontalAlignment="Left" VerticalAlignment="Center"
                                                  Content="{TemplateBinding SelectionBoxItem}"
                                                  ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}"
                                                  ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"
                                                  ContentStringFormat="{TemplateBinding SelectionBoxItemStringFormat}"/>

                                <TextBox x:Name="PART_EditableTextBox" Grid.Column="0" Margin="{TemplateBinding Padding}" Padding="0"
                                         Background="Transparent" BorderThickness="0" Foreground="{TemplateBinding Foreground}"
                                         CaretBrush="{TemplateBinding Foreground}" VerticalAlignment="Center"
                                         HorizontalAlignment="Stretch" Visibility="Hidden"/>
                            </Grid>

                            <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False"
                                   PopupAnimation="Slide" IsOpen="{TemplateBinding IsDropDownOpen}">
                                <Border x:Name="dropDown" Background="{DynamicResource PopupBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1"
                                        CornerRadius="6" Margin="0,4,0,0" Padding="2"
                                        MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}"
                                        MaxHeight="{Binding MaxDropDownHeight, RelativeSource={RelativeSource TemplatedParent}}">
                                    <Border.Resources>
                                        <Style TargetType="ScrollBar" BasedOn="{StaticResource DarkScrollBar}"/>
                                    </Border.Resources>
                                    <ScrollViewer SnapsToDevicePixels="True" VerticalScrollBarVisibility="Auto" HorizontalScrollBarVisibility="Disabled">
                                        <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                                    </ScrollViewer>
                                </Border>
                            </Popup>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                            </Trigger>
                            <Trigger Property="IsDropDownOpen" Value="True">
                                <Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                            </Trigger>
                            <Trigger Property="IsKeyboardFocusWithin" Value="True">
                                <Setter TargetName="frame" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                            </Trigger>
                            <Trigger Property="IsEditable" Value="True">
                                <Setter TargetName="SelectionBox" Property="Visibility" Value="Hidden"/>
                                <Setter TargetName="PART_EditableTextBox" Property="Visibility" Value="Visible"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="root" Property="Opacity" Value="0.5"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ============================== -->
        <!-- DIALOG BUTTONS (prompt window)  -->
        <!-- ============================== -->
        <Style x:Key="DialogPrimaryButton" TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource Accent}"/>
            <Setter Property="Foreground" Value="{DynamicResource TextOnAccent}"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Padding" Value="18,7"/>
            <Setter Property="MinWidth" Value="96"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="btnBorder" Background="{TemplateBinding Background}" CornerRadius="5" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="btnBorder" Property="Background" Value="{DynamicResource AccentHover}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="btnBorder" Property="Background" Value="{DynamicResource AccentPressed}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Small pill button used for inline actions (Change folder, etc.) -->
        <Style x:Key="SmallButton" TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource SmallButtonBg}"/>
            <Setter Property="Foreground" Value="{DynamicResource SmallButtonText}"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Padding" Value="14,7"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="btnBorder" Background="{TemplateBinding Background}" CornerRadius="5"
                                BorderBrush="{DynamicResource Border}" BorderThickness="1" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="btnBorder" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="btnBorder" Property="Opacity" Value="0.4"/>
                                <Setter Property="Cursor" Value="Arrow"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Shared intro line at the top of every page -->
        <Style x:Key="PageIntro" TargetType="TextBlock">
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Setter Property="Margin" Value="5,4,5,18"/>
        </Style>

        <Style x:Key="PageEyebrow" TargetType="TextBlock">
            <Setter Property="FontSize" Value="10"/>
            <Setter Property="FontWeight" Value="Bold"/>
            <Setter Property="Foreground" Value="{DynamicResource Accent}"/>
            <Setter Property="Margin" Value="5,2,5,7"/>
        </Style>

        <Style x:Key="PageHeading" TargetType="TextBlock">
            <Setter Property="FontSize" Value="26"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="Margin" Value="5,0,5,5"/>
        </Style>

        <!-- Shared explanatory card at the bottom of a page -->
        <Style x:Key="HintCard" TargetType="Border" BasedOn="{StaticResource Card}">
            <Setter Property="Background" Value="{DynamicResource HintBg}"/>
            <Setter Property="Padding" Value="14,12"/>
            <Setter Property="Margin" Value="5,0,5,6"/>
        </Style>

        <Style x:Key="HintText" TargetType="TextBlock">
            <Setter Property="FontSize" Value="12.5"/>
            <Setter Property="Foreground" Value="{DynamicResource TextMuted}"/>
            <Setter Property="TextWrapping" Value="Wrap"/>
            <Setter Property="LineHeight" Value="18"/>
        </Style>

        <Style x:Key="DialogSecondaryButton" TargetType="Button">
            <Setter Property="Background" Value="{DynamicResource ButtonBg}"/>
            <Setter Property="Foreground" Value="{DynamicResource SmallButtonText}"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Padding" Value="18,7"/>
            <Setter Property="MinWidth" Value="96"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="btnBorder" Background="{TemplateBinding Background}" CornerRadius="5"
                                BorderBrush="{DynamicResource ButtonBorder}" BorderThickness="1" Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="btnBorder" Property="Background" Value="{DynamicResource ButtonHoverBg}"/>
                                <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="btnBorder" Property="Background" Value="{DynamicResource ButtonPressedBg}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Accent preset dot shown in the header -->
        <Style x:Key="AccentDot" TargetType="RadioButton">
            <Setter Property="Width" Value="18"/>
            <Setter Property="Height" Value="18"/>
            <Setter Property="Margin" Value="0,0,4,0"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="RadioButton">
                        <Grid Background="Transparent">
                            <Ellipse x:Name="ring" Width="17" Height="17" Stroke="Transparent" StrokeThickness="1.5"/>
                            <Ellipse x:Name="dot" Width="10" Height="10" Fill="{TemplateBinding Background}"/>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="ring" Property="Stroke" Value="{DynamicResource TextMuted}"/>
                                <Setter TargetName="dot" Property="Width" Value="12"/>
                                <Setter TargetName="dot" Property="Height" Value="12"/>
                            </Trigger>
                            <Trigger Property="IsChecked" Value="True">
                                <Setter TargetName="ring" Property="Stroke" Value="{DynamicResource TextPrimary}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- Compact pill used for the activity-log filters and toggles -->
        <ControlTemplate x:Key="ChipTemplate" TargetType="ToggleButton">
            <Border x:Name="chip" Background="Transparent" BorderBrush="{DynamicResource Border}" BorderThickness="1" CornerRadius="4" Padding="{TemplateBinding Padding}">
                <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
            </Border>
            <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                    <Setter TargetName="chip" Property="Background" Value="{DynamicResource BgCardHover}"/>
                    <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
                </Trigger>
                <Trigger Property="IsChecked" Value="True">
                    <Setter TargetName="chip" Property="Background" Value="{DynamicResource Accent}"/>
                    <Setter TargetName="chip" Property="BorderBrush" Value="{DynamicResource Accent}"/>
                    <Setter Property="Foreground" Value="{DynamicResource TextOnAccent}"/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>

        <Style x:Key="FilterChip" TargetType="RadioButton">
            <Setter Property="Template" Value="{StaticResource ChipTemplate}"/>
            <Setter Property="Padding" Value="9,3"/>
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Margin" Value="0,0,5,0"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        </Style>

        <Style x:Key="FilterToggle" TargetType="ToggleButton">
            <Setter Property="Template" Value="{StaticResource ChipTemplate}"/>
            <Setter Property="Padding" Value="9,3"/>
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Foreground" Value="{DynamicResource TextSecondary}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="Margin" Value="0,0,5,0"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
        </Style>

        <!-- Prompt dialog option list (no popup, so it cannot glitch) -->
        <Style x:Key="DialogOptionItem" TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="Padding" Value="12,8"/>
            <Setter Property="Margin" Value="0,1"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FontSize" Value="13.5"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="row" Background="Transparent" CornerRadius="4" Padding="{TemplateBinding Padding}">
                            <ContentPresenter VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="row" Property="Background" Value="{DynamicResource PopupItemHover}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="row" Property="Background" Value="{DynamicResource PopupItemSelected}"/>
                                <Setter Property="FontWeight" Value="SemiBold"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="DialogOptionList" TargetType="ListBox">
            <Setter Property="Background" Value="{DynamicResource InputBg}"/>
            <Setter Property="Foreground" Value="{DynamicResource TextPrimary}"/>
            <Setter Property="BorderBrush" Value="{DynamicResource Border}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="ItemContainerStyle" Value="{StaticResource DialogOptionItem}"/>
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBox">
                        <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="3">
                            <ScrollViewer Focusable="False" VerticalScrollBarVisibility="Auto">
                                <ItemsPresenter/>
                            </ScrollViewer>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="250"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>
        
        <!-- Sidebar -->
        <Border Grid.Column="0" Background="{DynamicResource BgSidebar}" BorderBrush="{DynamicResource Border}" BorderThickness="0,0,1,0">
            <Grid>
                <Grid.RowDefinitions>
                    <RowDefinition Height="150"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="60"/>
                </Grid.RowDefinitions>
                
                <!-- Logo Area -->
                <Grid Grid.Row="0" Margin="24,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="48"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Border Width="42" Height="42" CornerRadius="14" Background="{DynamicResource Accent}" VerticalAlignment="Center" HorizontalAlignment="Left">
                        <TextBlock Text="SV" FontSize="15" FontWeight="Bold" Foreground="{DynamicResource TextOnAccent}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <StackPanel Grid.Column="1" VerticalAlignment="Center" Margin="12,0,0,0">
                        <TextBlock Text="SteamVault" FontSize="23" FontWeight="Bold" Foreground="{DynamicResource TextPrimary}"/>
                        <TextBlock Text="MW4 COMPANION" FontSize="9" Foreground="{DynamicResource Accent}" FontWeight="Bold" Margin="1,5,0,0"/>
                    </StackPanel>
                </Grid>

                <!-- Navigation -->
                <StackPanel Grid.Row="1" Margin="0,10,0,0">
                    <TextBlock Text="WORKSPACE" FontSize="10" Foreground="{DynamicResource TextMuted}" Margin="29,0,0,12" FontWeight="Bold"/>
                    
                    <RadioButton x:Name="NavOverview" Style="{StaticResource NavButton}" IsChecked="True" ToolTip="View game status and quick actions">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE80F;" FontFamily="Segoe MDL2 Assets" FontSize="16" Margin="0,0,15,0" VerticalAlignment="Center"/>
                            <TextBlock Text="Overview" FontSize="14" VerticalAlignment="Center"/>
                        </StackPanel>
                    </RadioButton>

                    <RadioButton x:Name="NavFixes" Style="{StaticResource NavButton}" ToolTip="Manage game fixes and restores">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE90F;" FontFamily="Segoe MDL2 Assets" FontSize="16" Margin="0,0,15,0" VerticalAlignment="Center"/>
                            <TextBlock Text="Fix Installer" FontSize="14" VerticalAlignment="Center"/>
                        </StackPanel>
                    </RadioButton>

                    <RadioButton x:Name="NavLanguages" Style="{StaticResource NavButton}" ToolTip="Install and switch language packs">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE8D2;" FontFamily="Segoe MDL2 Assets" FontSize="16" Margin="0,0,15,0" VerticalAlignment="Center"/>
                            <TextBlock Text="Languages" FontSize="14" VerticalAlignment="Center"/>
                        </StackPanel>
                    </RadioButton>

                    <RadioButton x:Name="NavDowngrade" Style="{StaticResource NavButton}" ToolTip="Fix 'Invalid game version' errors">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE777;" FontFamily="Segoe MDL2 Assets" FontSize="16" Margin="0,0,15,0" VerticalAlignment="Center"/>
                            <TextBlock Text="Downgrade Version" FontSize="14" VerticalAlignment="Center"/>
                        </StackPanel>
                    </RadioButton>

                    <RadioButton x:Name="NavSettings" Style="{StaticResource NavButton}" ToolTip="Customize appearance and motion">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="&#xE713;" FontFamily="Segoe MDL2 Assets" FontSize="16" Margin="0,0,15,0" VerticalAlignment="Center"/>
                            <TextBlock Text="Settings" FontSize="14" VerticalAlignment="Center"/>
                        </StackPanel>
                    </RadioButton>
                </StackPanel>
                
                <StackPanel Grid.Row="2" VerticalAlignment="Center" Margin="24,0">
                    <Border Height="1" Background="{DynamicResource Border}" Margin="0,0,0,11"/>
                    <Grid>
                        <TextBlock x:Name="VersionText" Text="v1.0.0" FontSize="11" Foreground="{DynamicResource TextMuted}" VerticalAlignment="Center"/>
                        <TextBlock Text="STEAMVAULT" FontSize="9" FontWeight="SemiBold" Foreground="{DynamicResource TextMuted}" Opacity="0.75" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                    </Grid>
                </StackPanel>
            </Grid>
        </Border>

        <!-- Main Content -->
        <Grid Grid.Column="1" Background="{DynamicResource BgDark}">
            <Grid.RowDefinitions>
                <RowDefinition Height="88"/>
                <RowDefinition Height="*" MinHeight="220"/>
                <!-- Grab bar: the activity log can be dragged taller, which matters
                     most during a long downgrade. -->
                <RowDefinition Height="5"/>
                <RowDefinition Height="194" MinHeight="110"/>
            </Grid.RowDefinitions>

            <!-- Header -->
            <Border Grid.Row="0" BorderBrush="{DynamicResource Border}" BorderThickness="0,0,0,1" Background="{DynamicResource BgDark}">
                <Grid Margin="32,0">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*" MinWidth="140"/>
                        <ColumnDefinition Width="Auto" MaxWidth="240"/>
                    </Grid.ColumnDefinitions>
                    <StackPanel Grid.Column="0" VerticalAlignment="Center" Margin="0,0,24,0">
                        <TextBlock Text="STEAMVAULT  /  MW4" FontSize="10" FontWeight="Bold" Foreground="{DynamicResource Accent}" Margin="0,0,0,3"/>
                        <TextBlock x:Name="HeaderTitle" Text="Overview" FontSize="25" FontWeight="SemiBold" VerticalAlignment="Center" Foreground="{DynamicResource TextPrimary}" TextTrimming="CharacterEllipsis"/>
                    </StackPanel>
                    
                    <!-- The header stays focused on the current page and live status. -->
                    <StackPanel Grid.Column="1" HorizontalAlignment="Right" Orientation="Horizontal" VerticalAlignment="Center">
                        <Border x:Name="StatusPill" Background="{DynamicResource StatusReadyBg}" CornerRadius="15" Padding="15,6" BorderBrush="{DynamicResource StatusReadyBorder}" BorderThickness="1">
                            <Grid>
                                <!-- Ready Panel -->
                                <StackPanel x:Name="ReadyPanel" Orientation="Horizontal">
                                    <Ellipse x:Name="StatusDot" Width="8" Height="8" Fill="{DynamicResource Green}" VerticalAlignment="Center" Margin="0,0,8,0">
                                        <Ellipse.Triggers>
                                            <EventTrigger RoutedEvent="Loaded">
                                                <BeginStoryboard>
                                                    <Storyboard RepeatBehavior="Forever">
                                                        <DoubleAnimation Storyboard.TargetProperty="Opacity" From="1.0" To="0.3" Duration="0:0:1" AutoReverse="True"/>
                                                    </Storyboard>
                                                </BeginStoryboard>
                                            </EventTrigger>
                                        </Ellipse.Triggers>
                                    </Ellipse>
                                    <TextBlock x:Name="StatusText" Text="Ready" FontSize="12" Foreground="{DynamicResource Green}" FontWeight="Bold" MaxWidth="580" TextWrapping="Wrap"/>
                                </StackPanel>
                                
                                <!-- Busy Panel -->
                                <StackPanel x:Name="BusyPanel" Orientation="Horizontal" Visibility="Collapsed">
                                    <TextBlock Text="&#xE713;" FontFamily="Segoe MDL2 Assets" FontSize="12" Foreground="{DynamicResource Accent}" VerticalAlignment="Center" Margin="0,0,8,0" RenderTransformOrigin="0.5,0.5">
                                        <TextBlock.RenderTransform>
                                            <RotateTransform Angle="0"/>
                                        </TextBlock.RenderTransform>
                                        <TextBlock.Triggers>
                                            <EventTrigger RoutedEvent="Loaded">
                                                <BeginStoryboard Storyboard="{StaticResource SpinStoryboard}"/>
                                            </EventTrigger>
                                        </TextBlock.Triggers>
                                    </TextBlock>
                                    <TextBlock x:Name="BusyText" Text="Processing..." FontSize="12" Foreground="{DynamicResource Accent}" FontWeight="Bold" MaxWidth="580" TextWrapping="Wrap"/>
                                </StackPanel>
                            </Grid>
                        </Border>
                    </StackPanel>
                </Grid>
            </Border>

            <!-- Pages -->
            <Grid Grid.Row="1" Margin="30,20,30,14">
                <!-- PAGE 1: OVERVIEW -->
                <ScrollViewer x:Name="PageOverview" Visibility="Visible" VerticalScrollBarVisibility="Auto" Padding="0" PanningMode="VerticalOnly" ClipToBounds="True">
                    <StackPanel>
                        <TextBlock Style="{StaticResource PageEyebrow}" Text="YOUR LIBRARY"/>
                        <TextBlock Style="{StaticResource PageHeading}" Text="Everything in one place."/>
                        <TextBlock Style="{StaticResource PageIntro}" Text="A calm little control centre for your MW4 install."/>

                        <Border Style="{StaticResource Card}" Padding="24" Background="{DynamicResource BgCardHover}">
                            <Grid>
                                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                <Border Width="62" Height="62" CornerRadius="18" Background="{DynamicResource HintBg}" VerticalAlignment="Center" Margin="0,0,20,0">
                                    <TextBlock x:Name="GameCardIcon" Text="&#xE73E;" FontFamily="Segoe MDL2 Assets" FontSize="28" Foreground="{DynamicResource Green}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Text="GAME FOLDER" Style="{StaticResource PageEyebrow}" Margin="0,0,0,5"/>
                                    <TextBlock x:Name="GameCardTitle" Text="Checking for the game..." FontSize="19" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                    <TextBlock x:Name="GamePathText" Text="Scanning Steam libraries..." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,6,12,0" TextWrapping="Wrap"/>
                                </StackPanel>
                                <Button x:Name="BtnChooseFolder" Grid.Column="2" Content="Change folder" Style="{StaticResource SmallButton}" VerticalAlignment="Center" Margin="14,0,0,0"
                                        ToolTip="Pick the Call of Duty HQ folder yourself; it is remembered for later sessions"/>
                            </Grid>
                        </Border>

                        <Grid Margin="0,0,0,2">
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="14"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Button x:Name="BtnQuickFix" Grid.Column="0" Style="{StaticResource ActionTileButton}" ToolTip="Downloads the latest fix and replaces matching files">
                                <StackPanel VerticalAlignment="Center">
                                    <Border Width="42" Height="42" CornerRadius="13" Background="{DynamicResource HintBg}" HorizontalAlignment="Left" Margin="0,0,0,18">
                                        <TextBlock Text="&#xE896;" Style="{StaticResource ActionIcon}" FontSize="20" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    </Border>
                                    <TextBlock Text="Game files" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Install the latest fix or restore your original files." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,6,0,0" TextWrapping="Wrap"/>
                                    <TextBlock Text="START FIX WORKFLOW  &gt;" FontSize="10" FontWeight="Bold" Foreground="{DynamicResource Accent}" Margin="0,15,0,0"/>
                                </StackPanel>
                            </Button>
                            <Button x:Name="BtnQuickLang" Grid.Column="2" Style="{StaticResource ActionTileButton}" ToolTip="Install new languages or switch active preset">
                                <StackPanel VerticalAlignment="Center">
                                    <Border Width="42" Height="42" CornerRadius="13" Background="{DynamicResource HintBg}" HorizontalAlignment="Left" Margin="0,0,0,18">
                                        <TextBlock Text="&#xE8D2;" Style="{StaticResource ActionIcon}" FontSize="20" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    </Border>
                                    <TextBlock Text="Languages" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Add a language pack or change your active preset." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,6,0,0" TextWrapping="Wrap"/>
                                    <TextBlock Text="CHOOSE LANGUAGE ACTION  &gt;" FontSize="10" FontWeight="Bold" Foreground="{DynamicResource Accent}" Margin="0,15,0,0"/>
                                </StackPanel>
                            </Button>
                        </Grid>

                        <Button x:Name="BtnLaunchBat" Style="{StaticResource PlayButton}" MinHeight="86" ToolTip="Launches !start_sp26.bat from your game folder">
                            <Grid>
                                <Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                <Border Width="46" Height="46" CornerRadius="14" Background="{DynamicResource HintBg}" VerticalAlignment="Center" Margin="0,0,18,0">
                                    <TextBlock Text="&#xE768;" Style="{StaticResource PlayIcon}" FontSize="21" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                </Border>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock Text="Ready when you are" Style="{StaticResource PlayTitle}" FontSize="16"/>
                                    <TextBlock Text="Launch the game from your selected folder." Style="{StaticResource PlaySubtitle}" Margin="0,4,0,0"/>
                                </StackPanel>
                                <TextBlock Grid.Column="2" Text="&#xE76C;" FontFamily="Segoe MDL2 Assets" FontSize="16" Foreground="{DynamicResource Green}" VerticalAlignment="Center" Margin="18,0,4,0"/>
                            </Grid>
                        </Button>

                        <Border Style="{StaticResource HintCard}" Margin="5,2,5,8">
                            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                <TextBlock Text="&#xE946;" FontFamily="Segoe MDL2 Assets" FontSize="15" Foreground="{DynamicResource Accent}" Margin="1,0,12,0" VerticalAlignment="Top"/>
                                <TextBlock Grid.Column="1" Style="{StaticResource HintText}" Text="Downloads are staged safely. Before any game files are replaced, originals are copied to a dated backup in your game folder."/>
                            </Grid>
                        </Border>
                    </StackPanel>
                </ScrollViewer>

                <!-- PAGE 2: FIXES -->
                <ScrollViewer x:Name="PageFixes" Visibility="Collapsed" VerticalScrollBarVisibility="Auto" Padding="0" PanningMode="VerticalOnly" ClipToBounds="True">
                    <StackPanel>
                        <TextBlock Style="{StaticResource PageEyebrow}" Text="GAME FILES"/>
                        <TextBlock Style="{StaticResource PageHeading}" Text="Fixes and restore points."/>
                        <TextBlock Style="{StaticResource PageIntro}" Text="Choose a task. Original files are kept in your game folder so you can roll back."/>
                        <Grid>
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="14"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Button x:Name="BtnInstallFix" Grid.Column="0" Style="{StaticResource ActionTileButton}" ToolTip="Applies the fix and creates a backup of originals">
                                <StackPanel VerticalAlignment="Center">
                                    <Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource HintBg}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE896;" Style="{StaticResource ActionIcon}" FontSize="21" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    </Border>
                                    <TextBlock Text="Install game fix" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Download and apply the latest files. A backup is created first." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,7,0,0" TextWrapping="Wrap"/>
                                </StackPanel>
                            </Button>
                            <Button x:Name="BtnRemoveFix" Grid.Column="2" Style="{StaticResource DangerButton}" MinHeight="138" ToolTip="Restores game files from the backup folder">
                                <StackPanel VerticalAlignment="Center">
                                    <Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource CardHoverDanger}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE7A7;" FontFamily="Segoe MDL2 Assets" FontSize="21" Foreground="{DynamicResource Red}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    </Border>
                                    <TextBlock Text="Restore originals" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Return to the newest backup in your 'og files' folder." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,7,0,0" TextWrapping="Wrap"/>
                                </StackPanel>
                            </Button>
                        </Grid>
                        <Border Style="{StaticResource HintCard}" Margin="5,10,5,8">
                            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                <TextBlock Text="&#xE72E;" FontFamily="Segoe MDL2 Assets" FontSize="16" Foreground="{DynamicResource Accent}" Margin="1,0,12,0" VerticalAlignment="Top"/>
                                <StackPanel Grid.Column="1"><TextBlock Text="Your backups stay yours" FontSize="13" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                    <TextBlock Style="{StaticResource HintText}" Text="Each backup is timestamped. Older backups are kept, so you can always go back further." Margin="0,4,0,0"/></StackPanel>
                            </Grid>
                        </Border>
                    </StackPanel>
                </ScrollViewer>

                <!-- PAGE 3: LANGUAGES -->
                <ScrollViewer x:Name="PageLanguages" Visibility="Collapsed" VerticalScrollBarVisibility="Auto" Padding="0" PanningMode="VerticalOnly" ClipToBounds="True">
                    <StackPanel>
                        <TextBlock Style="{StaticResource PageEyebrow}" Text="PERSONALIZE"/>
                        <TextBlock Style="{StaticResource PageHeading}" Text="Play in your language."/>
                        <TextBlock Style="{StaticResource PageIntro}" Text="Manage language packs and choose which preset the game uses."/>
                        <Grid>
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="14"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                            <Button x:Name="BtnInstallLang" Grid.Row="0" Grid.Column="0" Style="{StaticResource ActionTileButton}" ToolTip="Choose from supported languages and download">
                                <StackPanel VerticalAlignment="Center"><Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource HintBg}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE8D2;" Style="{StaticResource ActionIcon}" FontSize="21" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                    <TextBlock Text="Add a language" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Browse available packs and add one to your game." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,7,0,0" TextWrapping="Wrap"/></StackPanel>
                            </Button>
                            <Button x:Name="BtnActiveLang" Grid.Row="0" Grid.Column="2" Style="{StaticResource ActionTileButton}" ToolTip="Switch between already installed language packs">
                                <StackPanel VerticalAlignment="Center"><Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource HintBg}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE8AB;" Style="{StaticResource ActionIcon}" FontSize="21" Margin="0" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                    <TextBlock Text="Choose active preset" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Switch between English and your installed packs." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,7,0,0" TextWrapping="Wrap"/></StackPanel>
                            </Button>
                            <Button x:Name="BtnRemoveLang" Grid.Row="1" Grid.Column="0" Style="{StaticResource DangerButton}" MinHeight="138" ToolTip="Safely uninstall a language pack">
                                <StackPanel VerticalAlignment="Center"><Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource CardHoverDanger}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE74D;" FontFamily="Segoe MDL2 Assets" FontSize="21" Foreground="{DynamicResource Red}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                    <TextBlock Text="Remove a pack" FontSize="17" FontWeight="SemiBold"/>
                                    <TextBlock Text="Uninstall safely; shared files used by other packs stay in place." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,7,0,0" TextWrapping="Wrap"/></StackPanel>
                            </Button>
                            <Border Grid.Row="1" Grid.Column="2" Style="{StaticResource HintCard}" Margin="5,0,5,12" VerticalAlignment="Stretch">
                                <StackPanel VerticalAlignment="Center"><TextBlock Text="&#xE8D7;" FontFamily="Segoe MDL2 Assets" FontSize="22" Foreground="{DynamicResource Accent}" Margin="0,0,0,14"/>
                                    <TextBlock Text="Pack-friendly by design" FontSize="15" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                    <TextBlock Style="{StaticResource HintText}" Text="Keep several packs installed at once. Shared files are only removed when no installed pack needs them." Margin="0,7,0,0"/></StackPanel>
                            </Border>
                        </Grid>
                    </StackPanel>
                </ScrollViewer>

                <!-- PAGE 4: DOWNGRADE -->
                <ScrollViewer x:Name="PageDowngrade" Visibility="Collapsed" VerticalScrollBarVisibility="Auto" Padding="0" PanningMode="VerticalOnly" ClipToBounds="True">
                    <StackPanel>
                        <TextBlock Style="{StaticResource PageEyebrow}" Text="COMPATIBILITY"/>
                        <TextBlock Style="{StaticResource PageHeading}" Text="Get back to a playable version."/>
                        <TextBlock Style="{StaticResource PageIntro}" Text="Use these tools if the game reports 'Invalid game version'. Start with the recommended route."/>

                        <!-- Shown by the elevated worker instance during a downgrade. -->
                        <Border x:Name="DowngradeRunBanner" Visibility="Collapsed" CornerRadius="13" Padding="18,15" Margin="5,0,5,18"
                                Background="{DynamicResource StatusBusyBg}" BorderBrush="{DynamicResource StatusBusyBorder}" BorderThickness="1">
                            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                <Border Width="38" Height="38" CornerRadius="12" Background="{DynamicResource HintBg}" Margin="0,0,14,0">
                                    <TextBlock Text="&#xE895;" FontFamily="Segoe MDL2 Assets" FontSize="17" Foreground="{DynamicResource Accent}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                <StackPanel Grid.Column="1" VerticalAlignment="Center">
                                    <TextBlock x:Name="DowngradeRunTitle" Text="Downgrade running" FontSize="15" FontWeight="Bold" Foreground="{DynamicResource Accent}"/>
                                    <TextBlock x:Name="DowngradeRunDetail" FontSize="12.5" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,5,0,0"/>
                                </StackPanel>
                            </Grid>
                        </Border>

                        <Grid>
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="14"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Button x:Name="BtnDowngradeNormal" Grid.Column="0" Style="{StaticResource ActionTileButton}" ToolTip="Safely injects Lua locks and forces validation">
                                <StackPanel VerticalAlignment="Center"><Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource StatusReadyBg}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE777;" FontFamily="Segoe MDL2 Assets" FontSize="21" Foreground="{DynamicResource Green}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                    <TextBlock Text="Normal downgrade" FontSize="17" FontWeight="SemiBold" Foreground="{DynamicResource Green}"/>
                                    <TextBlock Text="Recommended" FontSize="10" FontWeight="Bold" Foreground="{DynamicResource Green}" Margin="0,4,0,0"/>
                                    <TextBlock Text="Adds the version lock and manifests, then asks Steam to validate." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,8,0,0" TextWrapping="Wrap"/></StackPanel>
                            </Button>
                            <Button x:Name="BtnDowngradeNuclear" Grid.Column="2" Style="{StaticResource DangerButton}" MinHeight="138" ToolTip="Wipes Steam files and reinstalls BetterSteamTools">
                                <StackPanel VerticalAlignment="Center"><Border Width="44" Height="44" CornerRadius="14" Background="{DynamicResource CardHoverDanger}" Margin="0,0,0,20">
                                        <TextBlock Text="&#xE7BA;" FontFamily="Segoe MDL2 Assets" FontSize="21" Foreground="{DynamicResource Red}" HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                                    <TextBlock Text="Advanced reset" FontSize="17" FontWeight="SemiBold" Foreground="{DynamicResource Red}"/>
                                    <TextBlock Text="Only use if the normal route fails. Cleans Steam files and reinstalls BetterSteamTools." FontSize="12.5" Foreground="{DynamicResource TextSecondary}" Margin="0,8,0,0" TextWrapping="Wrap"/></StackPanel>
                            </Button>
                        </Grid>
                        <Border Style="{StaticResource HintCard}" Margin="5,10,5,8">
                            <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="Auto"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                                <TextBlock Text="&#xE7BA;" FontFamily="Segoe MDL2 Assets" FontSize="16" Foreground="{DynamicResource Yellow}" Margin="1,0,12,0" VerticalAlignment="Top"/>
                                <TextBlock Grid.Column="1" Style="{StaticResource HintText}" Text="Both options close and restart Steam and require administrator rights. Accept the UAC prompt if it appears."/>
                            </Grid>
                        </Border>
                    </StackPanel>
                </ScrollViewer>

                <!-- PAGE 5: SETTINGS -->
                <ScrollViewer x:Name="PageSettings" Visibility="Collapsed" VerticalScrollBarVisibility="Auto" Padding="0" PanningMode="VerticalOnly" ClipToBounds="True">
                    <StackPanel>
                        <TextBlock Style="{StaticResource PageEyebrow}" Text="PREFERENCES"/>
                        <TextBlock Style="{StaticResource PageHeading}" Text="Make SteamVault yours."/>
                        <TextBlock Style="{StaticResource PageIntro}" Text="Tune the look and motion. Your choices are remembered for next time."/>

                        <Grid>
                            <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="14"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                            <Border Grid.Column="0" Style="{StaticResource Card}" MinHeight="148">
                                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                    <StackPanel VerticalAlignment="Center" Margin="0,0,12,0">
                                        <Border Width="40" Height="40" CornerRadius="13" Background="{DynamicResource HintBg}" HorizontalAlignment="Left" Margin="0,0,0,15">
                                            <TextBlock Text="&#xE706;" FontFamily="Segoe MDL2 Assets" FontSize="19" Foreground="{DynamicResource Accent}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        </Border>
                                        <TextBlock Text="Color theme" FontSize="16" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                        <TextBlock Text="Switch between the warm dark and light palettes." FontSize="12" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,5,0,0"/>
                                    </StackPanel>
                                    <Button x:Name="BtnThemeToggle" Grid.Column="1" Style="{StaticResource SmallButton}" Padding="12,9" VerticalAlignment="Center"
                                            ToolTip="Switch to the light theme">
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock x:Name="ThemeToggleIcon" Text="&#xE706;" FontFamily="Segoe MDL2 Assets" FontSize="14" VerticalAlignment="Center" Margin="0,0,7,0"/>
                                            <TextBlock Text="Change" VerticalAlignment="Center"/>
                                        </StackPanel>
                                    </Button>
                                </Grid>
                            </Border>

                            <Border Grid.Column="2" Style="{StaticResource Card}" MinHeight="148">
                                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                                    <StackPanel VerticalAlignment="Center" Margin="0,0,12,0">
                                        <Border Width="40" Height="40" CornerRadius="13" Background="{DynamicResource HintBg}" HorizontalAlignment="Left" Margin="0,0,0,15">
                                            <TextBlock Text="&#xE769;" FontFamily="Segoe MDL2 Assets" FontSize="19" Foreground="{DynamicResource Accent}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                        </Border>
                                        <TextBlock Text="Motion" FontSize="16" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                        <TextBlock Text="Calm the interface by turning off movement effects." FontSize="12" Foreground="{DynamicResource TextSecondary}" TextWrapping="Wrap" Margin="0,5,0,0"/>
                                    </StackPanel>
                                    <Button x:Name="BtnMotionToggle" Grid.Column="1" Style="{StaticResource SmallButton}" Padding="12,9" VerticalAlignment="Center"
                                            ToolTip="Reduce motion: on">
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock x:Name="MotionToggleIcon" Text="&#xE769;" FontFamily="Segoe MDL2 Assets" FontSize="14" VerticalAlignment="Center" Margin="0,0,7,0"/>
                                            <TextBlock Text="Toggle" VerticalAlignment="Center"/>
                                        </StackPanel>
                                    </Button>
                                </Grid>
                            </Border>
                        </Grid>

                        <Border Style="{StaticResource Card}" Margin="5,0,5,18">
                            <StackPanel>
                                <Grid Margin="0,0,0,18">
                                    <StackPanel>
                                        <TextBlock Text="Accent color" FontSize="16" FontWeight="SemiBold" Foreground="{DynamicResource TextPrimary}"/>
                                        <TextBlock Text="Choose the highlight used across buttons, icons, and selected navigation." FontSize="12" Foreground="{DynamicResource TextSecondary}" Margin="0,5,0,0" TextWrapping="Wrap"/>
                                    </StackPanel>
                                    <Border HorizontalAlignment="Right" VerticalAlignment="Top" Background="{DynamicResource HintBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1" CornerRadius="12" Padding="10,5">
                                        <TextBlock Text="LIVE PREVIEW" FontSize="9" FontWeight="Bold" Foreground="{DynamicResource TextMuted}"/>
                                    </Border>
                                </Grid>
                                <Border Background="{DynamicResource HintBg}" BorderBrush="{DynamicResource Border}" BorderThickness="1" CornerRadius="12" Padding="12,14">
                                    <StackPanel x:Name="AccentDots" Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Center">
                                        <RadioButton x:Name="AccentSteam" Style="{StaticResource AccentDot}" GroupName="AccentChoice" Background="#C7A878" IsChecked="True" ToolTip="Accent color: SteamVault gold"/>
                                        <RadioButton x:Name="AccentTeal" Style="{StaticResource AccentDot}" GroupName="AccentChoice" Background="#12B5A0" ToolTip="Accent color: Teal"/>
                                        <RadioButton x:Name="AccentViolet" Style="{StaticResource AccentDot}" GroupName="AccentChoice" Background="#8B7BFF" ToolTip="Accent color: Violet"/>
                                        <RadioButton x:Name="AccentAmber" Style="{StaticResource AccentDot}" GroupName="AccentChoice" Background="#F2A03D" ToolTip="Accent color: Amber"/>
                                        <RadioButton x:Name="AccentCrimson" Style="{StaticResource AccentDot}" GroupName="AccentChoice" Background="#F2586C" ToolTip="Accent color: Crimson"/>
                                        <TextBlock Text="Selected accent is applied immediately" FontSize="11" Foreground="{DynamicResource TextMuted}" VerticalAlignment="Center" Margin="12,0,0,0"/>
                                    </StackPanel>
                                </Border>
                            </StackPanel>
                        </Border>

                        <Border Style="{StaticResource HintCard}" Margin="5,0,5,8">
                            <TextBlock Style="{StaticResource HintText}" Text="Theme, accent, motion, window size, and your last page are saved locally in your user profile."/>
                        </Border>
                    </StackPanel>
                </ScrollViewer>
            </Grid>

            <GridSplitter Grid.Row="2" Height="5" HorizontalAlignment="Stretch" VerticalAlignment="Center"
                          Background="{DynamicResource Border}" Opacity="0.65" ResizeBehavior="PreviousAndNext" ResizeDirection="Rows"
                          Cursor="SizeNS" ToolTip="Drag to resize the activity log"/>

            <!-- Activity Log & Progress Bar -->
            <Border Grid.Row="3" Margin="16,0,16,12" CornerRadius="14" Background="{DynamicResource BgCard}" BorderBrush="{DynamicResource Border}" BorderThickness="1">
                <Grid>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="46"/>
                        <RowDefinition Height="34"/>
                        <RowDefinition Height="*"/>
                    </Grid.RowDefinitions>
                    <Grid Grid.Row="0" Background="{DynamicResource BgCardHover}">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="*"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>
                        <StackPanel Grid.Column="0" Orientation="Horizontal" VerticalAlignment="Center" Margin="24,0,16,0">
                            <Border Width="30" Height="30" CornerRadius="10" Background="{DynamicResource HintBg}" Margin="0,0,10,0">
                                <TextBlock Text="&#xE81C;" FontFamily="Segoe MDL2 Assets" FontSize="13" Foreground="{DynamicResource Accent}" VerticalAlignment="Center" HorizontalAlignment="Center"/>
                            </Border>
                            <StackPanel VerticalAlignment="Center">
                                <TextBlock Text="ACTIVITY" FontSize="10" Foreground="{DynamicResource TextPrimary}" FontWeight="Bold"/>
                                <TextBlock Text="Live operation feed" FontSize="10" Foreground="{DynamicResource TextMuted}" Margin="0,2,0,0"/>
                            </StackPanel>
                        </StackPanel>

                        <!-- Native Progress Bar -->
                        <ProgressBar x:Name="MainProgressBar" Grid.Column="1" Height="5" Margin="0,0,16,0" VerticalAlignment="Center" Visibility="Collapsed" BorderThickness="0" Background="{DynamicResource BgCard}" Foreground="{DynamicResource Accent}"/>

                        <Border Grid.Column="2" Background="{DynamicResource StatusReadyBg}" BorderBrush="{DynamicResource StatusReadyBorder}" BorderThickness="1" CornerRadius="10" Padding="9,4" Margin="0,0,20,0" VerticalAlignment="Center">
                            <StackPanel Orientation="Horizontal"><Ellipse Width="6" Height="6" Fill="{DynamicResource Green}" VerticalAlignment="Center" Margin="0,0,6,0"/>
                                <TextBlock Text="LIVE" FontSize="9" FontWeight="Bold" Foreground="{DynamicResource Green}" VerticalAlignment="Center"/></StackPanel>
                        </Border>

                    </Grid>
                    <Border Grid.Row="1" Background="{DynamicResource BgSidebar}" BorderBrush="{DynamicResource Border}" BorderThickness="0,0,0,1">
                        <StackPanel Orientation="Horizontal" VerticalAlignment="Center" HorizontalAlignment="Right" Margin="0,0,18,0">
                            <RadioButton x:Name="LogFilterAll" Content="All" Style="{StaticResource FilterChip}" GroupName="LogFilterChoice" IsChecked="True" ToolTip="Show every log line"/>
                            <RadioButton x:Name="LogFilterWarn" Content="Warnings" Style="{StaticResource FilterChip}" GroupName="LogFilterChoice" ToolTip="Show warnings and errors only"/>
                            <RadioButton x:Name="LogFilterError" Content="Errors" Style="{StaticResource FilterChip}" GroupName="LogFilterChoice" ToolTip="Show errors only"/>
                            <ToggleButton x:Name="BtnAutoScroll" Content="Auto-scroll" Style="{StaticResource FilterToggle}" IsChecked="True" Margin="8,0,0,0" ToolTip="Keep the log scrolled to the newest line"/>
                            <Border Width="1" Height="16" Background="{DynamicResource Border}" Margin="10,0,10,0" VerticalAlignment="Center"/>
                            <Button x:Name="BtnSaveLog" Content="Save" Style="{StaticResource LogLinkButton}" Margin="0,0,12,0" ToolTip="Save the activity log to a text file"/>
                            <Button x:Name="BtnCopyLog" Content="Copy" Style="{StaticResource LogLinkButton}" Margin="0,0,12,0" ToolTip="Copy the activity log to the clipboard"/>
                            <Button x:Name="BtnClearLog" Content="Clear" Style="{StaticResource LogLinkButton}" ToolTip="Clear the activity log"/>
                        </StackPanel>
                    </Border>
                    <RichTextBox x:Name="OutputBox" Grid.Row="2" Background="Transparent" Foreground="{DynamicResource TextSecondary}"
                                 FontFamily="Consolas" FontSize="12.5" BorderThickness="0"
                                 IsReadOnly="True" VerticalScrollBarVisibility="Auto" Padding="24,12,20,12"/>
                </Grid>
            </Border>
        </Grid>
    </Grid>
</Window>
"@

# ==========================================
# GUI OVERRIDES & LOGIC
# ==========================================
$global:OutputBox = $null
$global:StatusText = $null
$global:ProgressBar = $null
$global:StatusDot = $null
$global:StatusPill = $null
$global:BusyText = $null
$global:MainWindow = $null
$global:AvailableOptions = @()

# Renders an ErrorRecord exactly like a real PowerShell console does
# (message, "At <file>:<line> char:<n>", the offending source line, the caret
# underline, CategoryInfo and FullyQualifiedErrorId), then appends the details a
# console hides: the exception type chain and the PowerShell call stack.
function Format-ErrorRecord {
    param([Parameter(Mandatory = $true)] $ErrorRecord)

    if ($null -eq $ErrorRecord) { return '' }
    if ($ErrorRecord -isnot [System.Management.Automation.ErrorRecord]) { return [string]$ErrorRecord }

    $sb = [System.Text.StringBuilder]::new()
    $exception = $ErrorRecord.Exception
    $message = if ($exception) { $exception.Message } else { [string]$ErrorRecord }
    [void]$sb.AppendLine($message)

    $invocation = $ErrorRecord.InvocationInfo
    if ($invocation -and $invocation.PositionMessage) { [void]$sb.AppendLine($invocation.PositionMessage.TrimEnd()) }

    [void]$sb.AppendLine(('    + CategoryInfo          : {0}' -f $ErrorRecord.CategoryInfo))
    [void]$sb.AppendLine(('    + FullyQualifiedErrorId : {0}' -f $ErrorRecord.FullyQualifiedErrorId))

    $level = 0
    $current = $exception
    while ($null -ne $current) {
        $label = if ($level -eq 0) { 'Exception' } else { 'InnerException ' + $level }
        [void]$sb.AppendLine(('    + {0}: {1}' -f $label.PadRight(20), $current.GetType().FullName))
        if ($level -gt 0) { [void]$sb.AppendLine(('      {0}' -f $current.Message)) }
        $current = $current.InnerException
        $level++
    }

    if ($ErrorRecord.ScriptStackTrace) {
        [void]$sb.AppendLine('    + ScriptStackTrace    :')
        foreach ($frame in ($ErrorRecord.ScriptStackTrace -split "`r?`n")) { [void]$sb.AppendLine("        $frame") }
    } elseif ($exception -and $exception.StackTrace) {
        [void]$sb.AppendLine('    + StackTrace          :')
        foreach ($frame in ($exception.StackTrace -split "`r?`n")) { [void]$sb.AppendLine("        $($frame.Trim())") }
    }

    return $sb.ToString().TrimEnd()
}

# Severity comes from the colour the caller already passes (Red = error,
# Yellow = warning, Green = success), so every existing call site keeps working
# and the filters have something to filter on without touching the log text.
function Get-LogSeverity {
    param([string]$Color, [string]$Text)
    $severity = 'info'
    $key = if ($Color) { $Color.ToUpperInvariant() } else { '' }
    if ($LogColorAliases.ContainsKey($key)) {
        switch ($LogColorAliases[$key]) {
            'Red'    { $severity = 'error' }
            'Yellow' { $severity = 'warn' }
            'Green'  { $severity = 'ok' }
        }
    }
    if ($Text -match '^\s*\[(ERROR|FAIL|FAILED|UNHANDLED)') { $severity = 'error' }
    elseif ($Text -match '^\s*\[(WARN|CANCEL)') { $severity = 'warn' }
    elseif ($Text -match '^\s*\[(OK|DONE)') { if ($severity -eq 'info') { $severity = 'ok' } }
    return $severity
}

# Which severities the active filter chip lets through.
function Test-LogFilterVisible {
    param([string]$Severity)
    switch ($global:LogFilter) {
        'warn'  { return ($Severity -eq 'warn' -or $Severity -eq 'error') }
        'error' { return ($Severity -eq 'error') }
        default { return $true }
    }
}

# While an action runs, its work happens on the UI thread, so WPF only repaints
# when something gives the dispatcher a turn. This is that turn: a throttled empty
# dispatch at Background priority, which lets render, layout and input through.
# Without it the activity log shows nothing until the whole operation is finished.
function Invoke-UiPump {
    param([switch]$Force)
    if ($null -eq $global:MainWindow) { return }
    if ($global:IsUiPumping -or $global:SuppressUiPump) { return }
    $now = [DateTime]::UtcNow
    if (-not $Force -and ($now - $global:LastUiPumpUtc).TotalMilliseconds -lt 80) { return }
    $global:LastUiPumpUtc = $now
    $global:IsUiPumping = $true
    try {
        $global:MainWindow.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::Background)
    } catch {
    } finally {
        $global:IsUiPumping = $false
    }
}

# Appends one entry to the activity log. Each physical line becomes its own
# paragraph so multi-line error blocks keep their original layout (no re-wrapping).
# Entries are also kept in $global:LogEntries so the filters can rebuild the view,
# and so Save/Copy always produce the complete transcript.
function Add-LogEntry {
    param([string]$Text = '', [string]$Color = '#A0A0B0', [switch]$NoNewline)
    if ($null -eq $global:OutputBox) { return }
    $resolved = Resolve-ThemeColor $Color
    $brush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($resolved)
    $severity = Get-LogSeverity -Color $Color -Text $Text

    if ($NoNewline) {
        # Append to the last line, the way a console keeps a "....." progress
        # line on one row. Nothing is added when the log is empty.
        if ($global:LogEntries.Count -gt 0) {
            $last = $global:LogEntries[$global:LogEntries.Count - 1]
            $run = [System.Windows.Documents.Run]::new($Text)
            $run.Foreground = $brush
            $last.Paragraph.Inlines.Add($run)
            $last.Text += $Text
            if ($global:LogAutoScroll) { $global:OutputBox.ScrollToEnd() }
            Invoke-UiPump
        } else {
            Add-LogEntry -Text $Text -Color $Color
        }
        return
    }

    $stamp = (Get-Date).ToString('HH:mm:ss')
    $stampBrush = Get-ThemeBrush 'TextMuted'
    $firstLine = $true
    foreach ($line in ($Text -split "`r?`n")) {
        $lineText = if ([string]::IsNullOrEmpty($line)) { ' ' } else { $line }
        $para = [System.Windows.Documents.Paragraph]::new()
        $para.Margin = [System.Windows.Thickness]::new(0)
        # Continuation lines get the same width of blank space so blocks stay aligned.
        $stampRun = [System.Windows.Documents.Run]::new($(if ($firstLine) { "[$stamp] " } else { '           ' }))
        $stampRun.Foreground = $stampBrush
        $para.Inlines.Add($stampRun)
        $run = [System.Windows.Documents.Run]::new($lineText)
        $run.Foreground = $brush
        $para.Inlines.Add($run)
        $entryStamp = if ($firstLine) { $stamp } else { '' }
        $global:LogEntries.Add([pscustomobject]@{ Paragraph = $para; Severity = $severity; Text = $lineText; Stamp = $entryStamp })
        if (Test-LogFilterVisible $severity) { $global:OutputBox.Document.Blocks.Add($para) }
        $firstLine = $false
    }

    # Keep the transcript bounded: drop the oldest lines once the cap is passed.
    $overflow = $global:LogEntries.Count - [int]$global:LogMaxLines
    if ($overflow -gt 0) {
        for ($n = 0; $n -lt $overflow; $n++) {
            $old = $global:LogEntries[0]
            if ($null -ne $old -and $null -ne $old.Paragraph) { [void]$global:OutputBox.Document.Blocks.Remove($old.Paragraph) }
            $global:LogEntries.RemoveAt(0)
        }
        $global:LogTrimmed = $true
    }

    if ($global:LogAutoScroll) { $global:OutputBox.ScrollToEnd() }
    # Give WPF a turn so this line is on screen while the action is still running.
    Invoke-UiPump
}

# Rebuilds the visible log from the stored entries, honouring the filter chip.
function Update-LogView {
    if ($null -eq $global:OutputBox) { return }
    $global:OutputBox.Document.Blocks.Clear()
    foreach ($entry in $global:LogEntries) {
        if (Test-LogFilterVisible $entry.Severity) { $global:OutputBox.Document.Blocks.Add($entry.Paragraph) }
    }
    if ($global:LogAutoScroll) { $global:OutputBox.ScrollToEnd() }
}

function Clear-Log {
    if ($null -ne $global:LogEntries) { $global:LogEntries.Clear() }
    if ($null -ne $global:OutputBox) { $global:OutputBox.Document.Blocks.Clear() }
}

# Full transcript (not just the filtered view) with timestamps, for Copy and Save.
function Get-LogPlainText {
    $lines = foreach ($entry in $global:LogEntries) {
        if ($entry.Stamp) { "[$($entry.Stamp)] $($entry.Text)" } else { "           $($entry.Text)" }
    }
    return (($lines -join "`r`n").TrimEnd())
}

function Test-IsElevated {
    try {
        return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    } catch { return $false }
}

# Header written above the transcript by Save, so a bug report that arrives as one
# file answers the first round of questions (version, Windows, PowerShell, rights,
# Steam paths, which folder is in use) without a conversation.
function Get-DiagnosticsHeader {
    $lines = [System.Collections.Generic.List[string]]::new()
    # One aligned "Key: value" row; the value is the part a support thread needs.
    $add = {
        param([string]$Key, [string]$Value)
        if ([string]::IsNullOrWhiteSpace($Value)) { $Value = 'unknown' }
        $lines.Add(('{0}: {1}' -f $Key.PadRight(21), $Value))
    }

    # --- system -------------------------------------------------------------
    $osName = 'unknown'
    try {
        $currentVersion = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
        $osName = ('{0} {1} (build {2}.{3})' -f $currentVersion.ProductName, $currentVersion.DisplayVersion, $currentVersion.CurrentBuild, $currentVersion.UBR).Trim()
        # On Windows 11 the registry still reports ProductName "Windows 10"; the
        # build number is the reliable signal.
        if ([int]$currentVersion.CurrentBuild -ge 22000) { $osName = $osName -replace '^Windows 10', 'Windows 11' }
    } catch {
        try { $osName = [System.Environment]::OSVersion.VersionString } catch { }
    }

    $psVersion = ''
    try {
        $psVersion = 'PowerShell ' + $PSVersionTable.PSVersion.ToString()
        if ($PSVersionTable.ContainsKey('PSEdition')) { $psVersion += ' (' + [string]$PSVersionTable.PSEdition + ')' }
    } catch { $psVersion = 'PowerShell unknown' }

    # --- paths --------------------------------------------------------------
    $steamLibraries = ''
    try { $steamLibraries = (@(Get-SteamLibraries) -join '; ') } catch { }
    $detected = ''
    try { $detected = (@(Find-CallOfDutyHQ) -join '; ') } catch { }
    $gameFolder = $global:SessionGameFolder
    if ([string]::IsNullOrWhiteSpace($gameFolder)) { try { $gameFolder = Get-SavedGameFolder } catch { } }

    # --- run mode -----------------------------------------------------------
    $mode = 'interactive (GUI launched normally)'
    if ($global:RequestedDowngradeMethod) { $mode = 'administrator downgrade run (' + [string]$global:RequestedDowngradeMethod + ')' }
    if ([string]::IsNullOrWhiteSpace($global:ScriptPath)) { $mode += ', started from irm | iex' }

    $lines.Add('SteamVault - MW4 Companion diagnostics')
    $lines.Add(('Saved: ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')))
    $lines.Add('')
    $lines.Add('--- Utility ---')
    & $add 'Version' ('v' + [string]$global:AppVersion)
    & $add 'Run mode' $mode
    & $add 'Elevated' $(if (Test-IsElevated) { 'Yes' } else { 'No' })
    & $add 'Theme / accent' ('{0} / {1}' -f $global:ThemeName, $global:AccentName)
    & $add 'Reduce motion' $(if ($global:ReduceMotion) { 'On' } else { 'Off' })
    & $add 'Page / status' ('{0} / {1}' -f $titles[[int]$global:CurrentPage], $global:StatusState)
    $lines.Add('')
    $lines.Add('--- System ---')
    & $add 'Windows' $osName
    & $add 'PowerShell' $psVersion
    & $add 'Architecture' ([string]$env:PROCESSOR_ARCHITECTURE)
    $lines.Add('')
    $lines.Add('--- Paths ---')
    & $add 'Game folder in use' ([string]$gameFolder)
    & $add 'Game folder detected' $detected
    & $add 'Steam libraries' $steamLibraries
    & $add 'Settings file' ([string]$SettingsFile)
    $lines.Add('')
    $logCount = [int]$global:LogEntries.Count
    $logNote = ('{0} line(s)' -f $logCount)
    if ($global:LogTrimmed) { $logNote += (' - older lines dropped, the log keeps the last ' + [string]$global:LogMaxLines + ' line(s)') }
    & $add 'Log' $logNote

    return (($lines -join "`r`n").TrimEnd())
}

# Everything Save writes: the diagnostics header, a separator, then the transcript.
# Kept as one function so the header cannot drift out of the saved file.
function Get-ReportText {
    $header = ''
    try { $header = (Get-DiagnosticsHeader) + "`r`n`r`n" + ('-' * 72) + "`r`n`r`n" } catch { $header = '' }
    return $header + (Get-LogPlainText) + "`r`n"
}

# Single place that styles the status pill, so every code path stays consistent.
function Set-StatusVisual {
    param([string]$State = 'Ready')
    $global:StatusState = $State
    $suffix = switch ($State) {
        'Processing' { 'Busy' }
        'Failed'     { 'Failed' }
        'Cancelled'  { 'Cancelled' }
        default      { 'Ready' }
    }
    $textColorKey = switch ($suffix) {
        'Busy'      { 'Accent' }
        'Failed'    { 'Red' }
        'Cancelled' { 'Yellow' }
        default     { 'Green' }
    }
    $brush = Get-ThemeBrush $textColorKey
    if ($null -ne $global:StatusText) {
        $global:StatusText.Text = $State
        $global:StatusText.Foreground = $brush
    }
    if ($null -ne $global:StatusDot) { $global:StatusDot.Fill = $brush }
    if ($null -ne $global:BusyText) { $global:BusyText.Text = 'Processing...' }
    if ($null -ne $global:StatusPill) {
        $global:StatusPill.Background = Get-ThemeBrush "Status${suffix}Bg"
        $global:StatusPill.BorderBrush = Get-ThemeBrush "Status${suffix}Border"
    }
}

function Write-Host {
    param(
        [Parameter(Position=0, ValueFromPipeline=$true)] [object]$Object,
        [ConsoleColor]$ForegroundColor = [ConsoleColor]::Gray,
        [switch]$NoNewline
    )
    if ($global:OutputBox -eq $null) { Microsoft.PowerShell.Utility\Write-Host $Object -ForegroundColor $ForegroundColor -NoNewline:$NoNewline; return }
    $text = if ($null -eq $Object) { '' } else { [string]$Object }
    
    if ($text -match '^\s*\[(\d+)\]\s*(.*)') {
        $global:AvailableOptions += $text.Trim()
    }
    
    $color = "#A0A0B0"
    switch ($ForegroundColor) {
        'Red'     { $color = "#FF4D4D" }
        'Yellow'  { $color = "#FFD700" }
        'Green'   { $color = "#4EC9B0" }
        'Cyan'    { $color = "#00A8FF" }
        'Magenta' { $color = "#C586C0" }
        'White'   { $color = "#FFFFFF" }
        'DarkGray'{ $color = "#606070" }
        'DarkYellow' { $color = "#B5CEA8" }
    }
    
    if ($NoNewline) {
        # Same line in the log, exactly like a console (e.g. "....." progress dots).
        Add-LogEntry -Text $text -Color $color -NoNewline
    } else {
        Add-LogEntry -Text $text -Color $color
    }
}

# Write-Warning normally goes to a console window that does not exist in the GUI,
# so its output was being lost entirely. Route it into the activity log instead.
function Write-Warning {
    param([Parameter(Position=0, ValueFromPipeline=$true)] [object]$Message)
    if ($null -eq $global:OutputBox) { Microsoft.PowerShell.Utility\Write-Warning -Message $Message; return }
    $text = if ($Message -is [System.Management.Automation.ErrorRecord]) { Format-ErrorRecord $Message } else { [string]$Message }
    $lines = @($text -split "`r?`n")
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $prefix = if ($i -eq 0) { '[WARN] ' } else { '       ' }
        Add-LogEntry -Text ($prefix + $lines[$i]) -Color '#FFD700'
    }
}

function Write-Progress {
    param($Activity, $Status, $PercentComplete, [switch]$Completed)
    if ($global:StatusText -ne $null) {
        # Replaced PushFrame with a safe UI update to prevent crashes
        $global:StatusText.Dispatcher.Invoke([Action]{
            if ($Completed) {
                $global:ProgressBar.Visibility = "Collapsed"
                $global:ProgressBar.Value = 0
                if ($null -ne $global:BusyText) { $global:BusyText.Text = "Processing..." }
            } else {
                $global:ProgressBar.Visibility = "Visible"
                if ($null -eq $PercentComplete) { $PercentComplete = 0 }
                $global:ProgressBar.Value = $PercentComplete
                if ($Status -and $null -ne $global:BusyText) { $global:BusyText.Text = "$Activity : $Status" }
            }
        }, [System.Windows.Threading.DispatcherPriority]::Background)
    }
}

function Read-Host {
    param([string]$Prompt)
    
    # Keep the question in the activity log so a copied log reads like a transcript.
    if ($null -ne $global:OutputBox) {
        $promptLines = @($Prompt -split "`r?`n")
        Add-LogEntry -Text ('? ' + $promptLines[0]) -Color '#606070'
        for ($i = 1; $i -lt $promptLines.Count; $i++) { Add-LogEntry -Text ('  ' + $promptLines[$i]) -Color '#606070' }
    }

    $themeBrush = { param([string]$Key) Get-ThemeBrush $Key }

    $listStyle      = if ($null -ne $global:MainWindow) { $global:MainWindow.TryFindResource('DialogOptionList') } else { $null }
    $primaryStyle   = if ($null -ne $global:MainWindow) { $global:MainWindow.TryFindResource('DialogPrimaryButton') } else { $null }
    $secondaryStyle = if ($null -ne $global:MainWindow) { $global:MainWindow.TryFindResource('DialogSecondaryButton') } else { $null }

    $win = New-Object System.Windows.Window
    $win.Title = "Action Required"
    # Long prompts (folder paths, backup listings) get a wider dialog so text stays readable.
    $win.Width = if ($Prompt.Length -gt 70 -or $Prompt -match "`n") { 640 } else { 560 }
    $win.MinWidth = 480
    $win.MaxWidth = 760
    $win.MinHeight = 190
    $win.MaxHeight = 680
    $win.SizeToContent = "Height"
    $win.ResizeMode = "NoResize"
    $win.ShowInTaskbar = $false
    $win.FontFamily = "Segoe UI"
    $win.UseLayoutRounding = $true
    $win.Background = & $themeBrush 'PopupBg'
    # The dialog is a separate window, so it does not see the main window's
    # brushes: copy the live palette (theme + accent) in, otherwise every
    # DynamicResource inside it falls back to the default white control colours.
    if ($null -ne $global:MainWindow) {
        foreach ($key in @($ThemePalettes[$global:ThemeName].Keys)) {
            $brush = $global:MainWindow.Resources[$key]
            if ($null -ne $brush) { $win.Resources[$key] = $brush }
        }
    }
    $win.WindowStyle = "ToolWindow"
    if (($global:MainWindow -is [System.Windows.Window]) -and $global:MainWindow.IsVisible) {
        $win.Owner = $global:MainWindow
        $win.WindowStartupLocation = "CenterOwner"
    } else {
        $win.WindowStartupLocation = "CenterScreen"
    }
    
    $grid = New-Object System.Windows.Controls.Grid
    $grid.Margin = "28,24,28,22"
    foreach ($rowIndex in 1..4) {
        $grid.RowDefinitions.Add((New-Object System.Windows.Controls.RowDefinition -Property @{ Height = "Auto" }))
    }

    # Short accent bar instead of a flat dialog header.
    $accentBar = New-Object System.Windows.Controls.Border
    $accentBar.Width = 44; $accentBar.Height = 3; $accentBar.CornerRadius = "2"
    $accentBar.Background = & $themeBrush 'Accent'
    $accentBar.HorizontalAlignment = "Left"; $accentBar.Margin = "0,0,0,16"
    [System.Windows.Controls.Grid]::SetRow($accentBar, 0)
    $null = $grid.Children.Add($accentBar)
    
    $lbl = New-Object System.Windows.Controls.TextBlock
    $lbl.Text = $Prompt
    $lbl.Foreground = & $themeBrush 'TextPrimary'
    $lbl.FontSize = 15; $lbl.LineHeight = 22; $lbl.TextWrapping = "Wrap"; $lbl.Margin = "0,0,0,18"
    [System.Windows.Controls.Grid]::SetRow($lbl, 1)
    $null = $grid.Children.Add($lbl)

    # Footer: keyboard hint on the left, action buttons on the right.
    $footer = New-Object System.Windows.Controls.Grid
    $footer.Margin = "0,22,0,0"
    $footer.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = "Auto" }))
    $footer.ColumnDefinitions.Add((New-Object System.Windows.Controls.ColumnDefinition -Property @{ Width = "*" }))
    [System.Windows.Controls.Grid]::SetRow($footer, 3)
    $null = $grid.Children.Add($footer)

    $hint = New-Object System.Windows.Controls.TextBlock
    $hint.FontSize = 11.5
    $hint.Foreground = & $themeBrush 'TextMuted'
    $hint.VerticalAlignment = "Center"
    $hint.TextWrapping = "Wrap"
    $hint.MaxWidth = 240
    $hint.Margin = "0,0,16,0"
    [System.Windows.Controls.Grid]::SetColumn($hint, 0)
    $null = $footer.Children.Add($hint)
    
    $buttonPanel = New-Object System.Windows.Controls.StackPanel
    $buttonPanel.Orientation = "Horizontal"
    $buttonPanel.HorizontalAlignment = "Right"
    $buttonPanel.VerticalAlignment = "Center"
    [System.Windows.Controls.Grid]::SetColumn($buttonPanel, 1)
    $null = $footer.Children.Add($buttonPanel)
    
    if ($Prompt -match '\[Y/n\]' -or $Prompt -match '\[y/N\]') {
        # -cmatch, not -match: -match is case-insensitive, so '[Y/n]' also matched
        # '[y/N]' and made Yes the default button even on destructive prompts.
        $isDefaultYes = $Prompt -cmatch '\[Y/n\]'
        # Say what Enter actually does: on [y/N] prompts the safe answer is default.
        $hint.Text = if ($isDefaultYes) { 'Tip: Enter confirms Yes. Esc cancels.' } else { 'Tip: Enter keeps the safe answer (No). Esc also cancels.' }
        # The default button is the one drawn in the accent colour, so on a
        # destructive [y/N] prompt No is highlighted and Yes is the quieter option.
        if (-not $isDefaultYes) { $primaryStyle, $secondaryStyle = $secondaryStyle, $primaryStyle }
        
        $btnYes = New-Object System.Windows.Controls.Button
        $btnYes.Content = "Yes"; $btnYes.Margin = "0,0,10,0"
        $btnYes.IsDefault = $isDefaultYes
        $btnYes.Add_Click({ $win.DialogResult = $true; $win.Close() })
        if ($null -ne $primaryStyle) { $btnYes.Style = $primaryStyle }
        else {
            $btnYes.Padding = "15,5"; $btnYes.MinWidth = 90
            $btnYes.Background = & $themeBrush 'Accent'; $btnYes.Foreground = & $themeBrush 'TextOnAccent'
            $btnYes.BorderThickness = 0; $btnYes.Cursor = "Hand"
        }
        
        $btnNo = New-Object System.Windows.Controls.Button
        $btnNo.Content = "No"
        $btnNo.IsDefault = (-not $isDefaultYes)
        $btnNo.Add_Click({ $win.DialogResult = $false; $win.Close() })
        if ($null -ne $secondaryStyle) { $btnNo.Style = $secondaryStyle }
        else {
            $btnNo.Padding = "15,5"; $btnNo.MinWidth = 90
            $btnNo.Background = & $themeBrush 'ButtonBg'; $btnNo.Foreground = & $themeBrush 'TextPrimary'
            $btnNo.BorderThickness = 0; $btnNo.Cursor = "Hand"
        }
        # Escape (or the window's close button) always answers "No": dismissing a
        # prompt must never silently confirm an action.
        $btnNo.IsCancel = $true
        
        $buttonPanel.Children.Add($btnYes) | Out-Null
        $buttonPanel.Children.Add($btnNo) | Out-Null
        
        $win.Content = $grid
        $win.Add_SourceInitialized({ Enable-DarkTitleBar -TargetWindow $win -Dark:($global:ThemeName -eq 'dark') -CaptionKey 'PopupBg' })
        $res = $win.ShowDialog()
        $answer = if ($res -eq $true) { "y" } else { "n" }
        Add-LogEntry -Text ('> ' + $(if ($answer -eq 'y') { 'Yes' } else { 'No' })) -Color '#A0A0B0'
        return $answer
    } else {
        $global:DialogResult = ""
        $inputControl = $null
        $inputHost = $null
        $listControl = $null
        $freeTextBox = $null
        if ($global:AvailableOptions.Count -gt 0) {
            # A plain list instead of a ComboBox: the dropdown popup inside a modal
            # dialog was painting itself detached from its box (and could not be
            # clicked), so the options are now always on screen and scroll instead.
            $listControl = New-Object System.Windows.Controls.ListBox
            if ($null -ne $listStyle) { $listControl.Style = $listStyle }
            $listControl.MinHeight = 40
            $listControl.MaxHeight = 300
            $listControl.SelectionMode = "Single"
            foreach ($opt in $global:AvailableOptions) { $null = $listControl.Items.Add($opt) }
            $listControl.SelectedIndex = 0
            $global:AvailableOptions = @() # Reset

            $inputHost = New-Object System.Windows.Controls.StackPanel
            $null = $inputHost.Children.Add($listControl)
            $inputControl = $listControl

            # Prompts that also accept free text (e.g. a custom folder path) keep a box.
            if ($Prompt -match '(?i)path') {
                $freeTextBox = New-Object System.Windows.Controls.TextBox
                $freeTextBox.Background = & $themeBrush 'InputBg'
                $freeTextBox.Foreground = & $themeBrush 'TextPrimary'
                $freeTextBox.CaretBrush = & $themeBrush 'TextPrimary'
                $freeTextBox.BorderBrush = & $themeBrush 'Border'
                $freeTextBox.BorderThickness = "1"
                $freeTextBox.Padding = "12,8"; $freeTextBox.FontSize = 13.5
                $freeTextBox.MinHeight = 38
                $freeTextBox.Margin = "0,10,0,0"
                $freeTextBox.VerticalContentAlignment = "Center"
                $freeTextBox.ToolTip = "Type a path instead of picking one from the list"
                $null = $inputHost.Children.Add($freeTextBox)
                $inputControl = $freeTextBox
            }
            $hint.Text = "Pick an option, then press Enter (or double-click it)."
        } else {
            $inputControl = New-Object System.Windows.Controls.TextBox
            $inputControl.Background = & $themeBrush 'InputBg'
            $inputControl.Foreground = & $themeBrush 'TextPrimary'
            $inputControl.CaretBrush = & $themeBrush 'TextPrimary'
            $inputControl.BorderBrush = & $themeBrush 'Border'
            $inputControl.Padding = "12,9"; $inputControl.FontSize = 14
            $inputControl.MinHeight = 40
            $inputControl.VerticalContentAlignment = "Center"
            $inputHost = $inputControl
            $hint.Text = "Type a value, then press Enter."
        }
        [System.Windows.Controls.Grid]::SetRow($inputHost, 2)
        $null = $grid.Children.Add($inputHost)
        
        # Selection mapping is unchanged: a numbered line answers with its number,
        # anything typed by hand is returned verbatim.
        $submitAnswer = {
            $answerText = ''
            if ($null -ne $freeTextBox) { $answerText = ([string]$freeTextBox.Text).Trim() }
            if ([string]::IsNullOrWhiteSpace($answerText) -and $null -ne $listControl) {
                $answerText = [string]$listControl.SelectedItem
            }
            if ($answerText -match '^\s*\[(\d+)\]') { $global:DialogResult = $Matches[1] }
            else { $global:DialogResult = $answerText }
            $win.Close()
        }
        if ($null -ne $listControl) { $listControl.Add_MouseDoubleClick({ param($sender, $e) & $submitAnswer }) }

        $btnSubmit = New-Object System.Windows.Controls.Button
        $btnSubmit.Content = "Submit"; $btnSubmit.Margin = "0,0,10,0"
        $btnSubmit.IsDefault = $true
        $btnSubmit.Add_Click($submitAnswer)
        if ($null -ne $primaryStyle) { $btnSubmit.Style = $primaryStyle }
        else {
            $btnSubmit.Padding = "15,5"; $btnSubmit.MinWidth = 90
            $btnSubmit.Background = & $themeBrush 'Accent'; $btnSubmit.Foreground = & $themeBrush 'TextOnAccent'
            $btnSubmit.BorderThickness = 0; $btnSubmit.Cursor = "Hand"
        }
        
        $btnCancel = New-Object System.Windows.Controls.Button
        $btnCancel.Content = "Cancel"
        $btnCancel.IsCancel = $true
        $btnCancel.Add_Click({ $global:DialogResult = ""; $win.Close() })
        if ($null -ne $secondaryStyle) { $btnCancel.Style = $secondaryStyle }
        else {
            $btnCancel.Padding = "15,5"; $btnCancel.MinWidth = 90
            $btnCancel.Background = & $themeBrush 'ButtonBg'; $btnCancel.Foreground = & $themeBrush 'TextPrimary'
            $btnCancel.BorderThickness = 0; $btnCancel.Cursor = "Hand"
        }
        
        $buttonPanel.Children.Add($btnSubmit) | Out-Null
        $buttonPanel.Children.Add($btnCancel) | Out-Null
        
        $win.Content = $grid
        $win.Add_SourceInitialized({ Enable-DarkTitleBar -TargetWindow $win -Dark:($global:ThemeName -eq 'dark') -CaptionKey 'PopupBg' })
        $win.Add_Loaded({ $inputControl.Focus() | Out-Null })
        $win.ShowDialog() | Out-Null
        if ([string]::IsNullOrWhiteSpace($global:DialogResult)) {
            Add-LogEntry -Text '> (cancelled)' -Color '#A0A0B0'
        } else {
            Add-LogEntry -Text ('> ' + $global:DialogResult) -Color '#A0A0B0'
        }
        return $global:DialogResult
    }
}

# ==========================================
# WINDOW CHROME
# ==========================================
# Purely cosmetic: ask DWM to draw a title bar that matches the current theme.
# Unsupported Windows versions simply keep their normal frame.
function Enable-DarkTitleBar {
    param(
        [System.Windows.Window]$TargetWindow,
        [bool]$Dark = $true,
        [string]$CaptionKey = 'BgDark'
    )
    if ($null -eq $TargetWindow) { return }
    try {
        if (-not ('CodebuffTitleBar' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CodebuffTitleBar {
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    public static void Apply(IntPtr handle, int useDark, int caption, int text) {
        if (handle == IntPtr.Zero) { return; }
        int value = useDark;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004+), 19 = older builds.
        if (DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)) != 0) {
            DwmSetWindowAttribute(handle, 19, ref value, sizeof(int));
        }
        // Windows 11 only: paint the caption in the app's own colours instead of
        // the Windows accent. Unsupported builds simply ignore these.
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
        int border = caption;
        DwmSetWindowAttribute(handle, 34, ref border, sizeof(int));
    }
}
'@ -ErrorAction Stop
        }
        $captionColor = ConvertTo-ColorRef (Get-ThemeColor $CaptionKey)
        $textColor = ConvertTo-ColorRef (Get-ThemeColor 'TextPrimary')
        $helper = New-Object System.Windows.Interop.WindowInteropHelper($TargetWindow)
        [CodebuffTitleBar]::Apply($helper.Handle, $(if ($Dark) { 1 } else { 0 }), $captionColor, $textColor)
    } catch { }
}

# "#RRGGBB" -> Win32 COLORREF (0x00BBGGRR), the format DWM expects.
function ConvertTo-ColorRef {
    param([string]$Color)
    if ($Color -match '^#?([0-9A-Fa-f]{6})$') {
        $hex = $Matches[1]
        $r = [Convert]::ToInt32($hex.Substring(0, 2), 16)
        $g = [Convert]::ToInt32($hex.Substring(2, 2), 16)
        $b = [Convert]::ToInt32($hex.Substring(4, 2), 16)
        return [int](($b -shl 16) -bor ($g -shl 8) -bor $r)
    }
    return 0
}

# ==========================================
# WPF SETUP & EVENT HANDLERS
# ==========================================
$reader = (New-Object System.Xml.XmlNodeReader $xaml)
$window = [Windows.Markup.XamlReader]::Load($reader)

$global:OutputBox = $window.FindName("OutputBox")
$global:StatusText = $window.FindName("StatusText")
$global:ProgressBar = $window.FindName("MainProgressBar")
$global:StatusDot = $window.FindName("StatusDot")
$global:StatusPill = $window.FindName("StatusPill")
$global:BusyText = $window.FindName("BusyText")
$global:MainWindow = $window
# One source of truth for the version: the sidebar reads it from $AppVersion.
$window.FindName('VersionText').Text = 'v' + $AppVersion
$window.Add_SourceInitialized({ Enable-DarkTitleBar -TargetWindow $window -Dark:($global:ThemeName -eq 'dark') })

# Log lines keep the brush they were written with, so recolour them when the
# theme changes; otherwise old text stays pale on the new background.
function Update-LogTheme {
    if ($null -eq $global:OutputBox) { return }
    $keys = @($LogColorAliases.Values | Select-Object -Unique)
    $reverse = @{}
    foreach ($palette in $ThemePalettes.Values) {
        foreach ($key in $keys) {
            if ($palette.ContainsKey($key)) { $reverse[([string]$palette[$key]).ToUpperInvariant()] = $key }
        }
    }
    foreach ($hex in $LogColorAliases.Keys) { $reverse[$hex.ToUpperInvariant()] = $LogColorAliases[$hex] }
    # Recolour every stored entry, not just the visible ones, so lines hidden by a
    # filter come back in the right colour when the filter is relaxed again.
    $allParagraphs = @(foreach ($entry in $global:LogEntries) { $entry.Paragraph })
    if ($allParagraphs.Count -eq 0) { $allParagraphs = @($global:OutputBox.Document.Blocks) }
    foreach ($paragraph in $allParagraphs) {
        if ($paragraph -isnot [System.Windows.Documents.Paragraph]) { continue }
        foreach ($inline in @($paragraph.Inlines)) {
            if ($inline -isnot [System.Windows.Documents.Run]) { continue }
            $brush = $inline.Foreground
            if ($brush -isnot [System.Windows.Media.SolidColorBrush]) { continue }
            $hex = ('#' + $brush.Color.R.ToString('X2') + $brush.Color.G.ToString('X2') + $brush.Color.B.ToString('X2')).ToUpperInvariant()
            if ($reverse.ContainsKey($hex)) { $inline.Foreground = Get-ThemeBrush $reverse[$hex] }
        }
    }
}

# Swaps the window's brush resources. Everything else (styles, pages, log,
# dialogs) resolves colours through these keys, so the whole UI follows along.
function Apply-Theme {
    param([string]$Name = 'dark')
    if (-not $ThemePalettes.ContainsKey($Name)) { $Name = 'dark' }
    $global:ThemeName = $Name
    $palette = $ThemePalettes[$Name]
    if ($null -eq $global:MainWindow) { return }
    foreach ($key in $palette.Keys) {
        $global:MainWindow.Resources[$key] = [System.Windows.Media.BrushConverter]::new().ConvertFromString([string]$palette[$key])
    }
    # The accent sits on top of the theme, so it is re-applied after every swap.
    Apply-Accent -Name $global:AccentName
    # Repaint the pieces that are set from code rather than through a binding.
    if ($null -ne $global:GameCardState) {
        Set-GameFolderCard -Title $global:GameCardState.Title -Detail $global:GameCardState.Detail -Icon $global:GameCardState.Icon -ColorKey $global:GameCardState.ColorKey
    }
    if ($null -ne $global:StatusState) { Set-StatusVisual -State $global:StatusState }
    Update-LogTheme
    Update-HeaderChrome
    Enable-DarkTitleBar -TargetWindow $global:MainWindow -Dark:($Name -eq 'dark')
}

# Layers the chosen accent preset on top of the active theme.
function Apply-Accent {
    param([string]$Name = 'Steam')
    if (-not $AccentPresets.Contains($Name)) { $Name = 'Steam' }
    $global:AccentName = $Name
    if ($null -eq $global:MainWindow) { return }
    $preset = $AccentPresets[$Name]
    $triplet = if ($global:ThemeName -eq 'light') { $preset.Light } else { $preset.Dark }
    $keys = @('Accent', 'AccentHover', 'AccentPressed')
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $global:MainWindow.Resources[$keys[$i]] = [System.Windows.Media.BrushConverter]::new().ConvertFromString([string]$triplet[$i])
    }
    Update-HeaderChrome
}

# Keeps the header buttons (accent dots, motion, theme) showing the live state.
function Update-HeaderChrome {
    if ($null -eq $global:MainWindow) { return }
    $dots = [ordered]@{ AccentSteam = 'Steam'; AccentTeal = 'Teal'; AccentViolet = 'Violet'; AccentAmber = 'Amber'; AccentCrimson = 'Crimson' }
    foreach ($dotName in $dots.Keys) {
        $control = $global:MainWindow.FindName($dotName)
        if ($null -ne $control) { $control.IsChecked = ($dots[$dotName] -eq $global:AccentName) }
    }
    $motionIcon = $global:MainWindow.FindName('MotionToggleIcon')
    if ($null -ne $motionIcon) { $motionIcon.Text = if ($global:ReduceMotion) { [string][char]0xE768 } else { [string][char]0xE769 } }
    $motionButton = $global:MainWindow.FindName('BtnMotionToggle')
    if ($null -ne $motionButton) {
        $motionButton.ToolTip = if ($global:ReduceMotion) { 'Reduce motion: on (animations are off)' } else { 'Reduce motion: off (click to calm animations)' }
    }
    $toggleIcon = $global:MainWindow.FindName('ThemeToggleIcon')
    if ($null -ne $toggleIcon) { $toggleIcon.Text = if ($global:ThemeName -eq 'dark') { [string][char]0xE706 } else { [string][char]0xE708 } }
    $toggle = $global:MainWindow.FindName('BtnThemeToggle')
    if ($null -ne $toggle) { $toggle.ToolTip = if ($global:ThemeName -eq 'dark') { 'Switch to the light theme' } else { 'Switch to the dark theme' } }
}

# Reduce-motion: swaps the animated button styles for still ones and stops the
# status pulse. The page transition checks $global:ReduceMotion directly.
function Set-MotionPreference {
    param([bool]$Reduce)
    $global:ReduceMotion = $Reduce
    if ($null -eq $global:MainWindow) { return }
    $cardButtons = @('BtnQuickFix', 'BtnQuickLang', 'BtnInstallFix', 'BtnInstallLang', 'BtnActiveLang', 'BtnDowngradeNormal')
    $dangerButtons = @('BtnRemoveFix', 'BtnRemoveLang', 'BtnDowngradeNuclear')
    $playButtons = @('BtnLaunchBat')
    $cardStyle = $global:MainWindow.TryFindResource($(if ($Reduce) { 'ActionTileButtonMotionless' } else { 'ActionTileButton' }))
    $dangerStyle = $global:MainWindow.TryFindResource($(if ($Reduce) { 'DangerButtonMotionless' } else { 'DangerButton' }))
    $playStyle = $global:MainWindow.TryFindResource($(if ($Reduce) { 'PlayButtonMotionless' } else { 'PlayButton' }))
    foreach ($name in $cardButtons) {
        $button = $global:MainWindow.FindName($name)
        if ($null -ne $button -and $null -ne $cardStyle) { $button.Style = $cardStyle }
    }
    foreach ($name in $playButtons) {
        $button = $global:MainWindow.FindName($name)
        if ($null -ne $button -and $null -ne $playStyle) { $button.Style = $playStyle }
    }
    foreach ($name in $dangerButtons) {
        $button = $global:MainWindow.FindName($name)
        if ($null -ne $button -and $null -ne $dangerStyle) { $button.Style = $dangerStyle }
    }
    $dot = $global:StatusDot
    if ($null -ne $dot) {
        if ($Reduce) {
            if ($null -eq $global:StatusPulseTriggers) { $global:StatusPulseTriggers = @($dot.Triggers) }
            $dot.Triggers.Clear()
            $dot.Opacity = 1
            $dot.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $null)
        } elseif ($null -ne $global:StatusPulseTriggers) {
            $dot.Triggers.Clear()
            foreach ($trigger in $global:StatusPulseTriggers) { $dot.Triggers.Add($trigger) }
        }
    }
    Update-HeaderChrome
}

# Restores the previous window size/position, clamped to the visible desktop.
function Restore-WindowGeometry {
    param($Settings)
    try {
        $width = [double]$Settings.WindowWidth
        $height = [double]$Settings.WindowHeight
        if ($width -ge $window.MinWidth -and $height -ge $window.MinHeight) {
            $window.Width = [Math]::Min($width, [double][System.Windows.SystemParameters]::VirtualScreenWidth)
            $window.Height = [Math]::Min($height, [double][System.Windows.SystemParameters]::VirtualScreenHeight)
        }
        $left = [double]$Settings.WindowLeft
        $top = [double]$Settings.WindowTop
        $virtualRight = [System.Windows.SystemParameters]::VirtualScreenLeft + [System.Windows.SystemParameters]::VirtualScreenWidth
        $virtualBottom = [System.Windows.SystemParameters]::VirtualScreenTop + [System.Windows.SystemParameters]::VirtualScreenHeight
        if ($left -ge [System.Windows.SystemParameters]::VirtualScreenLeft -and $top -ge [System.Windows.SystemParameters]::VirtualScreenTop -and
            $left -lt ($virtualRight - 120) -and $top -lt ($virtualBottom - 120)) {
            $window.WindowStartupLocation = 'Manual'
            $window.Left = $left
            $window.Top = $top
        }
        if ([bool]$Settings.WindowMaximized) { $window.WindowState = 'Maximized' }
    } catch { }
}

$startupSettings = Get-AppSettings
$global:AccentName = if ($AccentPresets.Contains([string]$startupSettings.Accent)) { [string]$startupSettings.Accent } else { 'Steam' }
$global:ReduceMotion = [bool]$startupSettings.ReduceMotion
Apply-Theme -Name ([string]$startupSettings.Theme)
Set-MotionPreference -Reduce $global:ReduceMotion
Restore-WindowGeometry -Settings $startupSettings

# Any exception that escapes an event handler (and therefore never reaches
# Run-Action's catch) is logged with full detail instead of vanishing silently
# or tearing down the window.
$window.Dispatcher.add_UnhandledException({
    param($sender, $e)
    $e.Handled = $true
    $record = [System.Management.Automation.ErrorRecord]::new(
        $e.Exception,
        'UnhandledGuiException',
        [System.Management.Automation.ErrorCategory]::NotSpecified,
        $null)
    $separator = '=' * 72
    Add-LogEntry -Text '' -Color '#FF4D4D'
    Add-LogEntry -Text $separator -Color '#FF4D4D'
    Add-LogEntry -Text 'UNHANDLED ERROR' -Color '#FF4D4D'
    Add-LogEntry -Text $separator -Color '#FF4D4D'
    Add-LogEntry -Text (Format-ErrorRecord $record) -Color '#FF4D4D'
    Add-LogEntry -Text $separator -Color '#FF4D4D'
    $global:ProgressBar.Visibility = "Collapsed"
    $window.FindName("ReadyPanel").Visibility = "Visible"
    $window.FindName("BusyPanel").Visibility = "Collapsed"
    Set-StatusVisual -State 'Failed'
    $global:IsProcessing = $false
})

# Updates the Overview card so the automatic/saved/manual folder state is always visible.
function Set-GameFolderCard {
    param(
        [string]$Title,
        [string]$Detail,
        [string]$Icon = [string][char]0xE73E,
        [string]$ColorKey = 'Green'
    )
    # Remembered so a theme switch can repaint the card without losing its state.
    $global:GameCardState = @{ Title = $Title; Detail = $Detail; Icon = $Icon; ColorKey = $ColorKey }
    if ($null -eq $global:MainWindow) { return }
    $mainWindow = $global:MainWindow
    $mainWindow.FindName('GameCardTitle').Text = $Title
    $pathBlock = $mainWindow.FindName('GamePathText')
    $pathBlock.Text = $Detail
    $pathBlock.Foreground = Get-ThemeBrush 'TextSecondary'
    $iconBlock = $mainWindow.FindName('GameCardIcon')
    $iconBlock.Text = $Icon
    $iconBlock.Foreground = Get-ThemeBrush $ColorKey
}

# Game path detection on startup: a saved folder wins over detection; when nothing
# is found the card explains that the folder can be picked by hand.
$window.Dispatcher.Invoke([action]{
    try {
        $savedFolder = Get-SavedGameFolder
        if ($savedFolder) {
            Set-GameFolderCard -Title 'Game folder set' -Detail "Using your saved folder: $savedFolder" -ColorKey 'Green'
        } else {
            $detected = @(Find-CallOfDutyHQ)
            if ($detected.Count -gt 0) {
                Set-GameFolderCard -Title 'Game detected' -Detail "Detected at: $($detected[0])" -ColorKey 'Green'
            } else {
                Set-GameFolderCard -Title 'Game not detected' -Detail 'Steam could not find Call of Duty HQ. Click "Change folder" to pick it yourself; your choice is remembered next time.' -Icon ([string][char]0xE7BA) -ColorKey 'Yellow'
            }
        }
    } catch {
        Set-GameFolderCard -Title 'Detection failed' -Detail 'See the activity log, then click "Change folder" to pick the game folder.' -Icon ([string][char]0xE7BA) -ColorKey 'Red'
        Add-LogEntry -Text (Format-ErrorRecord $_) -Color '#FF4D4D'
    }
}, "Render")

$window.FindName("BtnClearLog").Add_Click({
    Clear-Log
    Add-LogEntry -Text 'Activity log cleared.' -Color '#606070'
})

$window.FindName("BtnCopyLog").Add_Click({
    $logText = Get-LogPlainText
    if ([string]::IsNullOrWhiteSpace($logText)) { return }
    try {
        [System.Windows.Clipboard]::SetText($logText)
        Add-LogEntry -Text '[OK]   Activity log copied to the clipboard.' -Color '#4EC9B0'
    } catch {
        Add-LogEntry -Text ('[WARN] Could not copy the activity log: ' + $_.Exception.Message) -Color '#FFD700'
    }
})

# Save the whole transcript (all severities, with timestamps) to a text file.
$window.FindName('BtnSaveLog').Add_Click({
    $logText = Get-LogPlainText
    if ([string]::IsNullOrWhiteSpace($logText)) {
        Add-LogEntry -Text 'Nothing to save yet: the activity log is empty.' -Color '#606070'
        return
    }
    $dialog = New-Object Microsoft.Win32.SaveFileDialog
    $dialog.Title = 'Save activity log'
    $dialog.Filter = 'Text file (*.txt)|*.txt|All files (*.*)|*.*'
    $dialog.FileName = 'codhq-utility-log-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.txt'
    if ($dialog.ShowDialog() -ne $true) {
        Add-LogEntry -Text 'Saving the log was cancelled.' -Color '#606070'
        return
    }
    try {
        [IO.File]::WriteAllText($dialog.FileName, (Get-ReportText), [Text.UTF8Encoding]::new($true))
        Add-LogEntry -Text ('[OK]   Activity log saved to ' + $dialog.FileName + ' (with a diagnostics header)') -Color '#4EC9B0'
    } catch {
        Add-LogEntry -Text ('[WARN] Could not save the log: ' + $_.Exception.Message) -Color '#FFD700'
    }
})

# Filter chips + auto-scroll toggle.
$window.FindName('LogFilterAll').Add_Click({ $global:LogFilter = 'all'; Update-LogView })
$window.FindName('LogFilterWarn').Add_Click({ $global:LogFilter = 'warn'; Update-LogView })
$window.FindName('LogFilterError').Add_Click({ $global:LogFilter = 'error'; Update-LogView })
$window.FindName('BtnAutoScroll').Add_Click({
    $global:LogAutoScroll = [bool]$window.FindName('BtnAutoScroll').IsChecked
    if ($global:LogAutoScroll) { $global:OutputBox.ScrollToEnd() }
})

# Manual folder choice: available on the Overview card, always saved for later runs.
$window.FindName('BtnChooseFolder').Add_Click({
    if ($global:IsProcessing) { return }
    $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
    $dialog.Description = 'Select the Call of Duty HQ game folder'
    $dialog.ShowNewFolderButton = $false
    $current = Get-SavedGameFolder
    if (-not $current) {
        $detected = @(Find-CallOfDutyHQ)
        if ($detected.Count -gt 0) { $current = $detected[0] }
    }
    if ($current -and (Test-Path -LiteralPath $current -PathType Container)) { $dialog.SelectedPath = $current }
    if ($dialog.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Add-LogEntry -Text 'Folder selection cancelled. The previous folder is still in use.' -Color '#606070'
        return
    }
    $folder = [IO.Path]::GetFullPath($dialog.SelectedPath).TrimEnd('\')
    # Warn (never block) when the picked folder has none of the usual game markers.
    $layout = Test-GameFolder -Folder $folder
    if (-not $layout.LooksValid) {
        $missing = ($layout.Missing -join ', ')
        $answer = Read-Host "This folder doesn't look like Call of Duty HQ:`n$folder`n`nNone of these were found: $missing`n`nUse it anyway? [y/N]"
        if ($answer -notmatch '^(y|yes)$') {
            Add-LogEntry -Text 'Folder was not changed. The previous folder is still in use.' -Color '#606070'
            return
        }
    }
    if (Save-GameFolder -Folder $folder) {
        Set-GameFolderCard -Title 'Game folder set' -Detail "Saved at: $folder" -ColorKey 'Green'
        Add-LogEntry -Text ('[OK]   Game folder saved: ' + $folder) -Color '#4EC9B0'
        Add-LogEntry -Text '       Every action uses this folder automatically from now on.' -Color '#606070'
    } else {
        # Saving can fail (permissions, read-only profile); still honor the pick now.
        $global:SessionGameFolder = $folder
        Set-GameFolderCard -Title 'Folder not saved' -Detail "Saved settings could not be written. Using this folder for the current session: $folder" -Icon ([string][char]0xE7BA) -ColorKey 'Yellow'
    }
})

$navButtons = @("NavOverview", "NavFixes", "NavLanguages", "NavDowngrade", "NavSettings")
$pages = @("PageOverview", "PageFixes", "PageLanguages", "PageDowngrade", "PageSettings")
$titles = @("Overview", "Fix Installer", "Languages", "Downgrade Version", "Settings")

# Single navigation helper, also used to restore the last opened page.
# Navigation is never blocked: the pages are just views, and a guard here could
# latch on a failed action and leave the whole sidebar dead.
function Show-Page {
    param([int]$Index, [switch]$Force)
    if ($Index -lt 0 -or $Index -ge $pages.Count) { return }
    for ($n = 0; $n -lt $navButtons.Count; $n++) { $window.FindName($navButtons[$n]).IsChecked = ($n -eq $Index) }
    for ($n = 0; $n -lt $pages.Count; $n++) { $window.FindName($pages[$n]).Visibility = if ($n -eq $Index) { 'Visible' } else { 'Collapsed' } }
    $window.FindName('HeaderTitle').Text = $titles[$Index]
    $global:CurrentPage = $Index

    # Fade + slide the incoming page in, unless reduce-motion is on or the window
    # is not visible yet (the startup page restore runs before ShowDialog, and an
    # animation that never ticks would leave the page stuck half transparent).
    $target = $window.FindName($pages[$Index])
    if ($null -eq $target) { return }
    if ($global:ReduceMotion -or -not $window.IsVisible) {
        $target.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $null)
        $target.Opacity = 1
        $target.RenderTransform = [System.Windows.Media.Transform]::Identity
        return
    }
    $slide = [System.Windows.Media.TranslateTransform]::new(0, 0)
    $target.RenderTransform = $slide
    $target.Opacity = 1
    $duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(240))
    $ease = [System.Windows.Media.Animation.CubicEase]::new()
    $ease.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut
    # FillBehavior.Stop leaves the base values (opacity 1, offset 0) in charge once
    # the animation ends, so nothing can latch at a half-faded value.
    $fade = [System.Windows.Media.Animation.DoubleAnimation]::new(0.0, 1.0, $duration)
    $fade.EasingFunction = $ease
    $fade.FillBehavior = [System.Windows.Media.Animation.FillBehavior]::Stop
    $rise = [System.Windows.Media.Animation.DoubleAnimation]::new(16.0, 0.0, $duration)
    $rise.EasingFunction = $ease
    $rise.FillBehavior = [System.Windows.Media.Animation.FillBehavior]::Stop
    $target.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $fade)
    $slide.BeginAnimation([System.Windows.Media.TranslateTransform]::YProperty, $rise)
}

# The page index travels on the control's Tag, and the handler below switches the
# view inline using only global state. GetNewClosure() cannot be used here: a
# closure runs in its own module where the script's functions (Show-Page) are not
# visible, which made every sidebar click fail with
# "The term 'Show-Page' is not recognized". Calling no functions at all makes the
# click path immune to that class of scope problem.
$global:UiNavState = @{ Buttons = $navButtons; Pages = $pages; Titles = $titles }
for ($i = 0; $i -lt $navButtons.Count; $i++) {
    $navButton = $window.FindName($navButtons[$i])
    $navButton.Tag = $i
    $navButton.Add_Click({
        param($sender, $e)
        $state = $global:UiNavState
        $main = $global:MainWindow
        if ($null -eq $state -or $null -eq $main) { return }
        $index = [int]$sender.Tag
        if ($index -lt 0 -or $index -ge $state.Pages.Count) { return }
        for ($n = 0; $n -lt $state.Buttons.Count; $n++) { $main.FindName($state.Buttons[$n]).IsChecked = ($n -eq $index) }
        for ($n = 0; $n -lt $state.Pages.Count; $n++) { $main.FindName($state.Pages[$n]).Visibility = $(if ($n -eq $index) { 'Visible' } else { 'Collapsed' }) }
        $main.FindName('HeaderTitle').Text = $state.Titles[$index]
        $global:CurrentPage = $index

        # Fade + slide the incoming page, unless reduce-motion is on.
        if (-not $global:ReduceMotion -and $main.IsVisible) {
            $target = $main.FindName($state.Pages[$index])
            if ($null -ne $target) {
                $duration = [System.Windows.Duration]::new([TimeSpan]::FromMilliseconds(240))
                $ease = [System.Windows.Media.Animation.CubicEase]::new()
                $ease.EasingMode = [System.Windows.Media.Animation.EasingMode]::EaseOut
                $slide = [System.Windows.Media.TranslateTransform]::new(0, 0)
                $target.RenderTransform = $slide
                $target.Opacity = 1
                $fade = [System.Windows.Media.Animation.DoubleAnimation]::new(0.0, 1.0, $duration)
                $fade.EasingFunction = $ease
                $fade.FillBehavior = [System.Windows.Media.Animation.FillBehavior]::Stop
                $rise = [System.Windows.Media.Animation.DoubleAnimation]::new(16.0, 0.0, $duration)
                $rise.EasingFunction = $ease
                $rise.FillBehavior = [System.Windows.Media.Animation.FillBehavior]::Stop
                $target.BeginAnimation([System.Windows.UIElement]::OpacityProperty, $fade)
                $slide.BeginAnimation([System.Windows.Media.TranslateTransform]::YProperty, $rise)
            }
        }
    })
}

# Theme toggle lives in Settings; the choice is persisted with the other settings.
$window.FindName('BtnThemeToggle').Add_Click({
    $next = if ($global:ThemeName -eq 'dark') { 'light' } else { 'dark' }
    Apply-Theme -Name $next
    $null = Save-ThemeChoice $next
    Add-LogEntry -Text ('[OK]   Switched to the ' + $next + ' theme.') -Color '#4EC9B0'
})

# Accent presets: one click recolours the UI in both themes and is remembered.
# Same Tag trick as the sidebar: no closures, so the script's functions resolve.
$accentDots = [ordered]@{ AccentSteam = 'Steam'; AccentTeal = 'Teal'; AccentViolet = 'Violet'; AccentAmber = 'Amber'; AccentCrimson = 'Crimson' }
foreach ($dotName in $accentDots.Keys) {
    $dot = $window.FindName($dotName)
    $dot.Tag = $accentDots[$dotName]
    $dot.Add_Click({
        param($sender, $e)
        $presetName = [string]$sender.Tag
        Apply-Accent -Name $presetName
        $null = Save-AccentChoice $presetName
        Add-LogEntry -Text ('[OK]   Accent colour set to ' + $AccentPresets[$presetName].Label + '.') -Color '#4EC9B0'
    })
}

# Reduce-motion toggle (motion off = static buttons, no page fade, no status pulse).
$window.FindName('BtnMotionToggle').Add_Click({
    Set-MotionPreference -Reduce (-not $global:ReduceMotion)
    $null = Save-MotionChoice $global:ReduceMotion
    $message = if ($global:ReduceMotion) { '[OK]   Reduce motion on: page animations and the status pulse are off.' } else { '[OK]   Reduce motion off: animations are back on.' }
    Add-LogEntry -Text $message -Color '#4EC9B0'
})

# Remember window size/position and the open page for the next launch.
$window.Add_Closing({
    param($sender, $eventArgs)
    # No UI pumping from inside the close notification.
    $global:SuppressUiPump = $true
    try {
        # Closing mid-action would end the message loop while the work carried on
        # invisibly, so the window is held open until the action has finished. The
        # elevation hand-off sets $global:AllowProgrammaticClose for its own Close().
        if ($global:IsProcessing -and -not $global:AllowProgrammaticClose) {
            $eventArgs.Cancel = $true
            Add-LogEntry -Text '[WARN] An operation is running. This window stays open until it finishes (Ctrl+C in a console, or End task in Task Manager, forces it).' -Color '#FFD700'
            return
        }
        # The administrator worker is a short-lived copy of this window: saving from
        # it would overwrite the layout and last page the real window owns, and it
        # always sits on the Downgrade page.
        if (-not $RunDowngradeOnly) {
            try {
                $null = Save-WindowPreferences -Page ([int]$global:CurrentPage) -Width ([double]$window.Width) -Height ([double]$window.Height) `
                    -Left ([double]$window.Left) -Top ([double]$window.Top) -Maximized ($window.WindowState -eq 'Maximized')
            } catch { }
        }
    } finally {
        $global:SuppressUiPump = $false
    }
})

function Run-Action {
    # -Arguments is splatted into the action block, so a caller never has to build
    # a closure to pass a value (closures cannot see the script's own functions).
    param([scriptblock]$Action, [object[]]$Arguments = @())
    if ($global:IsProcessing) { return }
    # While the administrator window works, this one only follows it: running a
    # second action here would compete with the run that is already going.
    if ($null -ne $global:WorkerWatcher) {
        Add-LogEntry -Text '[WARN] The administrator window is still running. This window follows it as soon as it finishes.' -Color '#FFD700'
        return
    }
    $global:IsProcessing = $true
    $actionFailed = $false
    $actionCancelled = $false
    $actionButtons = @("BtnQuickFix", "BtnQuickLang", "BtnChooseFolder", "BtnLaunchBat", "BtnInstallFix", "BtnRemoveFix",
                       "BtnInstallLang", "BtnActiveLang", "BtnRemoveLang", "BtnDowngradeNormal", "BtnDowngradeNuclear")

    # Everything below runs inside try/finally: a prompt that blows up half way
    # through must never leave the UI stuck in "Processing" with dead buttons.
    try {
    Clear-Log
    $window.FindName("ReadyPanel").Visibility = "Collapsed"
    $window.FindName("BusyPanel").Visibility = "Visible"
    $global:ProgressBar.Value = 0
    $global:ProgressBar.Visibility = "Collapsed"
    Set-StatusVisual -State 'Processing'

    # Disable all action buttons
    foreach ($actionButtonName in $actionButtons) { $window.FindName($actionButtonName).IsEnabled = $false }

    $window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::Background)
    
    try { & $Action @Arguments }
    catch { 
        if ($_.Exception -is [System.OperationCanceledException]) {
            # The user answered "No" or closed a prompt: say so calmly, no error block.
            $actionCancelled = $true
            Add-LogEntry -Text '' -Color '#FFD700'
            Add-LogEntry -Text ('[CANCEL] ' + $_.Exception.Message) -Color '#FFD700'
            Add-LogEntry -Text '' -Color '#FFD700'
        } else {
            # Reproduce the full console error block, plus the call stack a console
            # would not show, instead of only the bare exception message.
            $actionFailed = $true
            $separator = '=' * 72
            Add-LogEntry -Text '' -Color '#FF4D4D'
            Add-LogEntry -Text $separator -Color '#FF4D4D'
            Add-LogEntry -Text 'ACTION FAILED' -Color '#FF4D4D'
            Add-LogEntry -Text $separator -Color '#FF4D4D'
            Add-LogEntry -Text (Format-ErrorRecord $_) -Color '#FF4D4D'
            Add-LogEntry -Text $separator -Color '#FF4D4D'
        }
    }
    
    } finally {
        # Re-enable buttons and settle the status pill, whatever happened above.
        foreach ($actionButtonName in $actionButtons) { $window.FindName($actionButtonName).IsEnabled = $true }
        $global:ProgressBar.Visibility = "Collapsed"
        $window.FindName("ReadyPanel").Visibility = "Visible"
        $window.FindName("BusyPanel").Visibility = "Collapsed"
        if ($actionFailed) { Set-StatusVisual -State 'Failed' }
        elseif ($actionCancelled) { Set-StatusVisual -State 'Cancelled' }
        else { Set-StatusVisual -State 'Ready' }
        $global:IsProcessing = $false
    }
}

# What the administrator run did, written by the worker so the window that comes
# back can report it instead of guessing.
function Write-LastWorkerResult {
    param([string]$Method, [string]$State, [string]$LogPath)
    try {
        $result = @{
            Method     = $Method
            State      = $State
            LogPath    = [string]$LogPath
            FinishedAt = (Get-Date).ToString('o')
        }
        [IO.File]::WriteAllText((Join-Path $SettingsDirectory 'last-run.json'), ($result | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    } catch { }
}

# Reports the administrator run's outcome in this window, including where its log
# was saved. A result older than this run is ignored, so a previous session's
# outcome can never be reported as if it were this one.
function Show-LastWorkerResult {
    param([string]$Method = 'Normal', [DateTime]$StartedAt = [DateTime]::MinValue)
    $label = if ($Method -eq 'Nuclear') { 'Nuclear downgrade' } else { 'Normal downgrade' }
    $logsDir = Join-Path $SettingsDirectory 'logs'

    $result = $null
    try {
        $resultPath = Join-Path $SettingsDirectory 'last-run.json'
        if (Test-Path -LiteralPath $resultPath -PathType Leaf) { $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json }
    } catch { $result = $null }

    $fresh = $false
    if ($null -ne $result) {
        try { $fresh = ([datetime]$result.FinishedAt) -ge $StartedAt.AddSeconds(-10) } catch { $fresh = $false }
    }
    if (-not $fresh) {
        Add-LogEntry -Text ('[WARN] The administrator window closed without reporting a result. Its log is under ' + $logsDir) -Color '#FFD700'
        Set-StatusVisual -State 'Ready'
        return
    }

    $logPath = [string]$result.LogPath
    $where = if ([string]::IsNullOrWhiteSpace($logPath)) { '' } else { ' Log saved to ' + $logPath + '.' }
    switch ([string]$result.State) {
        'Failed' {
            Add-LogEntry -Text ('[WARN] ' + $label + ' failed.' + $where) -Color '#FFD700'
            Set-StatusVisual -State 'Failed'
        }
        'Cancelled' {
            Add-LogEntry -Text ('[OK]   ' + $label + ' was cancelled; nothing was changed.' + $where) -Color '#FFD700'
            Set-StatusVisual -State 'Cancelled'
        }
        default {
            Add-LogEntry -Text ('[OK]   ' + $label + ' finished.' + $where) -Color '#4EC9B0'
            Set-StatusVisual -State 'Ready'
        }
    }
}

# The administrator window writes this the moment it starts. "Start-Process -Verb
# RunAs -PassThru" does not reliably hand back that window's process: it can return
# the shell's elevation stub, which exits immediately. Watching the stub woke this
# window while the administrator one was still on screen, so two windows sat there.
function Get-WorkerHandshakePath { return (Join-Path $SettingsDirectory 'worker.json') }

function Clear-WorkerHandshake {
    try {
        $handshakePath = Get-WorkerHandshakePath
        if (Test-Path -LiteralPath $handshakePath -PathType Leaf) { Remove-Item -LiteralPath $handshakePath -Force }
    } catch { }
}

function Write-WorkerHandshake {
    param([string]$Method = 'Normal')
    try {
        if (-not (Test-Path -LiteralPath $SettingsDirectory -PathType Container)) { $null = New-Item -ItemType Directory -Path $SettingsDirectory -Force }
        $payload = [ordered]@{ Pid = $PID; Method = $Method; StartedAt = (Get-Date).ToString('o') }
        [IO.File]::WriteAllText((Get-WorkerHandshakePath), ($payload | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    } catch { }
}

# Returns the announced id, or 0 when there is no announcement from this run. A
# leftover file from an earlier run is ignored by its timestamp.
function Get-WorkerHandshakePid {
    param([DateTime]$Since = [DateTime]::MinValue)
    try {
        $handshakePath = Get-WorkerHandshakePath
        if (-not (Test-Path -LiteralPath $handshakePath -PathType Leaf)) { return 0 }
        $info = Get-Content -LiteralPath $handshakePath -Raw | ConvertFrom-Json
        if ([datetime]$info.StartedAt -lt $Since.AddSeconds(-15)) { return 0 }
        return [int]$info.Pid
    } catch { return 0 }
}

# Waits for the administrator window to exit, then brings this window back. The
# replacement cannot be a new process: one started from here would inherit this
# process's token instead of the normal user's, so this window steps aside (it
# minimizes - hiding would end the modal loop and quit the app) and returns.
function Start-WorkerWatcher {
    param([int]$WorkerProcessId, [string]$Method = 'Normal')
    if ($null -ne $global:WorkerWatcher) { $global:WorkerWatcher.Stop(); $global:WorkerWatcher = $null }
    $global:WorkerWatcherPid = $WorkerProcessId
    $global:WorkerWatcherFallbackPid = $WorkerProcessId
    $global:WorkerWatcherAnnounced = $false
    $global:WorkerWatcherMethod = $Method
    $global:WorkerWatcherStartedAt = Get-Date

    $timer = New-Object System.Windows.Threading.DispatcherTimer
    $timer.Interval = [TimeSpan]::FromMilliseconds(500)
    # Only globals: this tick runs long after the function that created it returned.
    $timer.Add_Tick({
        $announcedPid = Get-WorkerHandshakePid -Since $global:WorkerWatcherStartedAt
        if ($announcedPid -gt 0) {
            $global:WorkerWatcherPid = $announcedPid
            $global:WorkerWatcherAnnounced = $true
        } elseif (-not $global:WorkerWatcherAnnounced) {
            $global:WorkerWatcherPid = $global:WorkerWatcherFallbackPid
        }
        $pidToWatch = [int]$global:WorkerWatcherPid
        if ($pidToWatch -le 0) { return }
        $running = $false
        try { $running = [bool](Get-Process -Id $pidToWatch -ErrorAction SilentlyContinue) } catch { $running = $false }
        if ($running) { return }
        # Without an announcement the id may be the elevation stub, so give the real
        # window a few seconds to introduce itself before believing it is gone.
        if (-not $global:WorkerWatcherAnnounced -and ((Get-Date) - $global:WorkerWatcherStartedAt).TotalSeconds -lt 8) { return }
        if ($null -ne $global:WorkerWatcher) { $global:WorkerWatcher.Stop(); $global:WorkerWatcher = $null }
        $mainWindow = $global:MainWindow
        if ($null -eq $mainWindow) { return }
        try {
            $restore = $global:WorkerWatcherRestoreState
            if ($restore -ne 'Maximized') { $restore = 'Normal' }
            $mainWindow.WindowState = $restore
            [void]$mainWindow.Activate()
        } catch { }
        try { Show-LastWorkerResult -Method $global:WorkerWatcherMethod -StartedAt $global:WorkerWatcherStartedAt } catch { }
        Clear-WorkerHandshake
    })
    $global:WorkerWatcher = $timer
    $timer.Start()
}

# Downgrade buttons: run in place when already elevated, otherwise relaunch this
# same file elevated with -RunDowngradeOnly. $global:ScriptPath is captured at
# startup because inside a click handler $MyInvocation.MyCommand.Definition is the
# handler's own code block, not the path of the script.
function Invoke-DowngradeWithElevation {
    param([ValidateSet('Normal', 'Nuclear')][string]$Method)

    # Nuclear deletes files from the Steam folder, so it always asks first and
    # states exactly what goes. The confirmation sits here, before elevation, so
    # it is shown once whether the app is already elevated or has to relaunch.
    if ($Method -eq 'Nuclear') {
        $warning = @'
NUCLEAR DOWNGRADE (Advanced) - this is destructive.

It deletes everything inside your Steam folder except:
  appcache, config, steam, steamapps, userdata and steam.exe

Then it closes Steam, starts it so it can rebuild what was removed, closes
it again and runs the BetterSteamTools installer from
https://ps.lua.tools/install-ost.ps1.

After that it runs the normal downgrade: 1938090.lua is replaced with the
version-locked copy, this game's manifests are deleted from depotcache, and
Steam is asked to validate your files.

Steam restarts several times and administrator rights are required. Use this
only when the normal downgrade has failed.

If the fix is still applied, you will be asked to revert it and delete its
"og files" backups before the downgrade starts.

Run the Nuclear downgrade now? [y/N]
'@
        if ((Read-Host $warning) -notmatch '^(y|yes)$') {
            # Not a failure: report it calmly and keep the status pill on Ready.
            Add-LogEntry -Text '[CANCEL] Nuclear downgrade cancelled. Nothing in your Steam folder was touched.' -Color '#FFD700'
            return
        }
        Add-LogEntry -Text '[OK]   Nuclear downgrade confirmed.' -Color '#4EC9B0'
    }

    $isAdmin = Test-IsElevated
    if ($isAdmin) {
        # Called straight, not through Run-Action: this function already runs inside
        # an action, and Run-Action ignores a nested call while one is in progress
        # (that guard is what makes the button do nothing when the app is elevated).
        # Revert an applied fix first: its files and backups belong to the version
        # that is about to be replaced.
        Clear-FixBeforeDowngrade -Method $Method
        Invoke-SteamDowngrade -Method $Method
        return
    }

    Clear-Log
    Add-LogEntry -Text ('[WARN] Administrator privileges are required for the ' + $Method + ' downgrade. Accept the UAC prompt to continue.') -Color '#FFD700'
    $scriptPath = $global:ScriptPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        Add-LogEntry -Text '[WARN] Cannot relaunch as administrator: the path of this script is unknown. Start PowerShell as administrator and run the file again.' -Color '#FFD700'
        return
    }
    try {
        # Any handshake file belongs to an earlier run; the worker writes its own.
        Clear-WorkerHandshake
        # -DowngradeMethod keeps the chosen method across the elevation boundary.
        $elevated = Start-Process powershell.exe -Verb RunAs -PassThru -ArgumentList "-NoProfile -STA -ExecutionPolicy Bypass -File `"$scriptPath`" -RunDowngradeOnly -DowngradeMethod $Method"
        # The elevated instance is the one that runs the downgrade, and it shows its
        # own window with the live log. This window hides rather than closes, so it
        # can come back with the result once the administrator window is gone.
        Add-LogEntry -Text ('[OK]   Elevated window started. The ' + $Method + ' downgrade runs there; its activity log shows the progress.') -Color '#4EC9B0'
        Add-LogEntry -Text '       This window minimizes and comes back when that run is over.' -Color '#606070'
        Invoke-UiPump -Force
        $workerId = 0
        try { $workerId = [int]$elevated.Id } catch { $workerId = 0 }
        if ($workerId -le 0) {
            Add-LogEntry -Text '[WARN] The administrator window could not be tracked, so this window stays open; the downgrade still runs there.' -Color '#FFD700'
            return
        }
        # Minimize, never Hide(): hiding a window opened with ShowDialog ends the
        # dialog and the process would exit while the downgrade is still running.
        $global:WorkerWatcherRestoreState = $window.WindowState
        $window.WindowState = 'Minimized'
        Add-LogEntry -Text ('       Window minimized (state: ' + [string]$window.WindowState + '). It stays out of the way until the administrator window closes.') -Color '#606070'
        Start-WorkerWatcher -WorkerProcessId $workerId -Method $Method
    } catch {
        # Nothing was handed over, so make sure this window is on screen.
        try { if ($window.WindowState -eq 'Minimized') { $window.WindowState = 'Normal'; [void]$window.Activate() } } catch { }
        Add-LogEntry -Text ('[WARN] Elevation was refused or failed: ' + $_.Exception.Message) -Color '#FFD700'
    }
}

# Every action button shares one handler. The action key travels on the button's
# Tag and the map lives in script scope: same no-closure pattern as the sidebar
# and the accent dots, so no handler depends on the scope it is invoked from.
$global:ActionMap = @{
    'InstallFix'       = { Install-Fix }
    'InstallLanguage'  = { Install-LanguagePack }
    'LaunchBatch'      = { Launch-BatchFile }
    'RemoveFix'        = { Remove-Fix }
    'ActiveLanguage'   = { Set-ActiveLanguagePreset }
    'RemoveLanguage'   = { Remove-LanguagePack }
    'DowngradeNormal'  = { Invoke-DowngradeWithElevation -Method 'Normal' }
    'DowngradeNuclear' = { Invoke-DowngradeWithElevation -Method 'Nuclear' }
}

$actionButtonMap = [ordered]@{
    BtnQuickFix        = 'InstallFix'
    BtnQuickLang       = 'InstallLanguage'
    BtnLaunchBat       = 'LaunchBatch'
    BtnInstallFix      = 'InstallFix'
    BtnRemoveFix       = 'RemoveFix'
    BtnInstallLang     = 'InstallLanguage'
    BtnActiveLang      = 'ActiveLanguage'
    BtnRemoveLang      = 'RemoveLanguage'
    BtnDowngradeNormal = 'DowngradeNormal'
    BtnDowngradeNuclear = 'DowngradeNuclear'
}

foreach ($buttonName in $actionButtonMap.Keys) {
    $actionButton = $window.FindName($buttonName)
    if ($null -eq $actionButton) { continue }
    $actionButton.Tag = $actionButtonMap[$buttonName]
    $actionButton.Add_Click({
        param($sender, $e)
        $map = $global:ActionMap
        if ($null -eq $map) { return }
        $actionKey = [string]$sender.Tag
        if (-not $map.ContainsKey($actionKey)) { return }
        $action = $map[$actionKey]
        if ($null -eq $action) { return }
        Run-Action -Action $action
    })
}

# Makes it obvious that this window is the administrator worker, and what it is
# busy with, instead of looking like an ordinary idle session.
function Set-DowngradeRunChrome {
    param([string]$Method = 'Normal')
    try {
        $label = if ($Method -eq 'Nuclear') { 'Nuclear downgrade' } else { 'Normal downgrade' }
        $window.Title = 'SteamVault - ' + $label + ' running (Administrator)'
        $banner = $window.FindName('DowngradeRunBanner')
        if ($null -eq $banner) { return }
        $window.FindName('DowngradeRunTitle').Text = $label + ' running'
        $window.FindName('DowngradeRunDetail').Text = if ($Method -eq 'Nuclear') {
            'This is the administrator window. It cleans the Steam folder, reinstalls BetterSteamTools and restarts Steam, so it can take several minutes. Watch the activity log below; do not close this window.'
        } else {
            'This is the administrator window. It closes Steam, writes the version-locked files and asks Steam to validate. Watch the activity log below; do not close this window.'
        }
        $banner.Visibility = 'Visible'
    } catch { }
}

# Finishes the administrator window. The banner reports the result and the log is
# saved automatically, then the window waits for the user to press OK. That press is
# the hand-back: closing this window restores the one that minimized for the UAC
# hand-off, which reports the same outcome. Nothing closes on a timer any more, so a
# finished run cannot vanish while someone is still reading the log.
function Complete-DowngradeRunChrome {
    param([string]$Method = 'Normal', [string]$State = 'Ready')
    try {
        $label = if ($Method -eq 'Nuclear') { 'Nuclear downgrade' } else { 'Normal downgrade' }
        $window.Title = 'SteamVault - Administrator'

        $savedPath = $null
        try {
            $logDir = Join-Path $SettingsDirectory 'logs'
            if (-not (Test-Path -LiteralPath $logDir -PathType Container)) { $null = New-Item -ItemType Directory -Path $logDir -Force }
            $savedPath = Join-Path $logDir ('downgrade-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.txt')
            [IO.File]::WriteAllText($savedPath, (Get-ReportText), [Text.UTF8Encoding]::new($true))
            # Keep the folder small: only the newest handful of runs are worth keeping.
            $stale = @(Get-ChildItem -LiteralPath $logDir -Filter 'downgrade-*.txt' -File -ErrorAction SilentlyContinue |
                       Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 10)
            foreach ($staleLog in $stale) { try { Remove-Item -LiteralPath $staleLog.FullName -Force -ErrorAction Stop } catch { } }
        } catch { $savedPath = $null }

        $banner = $window.FindName('DowngradeRunBanner')
        if ($null -eq $banner) { return }
        $window.FindName('DowngradeRunTitle').Text = switch ($State) {
            'Failed'    { $label + ' failed' }
            'Cancelled' { $label + ' cancelled' }
            default     { $label + ' finished' }
        }
        $summary = if ($State -eq 'Failed') {
            'The activity log above holds the error and call stack.'
        } elseif ($State -eq 'Cancelled') {
            'Nothing was changed.'
        } else {
            'Every step it took is in the activity log above.'
        }
        if ($savedPath) { $summary += (' Log saved to ' + $savedPath + '.') }
        # Lets the window that comes back report this run instead of guessing.
        Write-LastWorkerResult -Method $Method -State $State -LogPath ([string]$savedPath)

        $banner.Visibility = 'Visible'
        $verb = switch ($State) { 'Failed' { 'failed' } 'Cancelled' { 'cancelled' } default { 'finished' } }
        $window.FindName('DowngradeRunDetail').Text = ($summary + ' Press OK on the message box to close this administrator window.')
        Add-LogEntry -Text ('[OK]   ' + $label + ' ' + $verb + '.') -Color '#4EC9B0'

        # A real dialog, not an inline button: this is the moment the elevated
        # session ends. Its OK closes the window, and closing the window is what
        # wakes the one that minimized for the hand-off.
        $icon = switch ($State) {
            'Failed'    { [System.Windows.MessageBoxImage]::Error }
            'Cancelled' { [System.Windows.MessageBoxImage]::Warning }
            default     { [System.Windows.MessageBoxImage]::Information }
        }
        $prompt = $summary + "`n`nThe window you started from comes back as soon as you close this one."
        # Let the banner and the last log line paint before the dialog covers them.
        try { $window.Dispatcher.Invoke([Action]{}, [System.Windows.Threading.DispatcherPriority]::Background) } catch { }
        [void][System.Windows.MessageBox]::Show($window, $prompt, ($label + ' ' + $verb), [System.Windows.MessageBoxButton]::OK, $icon)
        $global:AllowProgrammaticClose = $true
        $window.Close()
    } catch { }
}

if ($RunDowngradeOnly) {
    # The method travels with the relaunch. It is parked in a global first because
    # the dispatcher block must not depend on the scope it is invoked from.
    $global:RequestedDowngradeMethod = $DowngradeMethod
    # Announce this process so the window that launched it waits for the window, not
    # for whatever handle the elevating shell returned.
    Write-WorkerHandshake -Method $DowngradeMethod
    Show-Page -Index 3 -Force
    Set-DowngradeRunChrome -Method $DowngradeMethod
    # Queued, not Invoked. A Dispatcher.Invoke at Render priority ran the entire
    # downgrade before ShowDialog was reached, so the window only appeared once the
    # work had finished and the run looked silent. InvokeAsync at background
    # priority runs after the window has been laid out and painted.
    $null = $window.Dispatcher.InvokeAsync([Action]{
        $action = {
            param($chosen)
            # Logged inside the action: Run-Action clears the log before it starts.
            Add-LogEntry -Text ('=== ' + $chosen + ' downgrade started in the administrator window ===') -Color '#C586C0'
            Add-LogEntry -Text '    This can take several minutes; the log below updates live.' -Color '#606070'
            # Revert an applied fix first: its files and backups belong to the version
            # that is about to be replaced.
            Clear-FixBeforeDowngrade -Method $chosen
            Invoke-SteamDowngrade -Method $chosen
        }
        $runState = 'Ready'
        try {
            Run-Action -Action $action -Arguments @([string]$global:RequestedDowngradeMethod)
            # Run-Action settles the status pill, so it is the honest result.
            $runState = [string]$global:StatusState
        } catch {
            $runState = 'Failed'
        } finally {
            Complete-DowngradeRunChrome -Method ([string]$global:RequestedDowngradeMethod) -State $runState
        }
    }, [System.Windows.Threading.DispatcherPriority]::Background)
} else {
    Show-Page -Index ([int]$startupSettings.LastPage) -Force
    Add-LogEntry -Text 'Ready. Choose an action from the sidebar to begin.' -Color '#606070'
}

# Handlers created in script scope resolve the script's functions normally, but a
# handler that ends up invoked from an isolated scope cannot (that is what a
# GetNewClosure() scriptblock did to the sidebar). Publishing the shared state and
# the helper functions to the global scope is the cheap safety net, so a handler
# always resolves the same names the script body does.
foreach ($sharedName in @('window', 'ThemePalettes', 'AccentPresets', 'LogColorAliases', 'SettingsFile', 'SettingsDirectory', 'AppId',
                          'pages', 'navButtons', 'titles', 'ZipUrl', 'ZipFileName', 'LuaDownloadUrl', 'ZipDownloadUrl', 'ReleaseTag',
                          'SourceFileName', 'TargetFileName', 'SteamConfigSubPath', 'AppManifestFile', 'codDepotIds')) {
    $sharedValue = Get-Variable -Name $sharedName -ValueOnly -ErrorAction SilentlyContinue
    if ($null -ne $sharedValue) { Set-Variable -Name $sharedName -Value $sharedValue -Scope Global -Force }
}
# A scope-qualified Set-Item ("Function:\global:Name") does NOT create the
# function globally - it lands in the current scope - so each definition is
# re-declared with an explicit "function global:" prefix, which is the form the
# language routes to the global scope.
foreach ($helper in @(Get-ChildItem -Path Function:)) {
    try {
        $declaration = 'function global:' + $helper.Name + " {`n" + $helper.Definition + "`n}"
        . ([scriptblock]::Create($declaration))
    } catch { }
}

$window.ShowDialog() | Out-Null
