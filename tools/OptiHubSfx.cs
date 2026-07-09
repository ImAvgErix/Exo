// OptiHub self-extracting installer / launcher.
// Embeds payload.zip (full self-contained app folder), installs to
// %LocalAppData%\OptiHub\app, then starts OptiHub.exe.
//
// Built by Publish-OptiHub.ps1:
//   csc /target:winexe /optimize+ /out:OptiHub.exe /resource:payload.zip,payload.zip OptiHubSfx.cs

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [STAThread]
    private static int Main()
    {
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string root = Path.Combine(local, AppFolderName);
            string installDir = Path.Combine(root, InstallSubdir);
            string targetExe = Path.Combine(installDir, ExeName);

            StopOptiHub();

            string work = Path.Combine(Path.GetTempPath(), "optihub-sfx-" + Guid.NewGuid().ToString("N"));
            string zipPath = Path.Combine(work, "payload.zip");
            string stage = Path.Combine(work, "stage");
            Directory.CreateDirectory(stage);

            try
            {
                ExtractPayloadZip(zipPath);
                ZipFile.ExtractToDirectory(zipPath, stage);

                string payloadDir = FindAppDir(stage);
                if (payloadDir == null)
                    throw new InvalidOperationException("OptiHub.exe missing from package payload.");

                Directory.CreateDirectory(root);
                ReplaceDirectory(payloadDir, installDir);

                if (!File.Exists(targetExe))
                    throw new InvalidOperationException("Install failed: " + targetExe + " not found.");

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });

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
            MessageBoxW(
                IntPtr.Zero,
                "OptiHub install failed:\n\n" + ex.Message +
                "\n\nFallback (PowerShell):\n" +
                "irm \"https://raw.githubusercontent.com/BarcusEric/OptiHub/main/Install-OptiHub.ps1\" | iex",
                "OptiHub",
                0x10 /* MB_ICONERROR */);
            return 1;
        }
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
            foreach (string name in asm.GetManifestResourceNames())
            {
                if (name.EndsWith("payload.zip", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase))
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

        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length < 1000000L)
            throw new InvalidOperationException("Embedded payload looks invalid or empty.");
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
                Directory.Move(dest, backup);
                try { Directory.Delete(backup, true); } catch { }
            }
            catch
            {
                try { Directory.Delete(dest, true); } catch { }
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

    private static void StopOptiHub()
    {
        for (int i = 0; i < 15; i++)
        {
            Process[] procs = Process.GetProcessesByName("OptiHub");
            if (procs.Length == 0) break;
            foreach (Process p in procs)
            {
                try { p.CloseMainWindow(); } catch { }
            }
            Thread.Sleep(300);
            foreach (Process p in Process.GetProcessesByName("OptiHub"))
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            Thread.Sleep(200);
        }
        Thread.Sleep(400);
    }
}
