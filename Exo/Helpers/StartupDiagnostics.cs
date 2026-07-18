using System.Runtime.InteropServices;
using System.Text;

namespace Exo.Helpers;

/// <summary>Privacy-redacted fatal diagnostics for failures before WinUI can report them.</summary>
public static class StartupDiagnostics
{
    private static string _phase = "process-entry";

    public static void EnterPhase(string phase)
    {
        _phase = string.IsNullOrWhiteSpace(phase) ? "unknown" : phase;
        StartupLog.Mark(_phase);
    }

    public static void WriteFatal(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(PathHelper.LogsDir);
            var assembly = typeof(StartupDiagnostics).Assembly.GetName();
            var text = new StringBuilder()
                .AppendLine($"[{DateTime.UtcNow:O}] fatal-startup")
                .AppendLine($"phase={Sanitize(_phase)}")
                .AppendLine($"app={Sanitize(assembly.Version?.ToString() ?? "unknown")}")
                .AppendLine($"os={Sanitize(RuntimeInformation.OSDescription)}")
                .AppendLine($"processArchitecture={RuntimeInformation.ProcessArchitecture}")
                .AppendLine($"osArchitecture={RuntimeInformation.OSArchitecture}")
                .AppendLine($"framework={Sanitize(RuntimeInformation.FrameworkDescription)}")
                .AppendLine(Sanitize(exception.ToString()))
                .ToString();
            File.WriteAllText(Path.Combine(PathHelper.LogsDir, "startup-fatal.log"), text);
        }
        catch
        {
            // Diagnostics are never allowed to hide the original fatal exception.
        }
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        var result = value;
        var replacements = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.UserName
        };

        foreach (var sensitive in replacements
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(x => x.Length))
        {
            result = result.Replace(sensitive, sensitive.Contains(Path.DirectorySeparatorChar)
                ? "<user-path>"
                : "<user>", StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }
}
