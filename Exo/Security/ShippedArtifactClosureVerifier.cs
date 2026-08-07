using System.Security.Cryptography;
using System.Text;

namespace Exo.Security;

internal readonly record struct ShippedArtifactManifestEntry(long Length, string Sha256);

internal sealed record VerifiedShippedArtifact(
    string FullPath,
    string RelativePath,
    long RawLength,
    string RawSha256);

internal sealed record VerifiedShippedArtifactClosure(
    string ScriptsRoot,
    string EntrypointRelativePath,
    IReadOnlyList<VerifiedShippedArtifact> Artifacts,
    string Digest);

internal readonly record struct ShippedArtifactClosureVerification(
    bool Ok,
    string Message,
    VerifiedShippedArtifactClosure? Closure = null);

internal interface IShippedArtifactFileSystem
{
    string GetFullPath(string path);
    bool FileExists(string path);
    bool DirectoryExists(string path);
    FileAttributes GetAttributes(string path);
    IEnumerable<string> EnumerateFiles(string path);
    byte[] ReadAllBytes(string path);
    string ReadAllText(string path);
}

internal sealed class PhysicalShippedArtifactFileSystem : IShippedArtifactFileSystem
{
    public static PhysicalShippedArtifactFileSystem Instance { get; } = new();

    private PhysicalShippedArtifactFileSystem()
    {
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);
    public IEnumerable<string> EnumerateFiles(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
}

internal static class ShippedArtifactClosureVerifier
{
    /// <summary>
    /// Verify an entrypoint from either the immutable shipped tree or Exo's managed
    /// working-copy tree. Containment alone never grants trust: the selected root is
    /// still checked against the complete compiled manifest by <see cref="Verify"/>.
    /// </summary>
    public static ShippedArtifactClosureVerification VerifyManagedRoots(
        string shippedScriptsRoot,
        string workingScriptsRoot,
        string entrypoint,
        IReadOnlyDictionary<string, ShippedArtifactManifestEntry> manifest,
        IShippedArtifactFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? PhysicalShippedArtifactFileSystem.Instance;
        try
        {
            var entry = fs.GetFullPath(entrypoint);
            foreach (var candidateRoot in new[] { shippedScriptsRoot, workingScriptsRoot })
            {
                var root = fs.GetFullPath(candidateRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (IsContained(entry, root))
                    return Verify(root, entry, manifest, fs);
            }

            return Refuse("The optimizer entrypoint must be strictly contained under an Exo-managed scripts root.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Refuse("Shipped artifact closure could not be verified: " + ex.Message);
        }
    }

    public static ShippedArtifactClosureVerification Verify(
        string scriptsRoot,
        string entrypoint,
        IReadOnlyDictionary<string, ShippedArtifactManifestEntry> manifest,
        IShippedArtifactFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? PhysicalShippedArtifactFileSystem.Instance;
        try
        {
            var root = fs.GetFullPath(scriptsRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            var entry = fs.GetFullPath(entrypoint);
            if (!fs.DirectoryExists(root) || !IsContained(entry, root))
                return Refuse("The optimizer entrypoint must be strictly contained under ScriptsRoot.");
            if (ContainsReparsePoint(entry, root, fs))
                return Refuse("The optimizer entrypoint path contains a reparse point.");

            var entryRelative = ToManifestPath(Path.GetRelativePath(root, entry));
            if (!IsSafeRelativePath(entryRelative) || !manifest.ContainsKey(entryRelative))
                return Refuse($"Artifact {entryRelative} is not present in this Exo build's shipped manifest.");

            var slash = entryRelative.IndexOf('/');
            if (slash <= 0)
                return Refuse("The optimizer entrypoint does not identify a shipped module.");
            var module = entryRelative[..slash];
            if (module.Equals("lib", StringComparison.OrdinalIgnoreCase))
                return Refuse("A shared library cannot be used as an optimizer entrypoint.");

            var closureEntries = manifest
                .Where(pair => pair.Key.StartsWith(module + "/", StringComparison.OrdinalIgnoreCase) ||
                               pair.Key.StartsWith("lib/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (closureEntries.Length == 0)
                return Refuse($"The shipped artifact closure for {module} is empty.");

            var verified = new List<VerifiedShippedArtifact>(closureEntries.Length);
            foreach (var (relative, expected) in closureEntries)
            {
                if (!IsSafeRelativePath(relative))
                    return Refuse($"Manifest artifact path is unsafe: {relative}");

                var full = fs.GetFullPath(Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsContained(full, root))
                    return Refuse($"Manifest artifact escaped ScriptsRoot: {relative}");
                if (!fs.FileExists(full))
                    return Refuse($"Shipped artifact integrity failed for {relative} (file missing).");
                if (ContainsReparsePoint(full, root, fs))
                    return Refuse($"Shipped artifact path contains a reparse point: {relative}");

                var manifestBytes = ReadManifestBytes(full, fs);
                if (manifestBytes.LongLength != expected.Length)
                    return Refuse($"Shipped artifact integrity failed for {relative} (length mismatch).");
                var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
                if (!manifestHash.Equals(expected.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Refuse($"Shipped artifact integrity failed for {relative} (SHA-256 mismatch). Reinstall Exo before applying.");

                var rawBytes = fs.ReadAllBytes(full);
                verified.Add(new VerifiedShippedArtifact(
                    full,
                    relative,
                    rawBytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(rawBytes))));
            }

            foreach (var scanRoot in new[] { Path.Combine(root, module), Path.Combine(root, "lib") })
            {
                if (!fs.DirectoryExists(scanRoot))
                    continue;
                foreach (var actual in fs.EnumerateFiles(scanRoot))
                {
                    var full = fs.GetFullPath(actual);
                    if (!IsContained(full, root) || ContainsReparsePoint(full, root, fs))
                        return Refuse("The shipped artifact closure contains a reparse point or escaped ScriptsRoot.");
                    var relative = ToManifestPath(Path.GetRelativePath(root, full));
                    if (!manifest.ContainsKey(relative) && !IsRuntimeMaterial(relative))
                        return Refuse($"Artifact {relative} is not present in this Exo build's shipped manifest.");
                }
            }

            var digestInput = string.Join("\n", verified.Select(x =>
                $"{x.RelativePath}\0{x.RawLength}\0{x.RawSha256}"));
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digestInput)));
            return new ShippedArtifactClosureVerification(
                true,
                $"Verified {verified.Count} shipped artifacts for {module}.",
                new VerifiedShippedArtifactClosure(root, entryRelative, verified, digest));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Refuse("Shipped artifact closure could not be verified: " + ex.Message);
        }
    }

    internal static byte[] ReadManifestBytes(
        string path,
        IShippedArtifactFileSystem? fileSystem = null)
    {
        var fs = fileSystem ?? PhysicalShippedArtifactFileSystem.Instance;
        var extension = Path.GetExtension(path);
        var fileName = Path.GetFileName(path);
        var isText = extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".ini", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".c", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".def", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".txt", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".vbs", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("VERSION", StringComparison.OrdinalIgnoreCase) ||
                     fileName.Equals("PROFILE_VERSION", StringComparison.OrdinalIgnoreCase);
        if (!isText)
            return fs.ReadAllBytes(path);

        var canonical = fs.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(canonical);
    }

    private static bool ContainsReparsePoint(
        string path,
        string root,
        IShippedArtifactFileSystem fileSystem)
    {
        var current = path;
        while (true)
        {
            if ((fileSystem.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (PathEquals(current, root))
                return false;

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || !IsContained(parent, root))
                return true;
            current = parent;
        }
    }

    private static bool IsRuntimeMaterial(string relative) =>
        relative.StartsWith("Discord/kit/downloads/", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("Discord/kit/logs/", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("Discord/kit/tools/pwsh/", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("Discord/kit/tools/discord-modules/", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("Discord/kit/tools/desktop.asar", StringComparison.OrdinalIgnoreCase) ||
        relative.Equals("Discord/kit/tools/equicord.asar", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("Discord/kit/tools/DiscordSetup", StringComparison.OrdinalIgnoreCase) ||
        relative.StartsWith("Discord/kit/tools/Equilotl", StringComparison.OrdinalIgnoreCase) ||
        // Publish-Exo rebuilds Exo.NvDisplay FDD into Nvidia/tools with fresh hashes at
        // release time; the generator deliberately excludes this folder from the pinned
        // manifest (Generate-ScriptManifest.ps1). They still must sit next to the optimizer
        // for apply — treat as allowed runtime material, not a closure refusal.
        relative.StartsWith("Nvidia/tools/", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeRelativePath(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return false;
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 && segments.All(x => x is not "." and not "..");
    }

    private static string ToManifestPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsContained(string path, string root)
    {
        if (PathEquals(path, root))
            return true;
        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ShippedArtifactClosureVerification Refuse(string message) => new(false, message);
}
