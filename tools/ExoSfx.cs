// Exo self-extracting installer.
// Embeds payload.zip, installs to %LocalAppData%\Exo\app, creates shortcuts, launches the app.
//
// IMPORTANT: This binary is also named Exo.exe. Never kill our own process
// when stopping older Exo instances.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private const string AppFolderName = "Exo";
    private const string InstallSubdir = "app";
    private const string ExeName = "Exo.exe";
    private const string ResourceName = "payload.zip";
    private const string MutexName = "Local\\ExoInstallerSingleton";
    private static readonly int SelfPid = Process.GetCurrentProcess().Id;
    private static bool _silent;
    private static string _logPath = "";
    /// <summary>Optional parent app PID — wait until it exits before replacing files (in-app update).</summary>
    private static int _waitParentPid;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = false;
        int waitPid = 0;
        if (args != null)
        {
            foreach (string a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                string t = a.Trim();
                string lower = t.ToLowerInvariant();
                if (lower == "/silent" || lower == "/quiet" || lower == "--silent" || lower == "-s" || lower == "/s")
                    silent = true;
                // /waitpid:1234 or --waitpid=1234 — in-app updater passes the running Exo PID
                if (lower.StartsWith("/waitpid:") || lower.StartsWith("--waitpid=") || lower.StartsWith("/pid:"))
                {
                    int colon = t.IndexOf(':');
                    int eq = t.IndexOf('=');
                    int sep = colon >= 0 ? colon : eq;
                    if (sep > 0 && sep + 1 < t.Length)
                    {
                        int.TryParse(t.Substring(sep + 1).Trim(), out waitPid);
                    }
                }
            }
        }
        // Also honor env for in-app updates.
        try
        {
            string envSilent = Environment.GetEnvironmentVariable("EXO_SILENT_INSTALL");
            if (!string.IsNullOrEmpty(envSilent) &&
                (envSilent == "1" || envSilent.Equals("true", StringComparison.OrdinalIgnoreCase)))
                silent = true;
            string envPid = Environment.GetEnvironmentVariable("EXO_UPDATE_WAIT_PID");
            int envWait = 0;
            if (!string.IsNullOrEmpty(envPid) && int.TryParse(envPid.Trim(), out envWait) && envWait > 0)
                waitPid = envWait;
        }
        catch { }
        _silent = silent;
        _waitParentPid = waitPid > 0 && waitPid != SelfPid ? waitPid : 0;

        bool consoleOpen = false;
        bool mutexOwned = false;
        Mutex mutex = null;
        try
        {
            // Only one installer at a time — concurrent SFXes were racing and
            // leaving older app.old payloads as the live "app" folder.
            mutex = new Mutex(true, MutexName, out mutexOwned);
            if (!mutexOwned)
            {
                try
                {
                    // Wait briefly for an in-flight install to finish.
                    mutexOwned = mutex.WaitOne(TimeSpan.FromSeconds(90));
                }
                catch { mutexOwned = false; }
            }
            if (!mutexOwned)
            {
                // Quiet updates must not pop UI; the running app already shows status.
                if (!silent)
                {
                    MessageBoxW(IntPtr.Zero,
                        "Another Exo installer is already running.\n\nWait for it to finish, then open Exo from the Start menu.",
                        "Exo", 0x40);
                }
                return 2;
            }

            // Never allocate a console for quiet/in-app updates — matches normal app update UX.
            if (!silent)
            {
                consoleOpen = AllocConsole();
                try { Console.Title = "Exo Installer"; } catch { }
            }

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, AppFolderName);
            string installDir = Path.Combine(root, InstallSubdir);
            string targetExe = Path.Combine(installDir, ExeName);
            string logPath = Path.Combine(root, "install.log");
            _logPath = logPath;

            try
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(logPath, DateTime.Now.ToString("o") + " start pid=" + SelfPid +
                    (silent ? " quiet" : "") + Environment.NewLine);
            }
            catch { }

            Log("Exo installer starting..." + (silent ? " (quiet)" : ""));
            Log("PID " + SelfPid);
            if (_waitParentPid > 0)
                Log("Waiting for parent Exo pid=" + _waitParentPid + " to exit…");

            // In-app updates: parent starts us then exits. Wait for that PID first so
            // ReplaceDirectory is not racing a still-locked app folder.
            if (_waitParentPid > 0)
                WaitForProcessExit(_waitParentPid, TimeSpan.FromSeconds(90));

            Log("Closing any running Exo app (not this installer)...");
            StopOtherExo();
            // WebView2 often keeps handles on the app folder after Exo.exe dies.
            StopExoWebViews();
            // Extra settle time so file locks release (WebView2 / AV scanners).
            Thread.Sleep(900);
            StopOtherExo();
            StopExoWebViews();
            Thread.Sleep(500);

            // One-time upgrade path: OptiHub (the app's old name) may still be
            // installed. Close it, carry its settings/state over, remove it.
            StopLegacyApp();
            MigrateLegacyApp(local, root);

            string work = Path.Combine(Path.GetTempPath(), "exo-sfx-" + Guid.NewGuid().ToString("N"));
            string zipPath = Path.Combine(work, "payload.zip");
            string stage = Path.Combine(work, "stage");
            Directory.CreateDirectory(stage);

            try
            {
                Log("Extracting embedded package...");
                ExtractPayloadZip(zipPath);
                Log("Unzipping (" + FormatSize(new FileInfo(zipPath).Length) + ")...");
                if (Directory.Exists(stage))
                {
                    try { Directory.Delete(stage, true); } catch { }
                }
                Directory.CreateDirectory(stage);
                ZipFile.ExtractToDirectory(zipPath, stage);

                string payloadDir = FindAppDir(stage);
                if (payloadDir == null)
                    throw new InvalidOperationException("Exo.exe missing from package payload.");

                string payloadExe = Path.Combine(payloadDir, ExeName);
                string expectedVersion = ReadFileVersion(payloadExe);
                Log("Payload version: " + expectedVersion);

                Log("Installing to: " + installDir);
                Directory.CreateDirectory(root);
                ReplaceDirectory(payloadDir, installDir);

                if (!File.Exists(targetExe))
                    throw new InvalidOperationException("Install failed: " + targetExe + " not found.");

                // Sanity: real app folder has companion DLLs.
                string winUi = Path.Combine(installDir, "Microsoft.ui.xaml.dll");
                string core = Path.Combine(installDir, "coreclr.dll");
                if (!File.Exists(winUi) && !File.Exists(core))
                {
                    int fileCount = Directory.GetFiles(installDir, "*", SearchOption.AllDirectories).Length;
                    if (fileCount < 20)
                        throw new InvalidOperationException(
                            "Install looks incomplete (" + fileCount + " files). Payload may be corrupt.");
                }

                // CRITICAL: verify the live exe is actually the payload we just staged.
                string installedVersion = ReadFileVersion(targetExe);
                Log("Installed version: " + installedVersion);
                if (!string.IsNullOrEmpty(expectedVersion) &&
                    !string.IsNullOrEmpty(installedVersion) &&
                    !VersionsLooselyEqual(expectedVersion, installedVersion))
                {
                    throw new InvalidOperationException(
                        "Install verification failed.\n" +
                        "Expected: " + expectedVersion + "\n" +
                        "Got:      " + installedVersion + "\n\n" +
                        "Close every Exo window (and anything locking the folder), then run this installer again.");
                }

                // Stamp a plain-text version for easy checking.
                try
                {
                    File.WriteAllText(
                        Path.Combine(installDir, "EXO-INSTALLED-VERSION.txt"),
                        installedVersion + Environment.NewLine +
                        "installedUtc=" + DateTime.UtcNow.ToString("o") + Environment.NewLine +
                        "path=" + targetExe + Environment.NewLine);
                }
                catch { }

                // Ensure WebView2 Runtime is present (required for the SPA shell).
                try
                {
                    EnsureWebView2Runtime(root, logPath);
                }
                catch (Exception wvEx)
                {
                    Log("WebView2 prerequisite warning: " + wvEx.Message);
                }

                // Start Menu only — never create Desktop shortcuts (user policy).
                try
                {
                    // Stable icon path next to the EXE. Never use versioned names —
                    // updates/robocopy wipe them and leave Start Menu/taskbar as blank paper.
                    string iconSrc = Path.Combine(installDir, "Assets", "Exo.ico");
                    string iconPath = targetExe + ",0";
                    if (File.Exists(iconSrc))
                    {
                        try
                        {
                            string stable = Path.Combine(installDir, "Exo.ico");
                            File.Copy(iconSrc, stable, true);
                            try { File.SetLastWriteTimeUtc(stable, DateTime.UtcNow); } catch { }
                            iconPath = stable;
                            // Remove legacy versioned icons (Exo-2-0-2-0.ico etc.)
                            try
                            {
                                foreach (string oldIco in Directory.GetFiles(installDir, "Exo-*.ico"))
                                {
                                    try { File.Delete(oldIco); } catch { }
                                }
                            }
                            catch { }
                        }
                        catch
                        {
                            iconPath = iconSrc;
                        }
                    }

                    CreateStartMenuShortcut(targetExe, installDir, iconPath);
                    RemoveDesktopShortcuts();
                    // Always clear icon caches on install/update so Start Menu shows the new brand mark
                    // (friends often keep a stale Exo icon from an older release).
                    ClearWindowsIconCacheAndRefreshShell(iconPath, targetExe);
                    Log("Start Menu shortcut updated + icon cache cleared (icon=" + iconPath + ").");
                }
                catch (Exception scEx)
                {
                    Log("Shortcut warning: " + scEx.Message);
                }

                // Clean stale installer leftovers so an old update EXE cannot re-run later.
                try { CleanupStaleInstallArtifacts(root, keepInstallDir: installDir); }
                catch (Exception cuEx) { Log("Cleanup warning: " + cuEx.Message); }

                // Drop working kits so the new app re-materializes a full matching set
                // from its bundled Scripts/ tree. Prevents old-UI + new-scripts hybrids.
                try
                {
                    string scriptsDir = Path.Combine(root, "scripts");
                    if (Directory.Exists(scriptsDir))
                    {
                        Directory.Delete(scriptsDir, true);
                        Log("Cleared working optimizer kits for clean rebind.");
                    }
                }
                catch (Exception scEx)
                {
                    Log("Script kit cleanup warning: " + scEx.Message);
                    // Stamp force-resync even if delete partially failed.
                    try
                    {
                        string scriptsDir = Path.Combine(root, "scripts");
                        Directory.CreateDirectory(scriptsDir);
                        File.WriteAllText(
                            Path.Combine(scriptsDir, ".app-kit-stamp"),
                            "force-resync" + Environment.NewLine);
                    }
                    catch { }
                }

                Log("Launching Exo from " + targetExe + " ...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

                try
                {
                    File.AppendAllText(logPath,
                        DateTime.Now.ToString("o") + " ok v=" + installedVersion + " -> " + targetExe + Environment.NewLine);
                }
                catch { }

                Log("Done. Installed v" + installedVersion);
                Thread.Sleep(900);
                return 0;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(work))
                        Directory.Delete(work, true);
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            string detail = ex.GetType().Name + ": " + ex.Message;
            if (ex.InnerException != null)
                detail += "\n" + ex.InnerException.Message;

            try
            {
                string logDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppFolderName);
                Directory.CreateDirectory(logDir);
                File.AppendAllText(
                    Path.Combine(logDir, "install.log"),
                    DateTime.Now.ToString("o") + " FAIL " + detail + Environment.NewLine + ex + Environment.NewLine);
            }
            catch { }

            Log("FAILED: " + detail);
            // In-app quiet updates must not flash MessageBox; status lives in install.log.
            if (!_silent)
            {
                MessageBoxW(
                    IntPtr.Zero,
                    "Exo install failed:\n\n" + detail +
                    "\n\nLog: %LocalAppData%\\Exo\\install.log\n" +
                    "Download again from:\nhttps://github.com/ImAvgErix/Exo/releases/latest",
                    "Exo",
                    0x10);
            }
            if (consoleOpen)
                Thread.Sleep(4000);
            return 1;
        }
        finally
        {
            if (mutexOwned && mutex != null)
            {
                try { mutex.ReleaseMutex(); } catch { }
            }
            if (mutex != null)
            {
                try { mutex.Dispose(); } catch { }
            }
            if (consoleOpen)
            {
                try { FreeConsole(); } catch { }
            }
        }
    }

    private static void Log(string msg)
    {
        if (!_silent)
        {
            try { Console.WriteLine("  " + msg); }
            catch { }
        }
        try
        {
            if (!string.IsNullOrEmpty(_logPath))
            {
                File.AppendAllText(_logPath,
                    DateTime.Now.ToString("o") + " " + msg + Environment.NewLine);
            }
        }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes > 1024L * 1024L)
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        return (bytes / 1024.0).ToString("0") + " KB";
    }

    private static string ReadFileVersion(string exePath)
    {
        try
        {
            if (!File.Exists(exePath)) return "";
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(exePath);
            if (!string.IsNullOrWhiteSpace(vi.FileVersion))
                return vi.FileVersion.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductVersion))
                return vi.ProductVersion.Trim();
        }
        catch { }
        return "";
    }

    private static bool VersionsLooselyEqual(string a, string b)
    {
        string na = NormalizeVersion(a);
        string nb = NormalizeVersion(b);
        if (string.Equals(na, nb, StringComparison.OrdinalIgnoreCase))
            return true;
        // 1.3.8 vs 1.3.8.0
        Version va, vb;
        if (Version.TryParse(na, out va) && Version.TryParse(nb, out vb))
            return va.Major == vb.Major && va.Minor == vb.Minor && va.Build == vb.Build;
        return false;
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";
        v = v.Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v.Substring(0, plus);
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase) ||
            v.StartsWith("V", StringComparison.OrdinalIgnoreCase))
            v = v.Substring(1);
        return v.Trim();
    }

    private static void ExtractPayloadZip(string zipPath)
    {
        string dir = Path.GetDirectoryName(zipPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        Assembly asm = Assembly.GetExecutingAssembly();
        Stream stream = asm.GetManifestResourceStream(ResourceName);
        if (stream == null)
        {
            string[] names = asm.GetManifestResourceNames();
            Log("Resources: " + string.Join(", ", names));
            foreach (string name in names)
            {
                if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOf("payload", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stream = asm.GetManifestResourceStream(name);
                    if (stream != null) break;
                }
            }
        }

        if (stream == null)
            throw new InvalidOperationException("Embedded payload.zip not found inside this Exo.exe.");

        using (stream)
        using (FileStream fs = File.Create(zipPath))
            stream.CopyTo(fs);

        long len = new FileInfo(zipPath).Length;
        if (len < 1000000L)
            throw new InvalidOperationException("Embedded payload looks invalid (" + len + " bytes).");
    }

    private static string FindAppDir(string stage)
    {
        if (File.Exists(Path.Combine(stage, ExeName)))
            return stage;

        foreach (string file in Directory.GetFiles(stage, ExeName, SearchOption.AllDirectories))
        {
            // Prefer the folder that also has Exo.dll (real app, not a nested tool).
            string dir = Path.GetDirectoryName(file);
            if (dir != null && File.Exists(Path.Combine(dir, "Exo.dll")))
                return dir;
        }

        foreach (string file in Directory.GetFiles(stage, ExeName, SearchOption.AllDirectories))
            return Path.GetDirectoryName(file);

        return null;
    }

    private static void ReplaceDirectory(string source, string dest)
    {
        // Always install via fresh folder rename so we never half-merge over an old tree.
        string parent = Path.GetDirectoryName(dest);
        if (string.IsNullOrEmpty(parent))
            throw new InvalidOperationException("Invalid install path: " + dest);

        string incoming = dest + ".incoming-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        if (Directory.Exists(incoming))
        {
            try { Directory.Delete(incoming, true); } catch { }
        }

        // Prefer atomic move of staged payload into *.incoming
        bool staged = false;
        try
        {
            Directory.Move(source, incoming);
            staged = true;
        }
        catch
        {
            CopyTree(source, incoming);
            staged = true;
        }

        if (!staged || !Directory.Exists(incoming) || !File.Exists(Path.Combine(incoming, ExeName)))
            throw new InvalidOperationException("Could not stage new Exo files.");

        string incomingExe = Path.Combine(incoming, ExeName);
        long expectedExeLength = new FileInfo(incomingExe).Length;
        int expectedFileCount = Directory.GetFiles(incoming, "*", SearchOption.AllDirectories).Length;
        if (expectedExeLength < 100000 || expectedFileCount < 20)
            throw new InvalidOperationException(
                "Staged Exo payload looks incomplete (" + expectedFileCount + " files)."
            );

        // Preferred path: move live app aside, then promote incoming -> app.
        // Fallback: overwrite in place when the folder is locked (WebView2/AV).
        string backup = null;
        bool liveMoved = false;
        bool inPlace = false;
        if (Directory.Exists(dest))
        {
            backup = dest + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            Exception lastMove = null;
            for (int attempt = 1; attempt <= 16; attempt++)
            {
                try
                {
                    if (Directory.Exists(backup))
                    {
                        try { Directory.Delete(backup, true); } catch { }
                    }
                    if (attempt == 3 || attempt == 7 || attempt == 12)
                    {
                        StopOtherExo();
                        StopExoWebViews();
                    }
                    Directory.Move(dest, backup);
                    liveMoved = true;
                    lastMove = null;
                    break;
                }
                catch (Exception ex)
                {
                    lastMove = ex;
                    Thread.Sleep(300 + attempt * 40);
                }
            }

            if (!liveMoved)
            {
                // Folder locked: copy over live files instead of failing the whole update.
                Log("Directory.Move locked — installing in place over the live app folder.");
                if (lastMove != null)
                    Log("Move error was: " + lastMove.Message);
                StopOtherExo();
                StopExoWebViews();
                Thread.Sleep(600);
                OverwriteTreeAggressive(incoming, dest);
                inPlace = true;
            }
        }

        if (!inPlace)
        {
            // Promote incoming -> app. If both the atomic move and copy fallback
            // fail, restore the verified previous installation.
            try
            {
                try
                {
                    Directory.Move(incoming, dest);
                }
                catch
                {
                    CopyTree(incoming, dest);
                    try { Directory.Delete(incoming, true); } catch { }
                }

                string promotedExe = Path.Combine(dest, ExeName);
                if (!File.Exists(promotedExe))
                    throw new InvalidOperationException("The promoted app folder is missing Exo.exe.");
                int promotedFileCount = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length;
                long promotedExeLength = new FileInfo(promotedExe).Length;
                // In-place path may leave extra old locale folders — only strict-check on rename promote.
                if (promotedFileCount < 20 || promotedExeLength != expectedExeLength)
                    throw new InvalidOperationException(
                        "Promoted payload verification failed (expected exe size " + expectedExeLength +
                        ", found " + promotedExeLength + ", files=" + promotedFileCount + ").");
            }
            catch (Exception installEx)
            {
                string rollbackStatus = "";
                DeleteTreeBestEffort(dest);
                if (liveMoved && !string.IsNullOrEmpty(backup) && Directory.Exists(backup))
                {
                    try
                    {
                        if (Directory.Exists(dest))
                            Directory.Delete(dest, true);
                        Directory.Move(backup, dest);
                        rollbackStatus = " The previous version was restored.";
                    }
                    catch (Exception rollbackEx)
                    {
                        rollbackStatus = " Automatic rollback also failed (" + rollbackEx.Message +
                            "). The previous version remains at: " + backup;
                    }
                }

                throw new InvalidOperationException(
                    "Could not activate the new Exo installation." + rollbackStatus,
                    installEx);
            }
        }
        else
        {
            // Verify in-place result
            string liveExe = Path.Combine(dest, ExeName);
            if (!File.Exists(liveExe))
                throw new InvalidOperationException("In-place install missing Exo.exe.");
            long liveLen = new FileInfo(liveExe).Length;
            if (liveLen != expectedExeLength)
                throw new InvalidOperationException(
                    "In-place install verification failed (exe size " + liveLen +
                    " vs expected " + expectedExeLength + ").");
            try { Directory.Delete(incoming, true); } catch { }
            Log("In-place install verified.");
        }
    }

    /// <summary>
    /// Copy every file from source onto dest, renaming locked targets out of the way.
    /// Used when Directory.Move(dest) fails because something still holds the folder.
    /// </summary>
    private static void OverwriteTreeAggressive(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        int copied = 0;
        int renamed = 0;
        int failed = 0;

        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string rel = dir.Substring(source.Length).TrimStart('\\', '/');
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = file.Substring(source.Length).TrimStart('\\', '/');
            string target = Path.Combine(dest, rel);
            string targetDir = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            bool ok = false;
            for (int attempt = 1; attempt <= 8 && !ok; attempt++)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                        File.Copy(file, target, true);
                    }
                    else
                    {
                        File.Copy(file, target, true);
                    }
                    ok = true;
                    copied++;
                }
                catch
                {
                    // Rename locked file aside, then place the new one.
                    try
                    {
                        if (File.Exists(target))
                        {
                            string trash = target + ".old-" + DateTime.Now.ToString("HHmmssfff") + "-" + attempt;
                            try { File.SetAttributes(target, FileAttributes.Normal); } catch { }
                            File.Move(target, trash);
                            renamed++;
                            // Best-effort delete later
                            try { File.Delete(trash); } catch { }
                        }
                        File.Copy(file, target, true);
                        ok = true;
                        copied++;
                    }
                    catch
                    {
                        Thread.Sleep(120 * attempt);
                    }
                }
            }
            if (!ok)
            {
                failed++;
                Log("Could not replace locked file: " + rel);
            }
        }

        Log("In-place copy: ok=" + copied + " renamed=" + renamed + " failed=" + failed);
        if (failed > 0 && !File.Exists(Path.Combine(dest, ExeName)))
            throw new InvalidOperationException(
                "In-place install could not write Exo.exe (" + failed + " files locked).");
        // Require critical exe replaced
        if (!File.Exists(Path.Combine(dest, ExeName)))
            throw new InvalidOperationException("In-place install missing Exo.exe after copy.");
    }

    private static void DeleteTreeBestEffort(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch { }
        }
        try { Directory.Delete(path, true); } catch { }
    }

    private static void CopyTree(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            string rel = dir.Substring(source.Length).TrimStart('\\', '/');
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string rel = file.Substring(source.Length).TrimStart('\\', '/');
            string target = Path.Combine(dest, rel);
            string parent = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);
            // Retry copy a few times for AV scanners
            Exception last = null;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    File.Copy(file, target, true);
                    last = null;
                    break;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(120 * (i + 1));
                }
            }
            if (last != null) throw last;
        }
    }

    /// <summary>
    /// Stop any running instance of the legacy OptiHub app (pre-rename builds).
    /// </summary>
    private static void StopLegacyApp()
    {
        for (int i = 0; i < 8; i++)
        {
            Process[] procs = Process.GetProcessesByName("OptiHub");
            if (procs.Length == 0) return;
            foreach (Process p in procs)
            {
                try { p.CloseMainWindow(); } catch { }
                try { p.Dispose(); } catch { }
            }

            Thread.Sleep(250);

            foreach (Process p in Process.GetProcessesByName("OptiHub"))
            {
                try
                {
                    if (!p.HasExited)
                        p.Kill();
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
        }
    }

    /// <summary>
    /// Carry settings/optimizer state from a legacy %LocalAppData%\OptiHub install
    /// into the Exo data folder, then remove the old install and its shortcut.
    /// Never overwrites data Exo already has.
    /// </summary>
    private static void MigrateLegacyApp(string local, string root)
    {
        string legacyRoot = Path.Combine(local, "OptiHub");
        try
        {
            if (Directory.Exists(legacyRoot))
            {
                foreach (string src in Directory.GetFiles(legacyRoot, "*.json"))
                {
                    string dest = Path.Combine(root, Path.GetFileName(src));
                    try
                    {
                        if (!File.Exists(dest))
                            File.Copy(src, dest);
                    }
                    catch { }
                }

                try
                {
                    Directory.Delete(legacyRoot, true);
                    Log("Migrated legacy OptiHub settings/state and removed the old install.");
                }
                catch (Exception ex)
                {
                    Log("Legacy OptiHub cleanup warning (files may be locked): " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Legacy OptiHub migration warning: " + ex.Message);
        }

        try
        {
            string oldLnk = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs",
                "OptiHub.lnk");
            if (File.Exists(oldLnk))
            {
                File.Delete(oldLnk);
                Log("Removed legacy OptiHub Start Menu shortcut.");
            }
        }
        catch { }
    }

    private static void CreateStartMenuShortcut(string targetExe, string workingDir, string iconLocation)
    {
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "Exo.lnk");
        WriteShortcut(startMenu, targetExe, workingDir, iconLocation);
    }

    private static void RemoveDesktopShortcuts()
    {
        // Never leave Exo (or installer leftovers) on the Desktop.
        string[] desks = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        string[] names = new[]
        {
            "Exo.lnk",
            "Exo (1).lnk",
            "NVIDIA App.lnk",
            "NVIDIA Control Panel.lnk",
            "GeForce Experience.lnk",
            "Discord (Exo).lnk",
            "Steam (Exo).lnk",
            "Steam (Exo Lean).lnk",
            "Steam (Exo Aggressive).lnk"
        };
        foreach (string desk in desks)
        {
            if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) continue;
            foreach (string name in names)
            {
                try
                {
                    string path = Path.Combine(desk, name);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
            // Also drop any *.lnk whose name starts with Exo
            try
            {
                foreach (string path in Directory.GetFiles(desk, "Exo*.lnk"))
                {
                    try { File.Delete(path); } catch { }
                }
            }
            catch { }
        }
    }

    private static void WriteShortcut(string lnkPath, string targetExe, string workingDir, string iconLocation)
    {
        string parent = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

        // Replace existing .lnk so Explorer does not keep a stale icon cache entry.
        try { if (File.Exists(lnkPath)) File.Delete(lnkPath); } catch { }

        // Late-bound WScript.Shell — no extra assembly refs needed for csc.
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            throw new InvalidOperationException("WScript.Shell not available.");

        object shell = Activator.CreateInstance(shellType);
        object shortcut = shellType.InvokeMember(
            "CreateShortcut",
            BindingFlags.InvokeMethod,
            null,
            shell,
            new object[] { lnkPath });

        Type scType = shortcut.GetType();
        scType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
        scType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
        scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "Exo — max performance hub" });
        // Prefer explicit .ico path (not EXE,0) so the new brand mark shows immediately.
        string icon = string.IsNullOrEmpty(iconLocation) ? (targetExe + ",0") : iconLocation;
        if (icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && !icon.Contains(","))
            icon = icon + ",0";
        scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon });
        scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, string dwItem1, string dwItem2);

    private static void NotifyShellShortcutsChanged()
    {
        try
        {
            // SHCNE_ASSOCCHANGED - force Explorer to refresh icons / Start Menu.
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// Delete Explorer icon/thumbnail cache DBs and ping the shell so Start Menu picks up the new .ico.
    /// Safe best-effort: locked cache files are skipped; SHChangeNotify still runs.
    /// </summary>
    private static void ClearWindowsIconCacheAndRefreshShell(string iconPath, string targetExe)
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            TryDeleteFile(Path.Combine(local, "IconCache.db"));

            string explorerCache = Path.Combine(local, "Microsoft", "Windows", "Explorer");
            if (Directory.Exists(explorerCache))
            {
                try
                {
                    foreach (string f in Directory.GetFiles(explorerCache, "iconcache*.db"))
                        TryDeleteFile(f);
                    foreach (string f in Directory.GetFiles(explorerCache, "IconCache*.db"))
                        TryDeleteFile(f);
                    // thumbcache can also pin old Start Menu tiles
                    foreach (string f in Directory.GetFiles(explorerCache, "thumbcache_*.db"))
                        TryDeleteFile(f);
                }
                catch { }
            }

            // Per-user hidden IconCache folder (Win10/11)
            try
            {
                string hidden = Path.Combine(local, "Microsoft", "Windows", "Explorer");
                // also clear any IconCacheToDelete leftovers
                string toDelete = Path.Combine(local, "Microsoft", "Windows", "Explorer", "IconCacheToDelete");
                if (Directory.Exists(toDelete))
                {
                    try { Directory.Delete(toDelete, true); } catch { }
                }
            }
            catch { }

            Log("Windows icon cache clear attempted.");
        }
        catch (Exception ex)
        {
            Log("Icon cache clear warning: " + ex.Message);
        }

        // Broadcast refresh + notify specific icon / exe paths.
        NotifyShellShortcutsChanged();
        try
        {
            const int SHCNE_UPDATEITEM = 0x00002000;
            const int SHCNE_CREATE = 0x00000002;
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_PATHW = 0x0005;
            const uint SHCNF_FLUSH = 0x1000;
            const uint SHCNF_FLUSHNOWAIT = 0x2000;

            if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath.Split(',')[0]))
            {
                string icoOnly = iconPath.Split(',')[0];
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, icoOnly, null);
            }
            if (!string.IsNullOrEmpty(targetExe) && File.Exists(targetExe))
            {
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, targetExe, null);
            }

            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string lnk = Path.Combine(programs, "Exo.lnk");
            if (File.Exists(lnk))
            {
                SHChangeNotify(SHCNE_CREATE, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, lnk, null);
                SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT, lnk, null);
            }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { /* locked by Explorer - next login will rebuild */ }
    }

    /// <summary>
    /// Healthy = real browser data files, not just registry + msedgewebview2.exe.
    /// Incomplete installs (~15 files, no icudtl.dat) make WebView2 FileNotFound.
    /// </summary>
    private static bool IsWebView2RuntimeHealthy()
    {
        try
        {
            string folder = FindWebView2BrowserFolder();
            if (string.IsNullOrEmpty(folder)) return false;
            if (!File.Exists(Path.Combine(folder, "msedgewebview2.exe"))) return false;
            if (File.Exists(Path.Combine(folder, "icudtl.dat"))) return true;
            if (File.Exists(Path.Combine(folder, "resources.pak"))) return true;
            try
            {
                foreach (string f in Directory.GetFiles(folder, "icudtl.dat", SearchOption.AllDirectories))
                    if (File.Exists(f)) return true;
                foreach (string f in Directory.GetFiles(folder, "resources.pak", SearchOption.AllDirectories))
                    if (File.Exists(f)) return true;
            }
            catch { }
            return false;
        }
        catch { return false; }
    }

    private static string FindWebView2BrowserFolder()
    {
        try
        {
            string[] keys =
            {
                @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
                @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"
            };
            foreach (string k in keys)
            {
                using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(k))
                {
                    if (key == null) continue;
                    object loc = key.GetValue("location");
                    object pv = key.GetValue("pv");
                    if (loc != null && pv != null)
                    {
                        string dir = Path.Combine(loc.ToString(), pv.ToString());
                        if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                            return dir;
                    }
                }
            }
        }
        catch { }

        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "EdgeWebView", "Application");
            if (Directory.Exists(root))
            {
                string[] dirs = Directory.GetDirectories(root);
                Array.Sort(dirs);
                Array.Reverse(dirs);
                foreach (string dir in dirs)
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        return dir;
                }
            }
        }
        catch { }

        return null;
    }

    private static void EnsureWebView2Runtime(string root, string logPath)
    {
        if (IsWebView2RuntimeHealthy())
        {
            Log("WebView2 Runtime healthy: " + FindWebView2BrowserFolder());
            return;
        }

        string existing = FindWebView2BrowserFolder();
        if (!string.IsNullOrEmpty(existing))
            Log("WebView2 Runtime INCOMPLETE at " + existing + " — repairing…");
        else
            Log("WebView2 Runtime missing — downloading Evergreen bootstrapper...");

        string prereqDir = Path.Combine(root, "prereqs");
        Directory.CreateDirectory(prereqDir);
        string setupPath = Path.Combine(prereqDir, "MicrosoftEdgeWebview2Setup.exe");

        // Official Evergreen bootstrapper (small; pulls the full runtime).
        string url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        try
        {
            // .NET Framework WebClient — available to csc /nologo without extra refs.
            using (var wc = new System.Net.WebClient())
            {
                wc.Headers["User-Agent"] = "Exo-Installer/1.0";
                wc.DownloadFile(url, setupPath);
            }
        }
        catch (Exception dlEx)
        {
            Log("WebView2 download failed: " + dlEx.Message);
            Log("Install manually: https://go.microsoft.com/fwlink/p/?LinkId=2124703");
            return;
        }

        if (!File.Exists(setupPath) || new FileInfo(setupPath).Length < 10000)
        {
            Log("WebView2 bootstrapper looks invalid; skipping silent install.");
            return;
        }

        Log("Installing WebView2 Runtime (silent)...");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = setupPath,
                Arguments = "/silent /install",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process p = Process.Start(psi))
            {
                if (p != null)
                {
                    // Cap wait — bootstrapper usually finishes quickly when online.
                    if (!p.WaitForExit(10 * 60 * 1000))
                    {
                        try { p.Kill(); } catch { }
                        Log("WebView2 installer timed out after 10 minutes.");
                    }
                    else
                    {
                        Log("WebView2 installer exit code: " + p.ExitCode);
                    }
                }
            }
        }
        catch (Exception runEx)
        {
            Log("WebView2 install failed: " + runEx.Message);
        }

        // EdgeUpdate may still be unpacking — poll briefly for browser data files.
        for (int i = 0; i < 30 && !IsWebView2RuntimeHealthy(); i++)
            Thread.Sleep(1000);

        if (IsWebView2RuntimeHealthy())
            Log("WebView2 Runtime is healthy: " + FindWebView2BrowserFolder());
        else
            Log("WebView2 still incomplete — Exo will repair on next launch or fall back to classic UI.");
    }

    private static void CleanupStaleInstallArtifacts(string root, string keepInstallDir)
    {
        // Remove old app.old-* / app.incoming-* / app.broken-* folders (keep none — live app is enough).
        foreach (string dir in Directory.GetDirectories(root))
        {
            string name = Path.GetFileName(dir);
            if (name == null) continue;
            if (string.Equals(dir, keepInstallDir, StringComparison.OrdinalIgnoreCase))
                continue;
            if (name.StartsWith("app.old-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("app.incoming-", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("app.broken-", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "app-update", StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(dir, true); }
                catch { }
            }
        }

        // Delete ALL cached update SFXes — next update will re-download latest.
        // Leaving Exo-update-1.2.x.exe around caused reinstall races that restored old versions.
        string updates = Path.Combine(root, "updates");
        if (Directory.Exists(updates))
        {
            foreach (string file in Directory.GetFiles(updates, "Exo*.exe"))
            {
                try { File.Delete(file); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Kill Edge WebView2 leftovers that keep handles on the Exo app folder after Exo.exe exits.
    /// (SFX is csc-built without System.Management — name-based kill only.)
    /// When no other Exo.exe is running, orphan WebView2 processes are safe to stop.
    /// </summary>
    private static void StopExoWebViews()
    {
        try
        {
            bool exoStillRunning = false;
            foreach (Process e in Process.GetProcessesByName("Exo"))
            {
                try
                {
                    if (e.Id != SelfPid && !e.HasExited)
                        exoStillRunning = true;
                }
                catch { }
                try { e.Dispose(); } catch { }
            }

            // Only reap webviews when the app is gone — avoids killing other apps' WebView2.
            if (exoStillRunning)
            {
                Log("Exo still running — skipping WebView2 cleanup this pass.");
                return;
            }

            int killed = 0;
            foreach (Process p in Process.GetProcessesByName("msedgewebview2"))
            {
                try
                {
                    try { p.Kill(); } catch { }
                    try { p.WaitForExit(2500); } catch { }
                    killed++;
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
            if (killed > 0)
                Log("Stopped " + killed + " msedgewebview2 process(es).");
        }
        catch (Exception ex)
        {
            Log("WebView stop warning: " + ex.Message);
        }
    }

    /// <summary>
    /// Stop other Exo processes only. Never terminate this installer (same process name).
    /// </summary>
    private static void StopOtherExo()
    {
        for (int i = 0; i < 32; i++)
        {
            Process[] procs = Process.GetProcessesByName("Exo");
            bool anyOther = false;
            foreach (Process p in procs)
            {
                try
                {
                    if (p.Id == SelfPid)
                    {
                        p.Dispose();
                        continue;
                    }
                    anyOther = true;
                    try { p.CloseMainWindow(); } catch { }
                }
                catch { }
            }

            Thread.Sleep(280);

            foreach (Process p in Process.GetProcessesByName("Exo"))
            {
                try
                {
                    if (p.Id == SelfPid)
                    {
                        p.Dispose();
                        continue;
                    }
                    if (!p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                        try { p.WaitForExit(4000); } catch { }
                    }
                }
                catch { }
                try { p.Dispose(); } catch { }
            }

            if (!anyOther) break;
            Thread.Sleep(250);
        }
        Thread.Sleep(700);
    }

    /// <summary>Block until <paramref name="pid"/> exits (or timeout). Used so in-app updates don't race file locks.</summary>
    private static void WaitForProcessExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0 || pid == SelfPid) return;
        DateTime deadline = DateTime.UtcNow + timeout;
        try
        {
            using (Process p = Process.GetProcessById(pid))
            {
                Log("Parent process found: " + p.ProcessName + " pid=" + pid);
                int waitMs = (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue);
                if (!p.WaitForExit(waitMs))
                    Log("Timed out waiting for parent pid=" + pid + " — continuing with force-close.");
                else
                    Log("Parent pid=" + pid + " exited.");
            }
        }
        catch (ArgumentException)
        {
            // Already gone
            Log("Parent pid=" + pid + " already gone.");
        }
        catch (Exception ex)
        {
            Log("Wait parent warning: " + ex.Message);
        }

        // Spin until the PID truly vanishes (handles brief zombie windows).
        while (DateTime.UtcNow < deadline)
        {
            bool alive = false;
            try
            {
                using (Process p = Process.GetProcessById(pid))
                    alive = !p.HasExited;
            }
            catch { alive = false; }
            if (!alive) break;
            Thread.Sleep(150);
        }
        Thread.Sleep(400);
    }
}
