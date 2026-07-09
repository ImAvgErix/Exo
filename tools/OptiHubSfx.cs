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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();

    [STAThread]
    private static int Main()
    {
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
                MessageBoxW(IntPtr.Zero,
                    "Another OptiHub installer is already running.\n\nWait for it to finish, then open OptiHub from the Start menu.",
                    "OptiHub", 0x40);
                return 2;
            }

            consoleOpen = AllocConsole();
            Console.Title = "OptiHub Installer";
            Log("OptiHub installer starting...");
            Log("PID " + SelfPid);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, AppFolderName);
            string installDir = Path.Combine(root, InstallSubdir);
            string targetExe = Path.Combine(installDir, ExeName);
            string logPath = Path.Combine(root, "install.log");

            try
            {
                Directory.CreateDirectory(root);
                File.AppendAllText(logPath, DateTime.Now.ToString("o") + " start pid=" + SelfPid + Environment.NewLine);
            }
            catch { }

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

                // Shortcuts always point at the live install, not this SFX / not repo publish folders.
                try
                {
                    CreateShortcuts(targetExe, installDir);
                    Log("Shortcuts updated (Start Menu + Desktop).");
                }
                catch (Exception scEx)
                {
                    Log("Shortcut warning: " + scEx.Message);
                }

                // Clean stale installer leftovers so an old update EXE cannot re-run later.
                try { CleanupStaleInstallArtifacts(root, keepInstallDir: installDir); }
                catch (Exception cuEx) { Log("Cleanup warning: " + cuEx.Message); }

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
            MessageBoxW(
                IntPtr.Zero,
                "OptiHub install failed:\n\n" + detail +
                "\n\nLog: %LocalAppData%\\OptiHub\\install.log\n" +
                "Download again from:\nhttps://github.com/BarcusEric/OptiHub/releases/latest",
                "OptiHub",
                0x10);
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
        try { Console.WriteLine("  " + msg); }
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

        // Move live app out of the way
        if (Directory.Exists(dest))
        {
            string backup = dest + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                if (Directory.Exists(backup))
                {
                    try { Directory.Delete(backup, true); } catch { }
                }
                Directory.Move(dest, backup);
            }
            catch
            {
                // Fallback: wipe dest in place
                DeleteTreeBestEffort(dest);
                try
                {
                    if (Directory.Exists(dest))
                        Directory.Delete(dest, true);
                }
                catch { }
            }
        }

        // Promote incoming -> app
        try
        {
            Directory.Move(incoming, dest);
        }
        catch
        {
            // Last resort copy
            CopyTree(incoming, dest);
            try { Directory.Delete(incoming, true); } catch { }
        }

        if (!File.Exists(Path.Combine(dest, ExeName)))
            throw new InvalidOperationException("ReplaceDirectory finished but OptiHub.exe is missing.");
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

    private static void CreateShortcuts(string targetExe, string workingDir)
    {
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs",
            "OptiHub.lnk");
        string desktop = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "OptiHub.lnk");

        WriteShortcut(startMenu, targetExe, workingDir);
        WriteShortcut(desktop, targetExe, workingDir);
    }

    private static void WriteShortcut(string lnkPath, string targetExe, string workingDir)
    {
        string parent = Path.GetDirectoryName(lnkPath);
        if (!string.IsNullOrEmpty(parent))
            Directory.CreateDirectory(parent);

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
        scType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "OptiHub" });
        scType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe + ",0" });
        scType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
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
