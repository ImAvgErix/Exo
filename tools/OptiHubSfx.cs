// OptiHub self-extracting installer.
// Embeds payload.zip, installs to %LocalAppData%\OptiHub\app, creates shortcuts, launches the app.
//
// IMPORTANT: This binary is also named OptiHub.exe. Never kill our own process
// when stopping older OptiHub instances.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

internal static class Program
{
    private const string AppFolderName = "OptiHub";
    private const string InstallSubdir = "app";
    private const string ExeName = "OptiHub.exe";
    private const string ResourceName = "payload.zip";
    private const string MutexName = "Local\\OptiHubInstallerSingleton";
    private static readonly int SelfPid = Process.GetCurrentProcess().Id;
    private static bool _silent;
    private static string _logPath = "";

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
        if (args != null)
        {
            foreach (string a in args)
            {
                if (string.IsNullOrWhiteSpace(a)) continue;
                string t = a.Trim().ToLowerInvariant();
                if (t == "/silent" || t == "/quiet" || t == "--silent" || t == "-s" || t == "/s")
                    silent = true;
            }
        }
        // Also honor env for in-app updates.
        try
        {
            string envSilent = Environment.GetEnvironmentVariable("OPTIHUB_SILENT_INSTALL");
            if (!string.IsNullOrEmpty(envSilent) &&
                (envSilent == "1" || envSilent.Equals("true", StringComparison.OrdinalIgnoreCase)))
                silent = true;
        }
        catch { }
        _silent = silent;

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
                        "Another OptiHub installer is already running.\n\nWait for it to finish, then open OptiHub from the Start menu.",
                        "OptiHub", 0x40);
                }
                return 2;
            }

            // Never allocate a console for quiet/in-app updates — matches normal app update UX.
            if (!silent)
            {
                consoleOpen = AllocConsole();
                try { Console.Title = "OptiHub Installer"; } catch { }
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

            Log("OptiHub installer starting..." + (silent ? " (quiet)" : ""));
            Log("PID " + SelfPid);

            Log("Closing any running OptiHub app (not this installer)...");
            StopOtherOptiHub();
            // Extra settle time so file locks release.
            Thread.Sleep(600);
            StopOtherOptiHub();

            string work = Path.Combine(Path.GetTempPath(), "optihub-sfx-" + Guid.NewGuid().ToString("N"));
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
                    throw new InvalidOperationException("OptiHub.exe missing from package payload.");

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
                        "Close every OptiHub window (and anything locking the folder), then run this installer again.");
                }

                // Stamp a plain-text version for easy checking.
                try
                {
                    File.WriteAllText(
                        Path.Combine(installDir, "OPTIHUB-INSTALLED-VERSION.txt"),
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
                    // Prefer standalone .ico so Start Menu does not stick to a cached old EXE icon.
                    // Version the filename so Explorer cannot keep showing a stale cached mark.
                    string iconSrc = Path.Combine(installDir, "Assets", "OptiHub.ico");
                    if (!File.Exists(iconSrc))
                        iconSrc = Path.Combine(installDir, "OptiHub.ico");

                    string iconPath = targetExe + ",0";
                    if (File.Exists(iconSrc))
                    {
                        try
                        {
                            string verTag = string.IsNullOrWhiteSpace(installedVersion)
                                ? "app"
                                : NormalizeVersion(installedVersion).Replace(".", "-");
                            string versioned = Path.Combine(installDir, "OptiHub-" + verTag + ".ico");
                            File.Copy(iconSrc, versioned, true);
                            File.Copy(iconSrc, Path.Combine(installDir, "OptiHub.ico"), true);
                            iconPath = versioned;
                            // Drop older versioned icons so we do not pile up files.
                            try
                            {
                                foreach (string oldIco in Directory.GetFiles(installDir, "OptiHub-*.ico"))
                                {
                                    if (!string.Equals(oldIco, versioned, StringComparison.OrdinalIgnoreCase))
                                    {
                                        try { File.Delete(oldIco); } catch { }
                                    }
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
                    NotifyShellShortcutsChanged();
                    Log("Start Menu shortcut updated (icon=" + iconPath + ").");
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

                Log("Launching OptiHub from " + targetExe + " ...");
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
                    "OptiHub install failed:\n\n" + detail +
                    "\n\nLog: %LocalAppData%\\OptiHub\\install.log\n" +
                    "Download again from:\nhttps://github.com/BarcusEric/OptiHub/releases/latest",
                    "OptiHub",
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
            throw new InvalidOperationException("Embedded payload.zip not found inside this OptiHub.exe.");

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
            // Prefer the folder that also has OptiHub.dll (real app, not a nested tool).
            string dir = Path.GetDirectoryName(file);
            if (dir != null && File.Exists(Path.Combine(dir, "OptiHub.dll")))
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
            throw new InvalidOperationException("Could not stage new OptiHub files.");

        string incomingExe = Path.Combine(incoming, ExeName);
        long expectedExeLength = new FileInfo(incomingExe).Length;
        int expectedFileCount = Directory.GetFiles(incoming, "*", SearchOption.AllDirectories).Length;
        if (expectedExeLength < 100000 || expectedFileCount < 20)
            throw new InvalidOperationException(
                "Staged OptiHub payload looks incomplete (" + expectedFileCount + " files)."
            );

        // Move the live app out of the way. Keep the backup until the new
        // payload is verified so a failed promotion cannot leave users with
        // no runnable installation.
        string backup = null;
        bool liveMoved = false;
        if (Directory.Exists(dest))
        {
            backup = dest + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                if (Directory.Exists(backup))
                {
                    Directory.Delete(backup, true);
                }
                Directory.Move(dest, backup);
                liveMoved = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "Could not move the current OptiHub installation out of the way. " +
                    "Close OptiHub and any antivirus scan using the app folder, then retry.",
                    ex);
            }
        }

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
                throw new InvalidOperationException("The promoted app folder is missing OptiHub.exe.");
            int promotedFileCount = Directory.GetFiles(dest, "*", SearchOption.AllDirectories).Length;
            long promotedExeLength = new FileInfo(promotedExe).Length;
            if (promotedFileCount != expectedFileCount || promotedExeLength != expectedExeLength)
                throw new InvalidOperationException(
                    "Promoted payload verification failed (expected " + expectedFileCount +
                    " files, found " + promotedFileCount + ").");
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
                "Could not activate the new OptiHub installation." + rollbackStatus,
                installEx);
        }
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

    private static void CreateStartMenuShortcut(string targetExe, string workingDir, string iconLocation)
    {
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "OptiHub.lnk");
        WriteShortcut(startMenu, targetExe, workingDir, iconLocation);
    }

    private static void RemoveDesktopShortcuts()
    {
        // Never leave OptiHub (or installer leftovers) on the Desktop.
        string[] desks = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        string[] names = new[]
        {
            "OptiHub.lnk",
            "OptiHub (1).lnk",
            "NVIDIA App.lnk",
            "NVIDIA Control Panel.lnk",
            "GeForce Experience.lnk",
            "Discord (OptiHub).lnk",
            "Steam (OptiHub).lnk",
            "Steam (OptiHub Lean).lnk",
            "Steam (OptiHub Aggressive).lnk"
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
            // Also drop any *.lnk whose name starts with OptiHub
            try
            {
                foreach (string path in Directory.GetFiles(desk, "OptiHub*.lnk"))
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
        scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "OptiHub — max performance hub" });
        // Prefer explicit .ico path (not EXE,0) so the new brand mark shows immediately.
        string icon = string.IsNullOrEmpty(iconLocation) ? (targetExe + ",0") : iconLocation;
        if (icon.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) && !icon.Contains(","))
            icon = icon + ",0";
        scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { icon });
        scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private static void NotifyShellShortcutsChanged()
    {
        try
        {
            // SHCNE_ASSOCCHANGED — force Explorer to refresh icons / Start Menu.
            const int SHCNE_ASSOCCHANGED = 0x08000000;
            const uint SHCNF_IDLIST = 0x0000;
            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    private static bool IsWebView2RuntimeInstalled()
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
                    object pv = key.GetValue("pv");
                    if (pv != null && !string.IsNullOrWhiteSpace(pv.ToString()) && pv.ToString() != "0.0.0.0")
                        return true;
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
                foreach (string dir in Directory.GetDirectories(root))
                {
                    if (File.Exists(Path.Combine(dir, "msedgewebview2.exe")))
                        return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static void EnsureWebView2Runtime(string root, string logPath)
    {
        if (IsWebView2RuntimeInstalled())
        {
            Log("WebView2 Runtime already installed.");
            return;
        }

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
                wc.Headers["User-Agent"] = "OptiHub-Installer/1.0";
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

        if (IsWebView2RuntimeInstalled())
            Log("WebView2 Runtime is ready.");
        else
            Log("WebView2 still not detected — OptiHub will use classic UI fallback until Runtime is installed.");
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
        // Leaving OptiHub-update-1.2.x.exe around caused reinstall races that restored old versions.
        string updates = Path.Combine(root, "updates");
        if (Directory.Exists(updates))
        {
            foreach (string file in Directory.GetFiles(updates, "OptiHub*.exe"))
            {
                try { File.Delete(file); }
                catch { }
            }
        }
    }

    /// <summary>
    /// Stop other OptiHub processes only. Never terminate this installer (same process name).
    /// </summary>
    private static void StopOtherOptiHub()
    {
        for (int i = 0; i < 24; i++)
        {
            Process[] procs = Process.GetProcessesByName("OptiHub");
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

            Thread.Sleep(250);

            foreach (Process p in Process.GetProcessesByName("OptiHub"))
            {
                try
                {
                    if (p.Id == SelfPid)
                    {
                        p.Dispose();
                        continue;
                    }
                    if (!p.HasExited)
                        p.Kill();
                }
                catch { }
                try { p.Dispose(); } catch { }
            }

            if (!anyOther) break;
            Thread.Sleep(200);
        }
        Thread.Sleep(500);
    }
}
