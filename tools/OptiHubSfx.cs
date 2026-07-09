// OptiHub self-extracting installer.
// Embeds payload.zip, installs to %LocalAppData%\OptiHub\app, launches the app.
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
        try
        {
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

                Log("Installing to: " + installDir);
                Directory.CreateDirectory(root);
                ReplaceDirectory(payloadDir, installDir);

                if (!File.Exists(targetExe))
                    throw new InvalidOperationException("Install failed: " + targetExe + " not found.");

                // Sanity: real app folder has companion DLLs; bare 200KB exe alone is broken.
                string winUi = Path.Combine(installDir, "Microsoft.ui.xaml.dll");
                string core = Path.Combine(installDir, "coreclr.dll");
                if (!File.Exists(winUi) && !File.Exists(core))
                {
                    int fileCount = Directory.GetFiles(installDir, "*", SearchOption.AllDirectories).Length;
                    if (fileCount < 20)
                        throw new InvalidOperationException(
                            "Install looks incomplete (" + fileCount + " files). Payload may be corrupt.");
                }

                Log("Launching OptiHub...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

                try
                {
                    File.AppendAllText(logPath, DateTime.Now.ToString("o") + " ok -> " + targetExe + Environment.NewLine);
                }
                catch { }

                Log("Done.");
                Thread.Sleep(800);
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
            if (consoleOpen)
            {
                try { FreeConsole(); } catch { }
            }
        }
    }

    private static void Log(string msg)
    {
        try
        {
            Console.WriteLine("  " + msg);
        }
        catch { }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes > 1024L * 1024L)
            return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        return (bytes / 1024.0).ToString("0") + " KB";
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
            return Path.GetDirectoryName(file);

        return null;
    }

    private static void ReplaceDirectory(string source, string dest)
    {
        if (Directory.Exists(dest))
        {
            string backup = dest + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                // Prefer rename so locked files don't block a full delete mid-install.
                if (Directory.Exists(backup))
                {
                    try { Directory.Delete(backup, true); } catch { }
                }
                Directory.Move(dest, backup);
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Thread.Sleep(2000);
                        if (Directory.Exists(backup))
                            Directory.Delete(backup, true);
                    }
                    catch { }
                });
            }
            catch
            {
                // Best-effort wipe then copy over.
                try { DeleteTreeBestEffort(dest); } catch { }
            }
        }

        try
        {
            Directory.Move(source, dest);
            return;
        }
        catch
        {
            CopyTree(source, dest);
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
            File.Copy(file, target, true);
        }
    }

    /// <summary>
    /// Stop other OptiHub processes only. Never terminate this installer (same process name).
    /// </summary>
    private static void StopOtherOptiHub()
    {
        for (int i = 0; i < 20; i++)
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
        Thread.Sleep(400);
    }
}
