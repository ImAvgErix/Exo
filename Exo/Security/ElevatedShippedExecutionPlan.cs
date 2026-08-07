using System.Security.Cryptography;

namespace Exo.Security;

internal sealed record ElevatedShippedArtifact(
    string SourcePath,
    string RelativePath,
    long Length,
    string Sha256);

internal readonly record struct ElevatedExecutionPlanVerification(bool Ok, string Message);

/// <summary>
/// Immutable description of the exact shipped payload approved for an elevated run.
/// The plan contains no wildcard or directory-copy operation: every staged file is
/// bound to the raw length and SHA-256 observed during complete closure verification.
/// </summary>
internal sealed class ElevatedShippedExecutionPlan
{
    private ElevatedShippedExecutionPlan(
        string sourceRoot,
        string entrypointRelativePath,
        string workingDirectoryRelativePath,
        IReadOnlyList<ElevatedShippedArtifact> artifacts,
        string digest)
    {
        SourceRoot = sourceRoot;
        EntrypointRelativePath = entrypointRelativePath;
        WorkingDirectoryRelativePath = workingDirectoryRelativePath;
        Artifacts = artifacts;
        Digest = digest;
    }

    public string SourceRoot { get; }
    public string EntrypointRelativePath { get; }
    public string WorkingDirectoryRelativePath { get; }
    public IReadOnlyList<ElevatedShippedArtifact> Artifacts { get; }
    public string Digest { get; }

    public static ElevatedShippedExecutionPlan Create(
        VerifiedShippedArtifactClosure closure,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(closure);
        if (closure.Artifacts.Count == 0)
            throw new InvalidOperationException("The verified shipped payload is empty.");

        var root = Path.GetFullPath(closure.ScriptsRoot).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var work = Path.GetFullPath(workingDirectory);
        if (!IsContained(work, root))
            throw new InvalidOperationException("The elevated working directory escaped the verified scripts root.");

        var workRelative = ToManifestPath(Path.GetRelativePath(root, work));
        if (!IsSafeRelativeDirectory(workRelative))
            throw new InvalidOperationException("The elevated working directory is not a safe relative path.");

        var artifacts = new List<ElevatedShippedArtifact>(closure.Artifacts.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in closure.Artifacts.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var source = Path.GetFullPath(artifact.FullPath);
            var relative = ToManifestPath(artifact.RelativePath);
            if (!IsContained(source, root) || !IsSafeRelativeFile(relative))
                throw new InvalidOperationException($"Verified artifact escaped its source root: {relative}");
            if (!seen.Add(relative))
                throw new InvalidOperationException($"The verified payload contains a duplicate artifact: {relative}");

            var expectedSource = Path.GetFullPath(Path.Combine(
                root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathEquals(source, expectedSource))
                throw new InvalidOperationException($"Verified artifact path does not match its relative identity: {relative}");

            artifacts.Add(new ElevatedShippedArtifact(
                source,
                relative,
                artifact.RawLength,
                NormalizeSha256(artifact.RawSha256, relative)));
        }

        if (!artifacts.Any(x => string.Equals(
                x.RelativePath,
                closure.EntrypointRelativePath,
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The verified payload does not contain its entrypoint.");
        }

        var digest = ComputeDigest(artifacts);
        if (!digest.Equals(closure.Digest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The verified closure digest changed while creating the elevation plan.");

        return new ElevatedShippedExecutionPlan(
            root,
            ToManifestPath(closure.EntrypointRelativePath),
            workRelative,
            artifacts,
            digest);
    }

    /// <summary>
    /// Re-read every source immediately before UAC. The elevated bootstrap performs
    /// the same length/hash check again after copying each file into its protected stage.
    /// </summary>
    public ElevatedExecutionPlanVerification RevalidateSources()
    {
        foreach (var artifact in Artifacts)
        {
            try
            {
                if (!File.Exists(artifact.SourcePath))
                    return Refuse($"Approved artifact disappeared: {artifact.RelativePath}");
                if (ContainsReparsePoint(artifact.SourcePath, SourceRoot))
                    return Refuse($"Approved artifact path contains a reparse point: {artifact.RelativePath}");

                // The verified closure binds raw on-disk bytes; the manifest's
                // newline normalization was already applied during closure
                // verification, so revalidation compares raw bytes again.
                var bytes = File.ReadAllBytes(artifact.SourcePath);
                if (bytes.LongLength != artifact.Length)
                    return Refuse($"Approved artifact changed length: {artifact.RelativePath}");
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Refuse($"Approved artifact changed after verification: {artifact.RelativePath}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return Refuse($"Approved artifact could not be revalidated ({artifact.RelativePath}): {ex.Message}");
            }
        }

        var digest = ComputeDigest(Artifacts);
        return digest.Equals(Digest, StringComparison.OrdinalIgnoreCase)
            ? new ElevatedExecutionPlanVerification(true, $"Revalidated {Artifacts.Count} approved artifacts.")
            : Refuse("The approved payload digest changed after verification.");
    }

    internal static string ComputeDigest(IEnumerable<ElevatedShippedArtifact> artifacts)
    {
        var input = string.Join("\n", artifacts
            .OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.RelativePath}\0{x.Length}\0{x.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input)));
    }

    private static string NormalizeSha256(string value, string relative)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidOperationException($"Verified artifact has an invalid SHA-256: {relative}");
        return normalized;
    }

    private static bool ContainsReparsePoint(string path, string root)
    {
        var current = path;
        while (true)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            if (PathEquals(current, root))
                return false;
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || !IsContained(parent, root))
                return true;
            current = parent;
        }
    }

    private static bool IsSafeRelativeFile(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            return false;
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1 && segments.All(x => x is not "." and not "..");
    }

    private static bool IsSafeRelativeDirectory(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative == "." || Path.IsPathRooted(relative))
            return false;
        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(x => x is not "." and not "..");
    }

    private static bool IsContained(string path, string root) =>
        PathEquals(path, root) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string ToManifestPath(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');

    private static ElevatedExecutionPlanVerification Refuse(string message) => new(false, message);
}
