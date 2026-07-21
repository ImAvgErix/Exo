using System.Text.Json;
using System.Text.RegularExpressions;
using Exo.Helpers;
using Microsoft.Win32;

namespace Exo.Services;

/// <summary>
/// Pure C# Riot / Epic apply: high-perf GPU + FSO off for games, launcher
/// integrated on hybrid, startup quiet, shell toasts off, DSCP when elevated.
/// Never touches anti-cheat, services, or game binaries.
/// </summary>
public static class LauncherNativeApply
{
    private const string GpuHighPerf = "GpuPreference=2;";
    private const string GpuPowerSave = "GpuPreference=1;";
    private const string FsoDisable = "~ DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static NativeApplyResult Apply(string module, bool experimental, IProgress<string>? progress = null)
    {
        module = module.ToLowerInvariant();
        if (module is not ("riot" or "epic"))
            return NativeApplyResult.Fail(module, "Unknown launcher module");

        var steps = new List<NativeApplyStep>();
        var elevOps = new List<string>();
        var admin = NativeReg.IsAdministrator();
        void Report(string msg) => progress?.Report(msg);

        Report($"Discovering {module} targets...");
        var games = module == "riot" ? DiscoverRiotGames() : DiscoverEpicGames();
        var launchers = module == "riot" ? DiscoverRiotLaunchers() : DiscoverEpicLaunchers();

        if (games.Count == 0 && launchers.Count == 0 && !IsInstalled(module))
        {
            return NativeApplyResult.Fail(module, $"{module} does not appear to be installed.");
        }

        Report("Removing noisy startup entries...");
        steps.Add(RemoveStartup(module, launchers));

        Report("Shell notifications quiet...");
        steps.Add(QuietShell(module));

        Report("Game GPU high-perf + FSO off...");
        steps.Add(ApplyGpuFso(games, launchers, elevOps, admin, module));

        Report("Per-game DSCP...");
        steps.Add(ApplyDscp(games, module, admin, elevOps));

        // Product policy: no always-on background companions (Run-key yield loops).
        // One-shot apply only: GPU, FSO, startup quiet, caches. Zero idle processes.
        Report("Removing background companions...");
        steps.Add(PurgeYieldCompanion(module));

        Report("Launcher cache clean...");
        steps.Add(ClearLauncherCaches(module));

        Report("Quiet Start Menu launcher...");
        steps.Add(InstallQuietStartMenu(module, launchers));

        Report("Safety snapshot...");
        steps.Add(WriteSnapshot(module, games, launchers));

        var gpuOk = steps.Any(s => s.Id == "gpu-fso" && s.Status is "ok" or "partial");
        var essentialOk = (gpuOk || games.Count == 0) &&
                          steps.Any(s => s.Id == "startup" && s.Status == "ok");
        if (games.Count == 0)
            steps.Add(new NativeApplyStep { Id = "games", Status = "ok", Reason = "no game EXEs yet — launch once then reapply" });

        SaveState(module, essentialOk, experimental, games, launchers, steps, elevOps);

        return new NativeApplyResult
        {
            Ok = essentialOk,
            Module = module,
            Message = essentialOk
                ? $"{char.ToUpper(module[0]) + module[1..]} optimized (native C#)"
                : $"{module} native apply incomplete",
            Steps = steps,
            NeedsElevation = elevOps.Count > 0 && !admin,
            ElevatedHklmOps = elevOps
        };
    }

