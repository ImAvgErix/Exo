using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Exo.Security;

internal enum PowerShellHostSource
{
    StableProgramFiles,
    StableAppx,
    StableAppManaged,
    PreviewProgramFiles,
    PreviewAppx,
    PreviewAppManaged,
    ArbitraryPath
}

internal enum MicrosoftSignatureStatus
{
    TrustedMicrosoft,
    Untrusted,
    Unavailable
}

internal readonly record struct PowerShellHostCandidate(
    string Path,
    string TrustedRoot,
    PowerShellHostSource Source);

internal readonly record struct PowerShellHostSelection(
    string Path,
    string TrustedRoot,
    PowerShellHostSource Source);

internal interface IPowerShellHostFileSystem
{
    string GetFullPath(string path);
    bool FileExists(string path);
    FileAttributes GetAttributes(string path);
    MicrosoftSignatureStatus GetMicrosoftSignatureStatus(string path);
}

internal sealed class PhysicalPowerShellHostFileSystem : IPowerShellHostFileSystem
{
    public static PhysicalPowerShellHostFileSystem Instance { get; } = new();

    private PhysicalPowerShellHostFileSystem()
    {
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool FileExists(string path) => File.Exists(path);

    public FileAttributes GetAttributes(string path) => File.GetAttributes(path);

    public MicrosoftSignatureStatus GetMicrosoftSignatureStatus(string path)
    {
        if (!OperatingSystem.IsWindows())
            return MicrosoftSignatureStatus.Unavailable;

        try
        {
            // Authenticode signer extraction has no X509CertificateLoader equivalent;
            // the loader API only reads raw certificate files, not signed PE images.
#pragma warning disable SYSLIB0057
            var signature = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = new X509Certificate2(signature);
            using var chain = new X509Chain
            {
                ChainPolicy =
                {
                    RevocationMode = X509RevocationMode.Offline,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                    UrlRetrievalTimeout = TimeSpan.FromSeconds(2)
                }
            };

            if (!chain.Build(certificate))
                return MicrosoftSignatureStatus.Untrusted;

            var publisher = $"{certificate.Subject} {certificate.Issuer}";
            return publisher.Contains("Microsoft Corporation", StringComparison.OrdinalIgnoreCase)
                ? MicrosoftSignatureStatus.TrustedMicrosoft
                : MicrosoftSignatureStatus.Untrusted;
        }
        catch (CryptographicException)
        {
            return MicrosoftSignatureStatus.Untrusted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return MicrosoftSignatureStatus.Unavailable;
        }
    }
}

internal static class PowerShellHostResolver
{
    public static PowerShellHostSelection Resolve(
        IEnumerable<PowerShellHostCandidate> candidates,
        IPowerShellHostFileSystem? fileSystem = null,
        bool? requireMicrosoftSignature = null)
    {
        var fs = fileSystem ?? PhysicalPowerShellHostFileSystem.Instance;
        var requireSignature = requireMicrosoftSignature ?? OperatingSystem.IsWindows();
        foreach (var candidate in candidates.OrderBy(x => Rank(x.Source)))
        {
            if (TryValidate(candidate, fs, requireSignature, out var selection, out _))
                return selection;
        }

        throw new InvalidOperationException(
            "A trusted PowerShell 7 host was not found in Exo-managed, AppX, or Program Files locations.");
    }

    public static string RevalidateForElevation(
        PowerShellHostSelection selection,
        IPowerShellHostFileSystem? fileSystem = null,
        bool? requireMicrosoftSignature = null)
    {
        var fs = fileSystem ?? PhysicalPowerShellHostFileSystem.Instance;
        var requireSignature = requireMicrosoftSignature ?? OperatingSystem.IsWindows();
        var candidate = new PowerShellHostCandidate(selection.Path, selection.TrustedRoot, selection.Source);
        if (!TryValidate(candidate, fs, requireSignature, out var revalidated, out var reason))
            throw new InvalidOperationException("PowerShell elevation host is no longer trusted: " + reason);
        if (!PathEquals(revalidated.Path, selection.Path))
            throw new InvalidOperationException("PowerShell elevation host changed after selection.");
        return revalidated.Path;
    }

    private static bool TryValidate(
        PowerShellHostCandidate candidate,
        IPowerShellHostFileSystem fileSystem,
        bool requireMicrosoftSignature,
        out PowerShellHostSelection selection,
        out string reason)
    {
        selection = default;
        reason = "candidate was invalid";
        if (candidate.Source == PowerShellHostSource.ArbitraryPath)
        {
            reason = "arbitrary PATH candidates are not trusted";
            return false;
        }

        try
        {
            if (string.IsNullOrWhiteSpace(candidate.Path) || string.IsNullOrWhiteSpace(candidate.TrustedRoot))
                return false;

            var path = fileSystem.GetFullPath(candidate.Path);
            var root = fileSystem.GetFullPath(candidate.TrustedRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (!IsContained(path, root) || !fileSystem.FileExists(path))
            {
                reason = "candidate was outside its trusted root or missing";
                return false;
            }

            var name = Path.GetFileName(path);
            if (!name.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("pwsh-preview.exe", StringComparison.OrdinalIgnoreCase))
            {
                reason = "candidate was not a PowerShell 7 executable";
                return false;
            }

            if (ContainsReparsePoint(path, root, fileSystem))
            {
                reason = "candidate path contains a reparse point";
                return false;
            }

            var signature = fileSystem.GetMicrosoftSignatureStatus(path);
            if (signature == MicrosoftSignatureStatus.Untrusted ||
                (requireMicrosoftSignature && signature != MicrosoftSignatureStatus.TrustedMicrosoft))
            {
                reason = "candidate is not a trusted Microsoft-signed executable";
                return false;
            }

            selection = new PowerShellHostSelection(path, root, candidate.Source);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool ContainsReparsePoint(
        string path,
        string root,
        IPowerShellHostFileSystem fileSystem)
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

    private static bool IsContained(string path, string root)
    {
        if (PathEquals(path, root))
            return true;
        var rootPrefix = root + Path.DirectorySeparatorChar;
        return path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static int Rank(PowerShellHostSource source) => source switch
    {
        PowerShellHostSource.StableProgramFiles => 0,
        PowerShellHostSource.StableAppx => 1,
        PowerShellHostSource.StableAppManaged => 2,
        PowerShellHostSource.PreviewProgramFiles => 3,
        PowerShellHostSource.PreviewAppx => 4,
        PowerShellHostSource.PreviewAppManaged => 5,
        _ => int.MaxValue
    };
}
