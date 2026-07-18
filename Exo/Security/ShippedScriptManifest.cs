using System.Security.Cryptography;
using Exo.Helpers;

namespace Exo.Security;

internal readonly record struct ManifestEntry(long Length, string Sha256);

public enum ScriptTrustPolicy
{
    ShippedManifest,
    AppGeneratedNetwork
}

internal static partial class ShippedScriptManifest
{
    public static (bool Ok, string Message) Verify(string path, ScriptTrustPolicy policy)
    {
        if (policy == ScriptTrustPolicy.AppGeneratedNetwork)
            return VerifyGeneratedNetworkScript(path);

        if (!TryGetRelativePath(path, out var relative) || !Entries.TryGetValue(relative, out var entry))
            return (false, "The optimizer is not present in this Exo build's signed script manifest.");

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != entry.Length)
                return (false, $"Optimizer integrity failed for {relative} (length mismatch).");

            using var stream = info.OpenRead();
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            return hash.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)
                ? (true, relative)
                : (false, $"Optimizer integrity failed for {relative} (SHA-256 mismatch). Reinstall Exo before applying.");
        }
        catch (Exception ex)
        {
            return (false, $"Optimizer integrity could not be verified: {ex.Message}");
        }
    }

    private static bool TryGetRelativePath(string path, out string relative)
    {
        var full = Path.GetFullPath(path);
        foreach (var root in new[] { PathHelper.ScriptsRoot, PathHelper.WorkingScriptsDir })
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                continue;
            relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            return !relative.StartsWith("../", StringComparison.Ordinal) && relative != "..";
        }

        relative = string.Empty;
        return false;
    }

    private static (bool Ok, string Message) VerifyGeneratedNetworkScript(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var name = Path.GetFileName(full);
            var valid = full.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                        name.StartsWith("exo-net-", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
            return valid
                ? (true, name)
                : (false, "Generated elevated scripts are restricted to Exo's network transaction path.");
        }
        catch (Exception ex)
        {
            return (false, $"Generated script path could not be validated: {ex.Message}");
        }
    }
}