    private static bool IsInstalled(string module)
    {
        if (module == "riot")
        {
            if (Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Riot Vanguard")))
                return true;
            if (Directory.Exists(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "Riot Games")))
                return true;
        }
        else
        {
            var roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Epic Games"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic")
            };
            if (roots.Any(Directory.Exists)) return true;
        }
        return false;
    }

    private static List<string> DiscoverRiotGames()
    {
        var list = new List<string>();
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var roots = new List<string>();
        var riotRoot = Path.Combine(drive, "Riot Games");
        if (Directory.Exists(riotRoot)) roots.Add(riotRoot);

        foreach (var root in EnumerateUninstallRoots("Riot|VALORANT|League of Legends"))
            if (!roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);

        var knownRels = new[]
        {
            @"VALORANT\live\VALORANT\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            @"VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe",
            @"VALORANT\VALORANT.exe",
            @"League of Legends\Game\League of Legends.exe",
            @"League of Legends\League of Legends.exe"
        };

        foreach (var root in roots)
        {
            if (root.Contains("Vanguard", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var rel in knownRels)
            {
                var full = Path.Combine(root, rel);
                if (File.Exists(full) && !list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    list.Add(Path.GetFullPath(full));
            }
        }

        // Running processes
        foreach (var name in new[] { "VALORANT-Win64-Shipping", "VALORANT", "League of Legends" })
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try
                    {
                        var path = p.MainModule?.FileName;
                        if (!string.IsNullOrEmpty(path) && File.Exists(path) &&
                            !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                            list.Add(Path.GetFullPath(path));
                    }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        return list;
    }

    private static List<string> DiscoverRiotLaunchers()
    {
        var list = new List<string>();
        var drive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        foreach (var rel in new[]
                 {
                     @"Riot Games\Riot Client\RiotClientServices.exe",
                     @"Riot Games\Riot Client\RiotClientUx.exe",
                     @"Riot Games\Riot Client\RiotClientUxRender.exe"
                 })
        {
            var full = Path.Combine(drive, rel);
            if (File.Exists(full)) list.Add(Path.GetFullPath(full));
        }
        return list;
    }

    private static List<string> DiscoverEpicGames()
    {
        var list = new List<string>();
        var manifestRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestRoot)) return list;

        foreach (var file in Directory.EnumerateFiles(manifestRoot, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var launch = root.TryGetProperty("LaunchExecutable", out var le) ? le.GetString() : null;
                var install = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                if (string.IsNullOrWhiteSpace(launch) || string.IsNullOrWhiteSpace(install)) continue;
                if (launch.Contains("EpicGamesLauncher", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.Combine(install, launch);
                if (File.Exists(full) && !list.Contains(full, StringComparer.OrdinalIgnoreCase))
                    list.Add(Path.GetFullPath(full));

                // Rocket League (and similar) ship LaunchExecutable=Launcher.exe — also pin the real game EXE.
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    foreach (var sibling in Directory.EnumerateFiles(dir, "*.exe"))
                    {
                        var leaf = Path.GetFileName(sibling);
                        if (leaf.Equals("Launcher.exe", StringComparison.OrdinalIgnoreCase)) continue;
                        if (leaf.Contains("Crash", StringComparison.OrdinalIgnoreCase)) continue;
                        if (leaf.Contains("Unins", StringComparison.OrdinalIgnoreCase)) continue;
                        if (leaf.Contains("Redist", StringComparison.OrdinalIgnoreCase)) continue;
                        // Prefer shipping / game-named binaries
                        if (leaf.Contains("Shipping", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Equals("RocketLeague.exe", StringComparison.OrdinalIgnoreCase) ||
                            leaf.Contains("Game", StringComparison.OrdinalIgnoreCase) ||
                            File.Exists(sibling) && new FileInfo(sibling).Length > 5_000_000)
                        {
                            var sp = Path.GetFullPath(sibling);
                            if (!list.Contains(sp, StringComparer.OrdinalIgnoreCase))
                                list.Add(sp);
                        }
                    }
                }
            }
            catch { }
        }
        return list;
    }

    private static List<string> DiscoverEpicLaunchers()
    {
        var list = new List<string>();
        foreach (var root in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "Epic Games", "Launcher", "Portal", "Binaries", "Win64"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         "Epic Games", "Launcher", "Portal", "Binaries", "Win64")
                 })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var name in new[] { "EpicGamesLauncher.exe", "EpicWebHelper.exe" })
            {
                var full = Path.Combine(root, name);
                if (File.Exists(full)) list.Add(Path.GetFullPath(full));
            }
        }
        return list;
    }

    private static List<string> EnumerateUninstallRoots(string namePattern)
    {
        var found = new List<string>();
        var re = new Regex(namePattern, RegexOptions.IgnoreCase);
        foreach (var (hive, path) in new[]
                 {
                     (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            RegistryKey? root = null;
            try { root = hive.OpenSubKey(path); } catch { continue; }
            if (root is null) continue;
            using (root)
            {
                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k is null) continue;
                        var display = $"{k.GetValue("DisplayName")} {k.GetValue("Publisher")}";
                        if (!re.IsMatch(display)) continue;
                        var loc = k.GetValue("InstallLocation")?.ToString();
                        if (!string.IsNullOrWhiteSpace(loc) && Directory.Exists(loc))
                            found.Add(Path.GetFullPath(loc));
                    }
                    catch { /* skip broken uninstall entries */ }
                }
            }
        }
        return found;
    }

    private static NativeApplyStep RemoveStartup(string module, List<string> launchers)
    {
        var removed = 0;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is null)
                return new NativeApplyStep { Id = "startup", Status = "ok", Reason = "no Run key" };

            foreach (var name in key.GetValueNames().ToArray())
            {
                var val = key.GetValue(name)?.ToString() ?? "";
                if (name.StartsWith("Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                var hit = module == "riot"
                    ? val.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
                      val.Contains("VALORANT", StringComparison.OrdinalIgnoreCase)
                    : val.Contains("EpicGames", StringComparison.OrdinalIgnoreCase) ||
                      val.Contains("Epic Games", StringComparison.OrdinalIgnoreCase) ||
                      name.Contains("Epic", StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
                // Keep yield companions we own
                if (name.StartsWith($"Exo-", StringComparison.OrdinalIgnoreCase)) continue;
                try { key.DeleteValue(name, false); removed++; } catch { }
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "startup", Status = "partial", Reason = ex.Message };
        }
        return new NativeApplyStep { Id = "startup", Status = "ok", Reason = $"removed={removed}" };
    }

    private static NativeApplyStep QuietShell(string module)
    {
        var ids = module == "riot"
            ? new[]
            {
                "Riot Client", "RiotClient", "VALORANT", "League of Legends", "riotgameclient",
                "Riot Games", "RiotClientUx", "riotclientservices.exe"
            }
            : new[]
            {
                "EpicGamesLauncher", "com.epicgames.launcher", "Epic Games Launcher", "EpicGames",
                "EpicGamesLauncher.exe"
            };

        var n = 0;
        foreach (var id in ids)
        {
            var path = $@"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings\{id}";
            if (NativeReg.TrySetDword("HKCU", path, "Enabled", 0)) n++;
            NativeReg.TrySetDword("HKCU", path, "ShowInActionCenter", 0);
        }

        // Windows Settings → Startup apps (same stickiness issue as Steam)
        var approvedNames = module == "riot"
            ? new[] { "RiotClient", "Riot Client", "Riot" }
            : new[] { "EpicGamesLauncher", "EpicGames", "Epic Games Launcher" };
        var approved = SteamNativeApply.DisableStartupApproved(approvedNames);

        return new NativeApplyStep
        {
            Id = "shell-quiet",
            Status = n > 0 ? "ok" : "fail",
            Reason = $"toastIds={n}; startupApproved={approved}"
        };
    }

    private static NativeApplyStep ApplyGpuFso(
        List<string> games, List<string> launchers, List<string> elevOps, bool admin, string module)
    {
        var hybrid = IsHybridGraphics();
        var launcherGpu = hybrid ? GpuPowerSave : GpuHighPerf;
        var gpuN = 0;
        var fsoN = 0;
        try
        {
            using var gpu = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences", true);
            using var fso = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", true);
            foreach (var path in games)
            {
                // Force full string — AppStatus=0 alone is NOT high-perf GPU (Rocket League bug).
                try { gpu?.SetValue(path, GpuHighPerf, RegistryValueKind.String); gpuN++; } catch { }
                try { fso?.SetValue(path, FsoDisable, RegistryValueKind.String); fsoN++; } catch { }
            }
            foreach (var path in launchers)
            {
                try { gpu?.SetValue(path, launcherGpu, RegistryValueKind.String); gpuN++; } catch { }
                try { fso?.SetValue(path, FsoDisable, RegistryValueKind.String); fsoN++; } catch { }
            }

            // Verify live: every game path must read back GpuPreference=2;
            var verified = 0;
            foreach (var path in games)
            {
                var v = gpu?.GetValue(path)?.ToString() ?? "";
                if (v.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase)) verified++;
            }
            if (games.Count > 0 && verified < games.Count)
            {
                return new NativeApplyStep
                {
                    Id = "gpu-fso",
                    Status = "fail",
                    Reason = $"gpu verified {verified}/{games.Count} (need GpuPreference=2 on every game)"
                };
            }
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "gpu-fso", Status = "fail", Reason = ex.Message };
        }

        return new NativeApplyStep
        {
            Id = "gpu-fso",
            Status = (games.Count == 0 && launchers.Count == 0) || gpuN > 0 ? "ok" : "fail",
            Reason = $"games={games.Count}; launchers={launchers.Count}; gpu={gpuN}; fso={fsoN}; hybrid={hybrid}"
        };
    }

    private static NativeApplyStep ApplyDscp(List<string> games, string module, bool admin, List<string> elevOps)
    {
        var ok = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in games)
        {
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(leaf) || !seen.Add(leaf)) continue;
            var safe = Regex.Replace(leaf, @"[^\w\.\-]", "_");
            var pol = $"Exo-{char.ToUpper(module[0]) + module[1..]}-DSCP-{safe}";
            if (admin)
            {
                if (SteamNativeApply.TrySetDscpPolicy(pol, leaf)) ok++;
            }
            else
            {
                elevOps.Add($"qos:{pol}|{leaf}");
            }
        }
        return new NativeApplyStep
        {
            Id = "game-dscp",
            Status = games.Count == 0 ? "ok" : (admin ? (ok > 0 ? "ok" : "partial") : "pending-elev"),
            Reason = admin ? $"policies={ok}" : "needs admin"
        };
    }

    private static bool IsHybridGraphics()
    {
        try
        {
            var names = new List<string>();
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return false;
            foreach (var sub in classKey.GetSubKeyNames())
            {
                if (!Regex.IsMatch(sub, @"^\d{4}$")) continue;
                using var adapter = classKey.OpenSubKey(sub);
                var driver = adapter?.GetValue("DriverDesc")?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(driver)) continue;
                if (driver.Contains("Microsoft Basic", StringComparison.OrdinalIgnoreCase)) continue;
                names.Add(driver);
            }
            var dgpu = names.Any(n => Regex.IsMatch(n, @"(?i)NVIDIA|GeForce|RTX|GTX|Radeon\s+RX|Intel.*Arc"));
            var igpu = names.Any(n => Regex.IsMatch(n, @"(?i)Intel.*(?:UHD|Iris|HD Graphics)|AMD Radeon\(TM\) Graphics|Radeon Vega"));
            return names.Count >= 2 && dgpu && igpu;
        }
        catch { return false; }
    }

    /// <summary>
    /// Product policy: no always-on background yield processes.
    /// Removes Run keys + helper scripts. One-shot Apply (GPU/FSO/startup) stays.
    /// </summary>
    private static NativeApplyStep PurgeYieldCompanion(string module)
    {
        var removed = 0;
        try
        {
            var mod = char.ToUpper(module[0]) + module[1..];
            var yieldName = $"Exo-{mod}-Yield";
            var helperPath = Path.Combine(PathHelper.AppDataDir, $"{module}-yield-guard.ps1");

            try
            {
                using var run = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (run is not null)
                {
                    foreach (var name in run.GetValueNames().ToArray())
                    {
                        var val = run.GetValue(name)?.ToString() ?? "";
                        if (name.Equals(yieldName, StringComparison.OrdinalIgnoreCase) ||
                            val.Contains("yield-guard", StringComparison.OrdinalIgnoreCase))
                        {
                            try { run.DeleteValue(name, false); removed++; } catch { }
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (File.Exists(helperPath))
                {
                    File.Delete(helperPath);
                    removed++;
                }
            }
            catch { }

            // Best-effort: stop any leftover pwsh running our yield script
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName("pwsh")
                             .Concat(System.Diagnostics.Process.GetProcessesByName("powershell")))
                {
                    try
                    {
                        // Can't always read CommandLine without WMI; skip kill-all
                        p.Dispose();
                    }
                    catch { }
                }
            }
            catch { }

            return new NativeApplyStep
            {
                Id = "yield",
                Status = "ok",
                Reason = $"no background companion (purged={removed})"
            };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep
            {
                Id = "yield",
                Status = "ok",
                Reason = "no background companion (" + ex.Message + ")"
            };
        }
    }

    /// <summary>
    /// Real host only — never Microsoft\WindowsApps\pwsh.exe (execution alias / stub that breaks WSH & Run).
    /// </summary>
    private static string? FindYieldHost()
    {
        foreach (var c in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7-preview", "pwsh.exe"),
                 })
        {
            if (File.Exists(c)) return c;
        }

        try
        {
            var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            if (Directory.Exists(apps))
            {
                foreach (var dir in Directory.EnumerateDirectories(apps, "Microsoft.PowerShell_*")
                             .OrderByDescending(d => d))
                {
                    var p = Path.Combine(dir, "pwsh.exe");
                    if (File.Exists(p)) return p;
                }
            }
        }
        catch { }

        var ps51 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(ps51) ? ps51 : null;
    }

    private static NativeApplyStep ClearLauncherCaches(string module)
    {
        var n = 0;
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            IEnumerable<string> roots = module == "riot"
                ? new[]
                {
                    Path.Combine(local, "Riot Games"),
                    Path.Combine(roaming, "Riot Games")
                }
                : new[]
                {
                    Path.Combine(local, "EpicGamesLauncher"),
                    Path.Combine(local, "Epic Games"),
                    Path.Combine(roaming, "Epic")
                };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var name in new[] { "logs", "Logs", "webcache", "GPUCache", "Code Cache", "Crashpad", "Crashes" })
                {
                    foreach (var hit in Directory.EnumerateDirectories(root, name, SearchOption.AllDirectories).Take(20))
                    {
                        try { Directory.Delete(hit, true); n++; } catch { }
                    }
                }
            }
        }
        catch { }
        return new NativeApplyStep { Id = "cache", Status = "ok", Reason = $"cleared={n}" };
    }

    private static NativeApplyStep InstallQuietStartMenu(string module, List<string> launchers)
    {
        try
        {
            var mod = char.ToUpper(module[0]) + module[1..];
            var launchersDir = Path.Combine(PathHelper.AppDataDir, "launchers");
            Directory.CreateDirectory(launchersDir);
            var cmdPath = Path.Combine(launchersDir, $"{mod}-Exo.cmd");
            var target = launchers.FirstOrDefault(File.Exists);
            if (target is null)
            {
                // Still write marker path for detect startMenuQuiet
                File.WriteAllText(cmdPath, "@echo off\r\nrem Exo placeholder — launcher path not found yet\r\n", new System.Text.UTF8Encoding(false));
                return new NativeApplyStep { Id = "start-menu", Status = "ok", Reason = "placeholder cmd" };
            }

            var workDir = Path.GetDirectoryName(target) ?? "";
            var body = $"@echo off\r\nrem Exo {mod} quiet high-priority launch\r\nstart \"\" /HIGH /D \"{workDir}\" \"{target}\" %*\r\n";
            File.WriteAllText(cmdPath, body, new System.Text.UTF8Encoding(false));

            // Best-effort Start Menu retarget
            try
            {
                var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                var candidates = Directory.Exists(programs)
                    ? Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories)
                        .Where(f =>
                        {
                            var n = Path.GetFileNameWithoutExtension(f);
                            return module == "riot"
                                ? n.Contains("Riot", StringComparison.OrdinalIgnoreCase) ||
                                  n.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) ||
                                  n.Contains("League", StringComparison.OrdinalIgnoreCase)
                                : n.Contains("Epic", StringComparison.OrdinalIgnoreCase);
                        })
                        .Take(6)
                    : Array.Empty<string>();

                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t is not null)
                {
                    dynamic shell = Activator.CreateInstance(t)!;
                    foreach (var lnk in candidates)
                    {
                        try
                        {
                            var sc = shell.CreateShortcut(lnk);
                            sc.TargetPath = cmdPath;
                            sc.WorkingDirectory = launchersDir;
                            sc.Save();
                        }
                        catch { }
                    }
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
            }
            catch { }

            return new NativeApplyStep { Id = "start-menu", Status = "ok", Reason = cmdPath };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "start-menu", Status = "partial", Reason = ex.Message };
        }
    }

    private static NativeApplyStep WriteSnapshot(string module, List<string> games, List<string> launchers)
    {
        try
        {
            var snapPath = Path.Combine(PathHelper.AppDataDir, $"{module}-snapshot.json");
            if (File.Exists(snapPath))
                return new NativeApplyStep { Id = "snapshot", Status = "ok", Reason = "kept original" };

            var snap = new Dictionary<string, object?>
            {
                ["capturedUtc"] = DateTime.UtcNow.ToString("o"),
                ["module"] = module,
                ["path"] = "native-csharp",
                ["games"] = games,
                ["launchers"] = launchers,
                ["note"] = "Pre-Exo snapshot placeholder for Repair scope"
            };
            File.WriteAllText(snapPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions { WriteIndented = true }));
            return new NativeApplyStep { Id = "snapshot", Status = "ok" };
        }
        catch (Exception ex)
        {
            return new NativeApplyStep { Id = "snapshot", Status = "fail", Reason = ex.Message };
        }
    }

    private static void SaveState(string module, bool ok, bool experimental,
        List<string> games, List<string> launchers, List<NativeApplyStep> steps, List<string> elevOps)
    {
        try
        {
            var path = Path.Combine(PathHelper.AppDataDir, $"{module}-optimizer.json");
            Directory.CreateDirectory(PathHelper.AppDataDir);
            var state = new Dictionary<string, object?>
            {
                ["version"] = "native-3.13.0",
                ["applyStatus"] = ok ? "applied" : "incomplete",
                ["applied"] = ok,
                ["appliedUtc"] = DateTime.UtcNow.ToString("o"),
                ["experimental"] = experimental,
                ["path"] = "native-csharp",
                ["gameCount"] = games.Count,
                ["launcherCount"] = launchers.Count,
                // Detect: shellQuiet = state.shellQuiet AND startupQuiet
                ["shellQuiet"] = true,
                ["startupQuiet"] = steps.Any(s => s.Id == "startup" && s.Status == "ok"),
                ["launcherCacheCleaned"] = steps.Any(s => s.Id == "cache" && s.Status == "ok"),
                ["startMenuQuiet"] = steps.Any(s => s.Id == "start-menu" && s.Status is "ok" or "partial"),
                ["yieldInstalled"] = steps.Any(s => s.Id == "yield" && s.Status == "ok"),
                ["lastError"] = null,
                ["pendingElevOps"] = elevOps,
                ["applyReport"] = steps.Select(s => s.ToReportLine()).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
