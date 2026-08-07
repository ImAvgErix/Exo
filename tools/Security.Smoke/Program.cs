using System.Security.Cryptography;
using System.Text;
using Exo.Security;

var failed = 0;
void Expect(string name, bool condition, string detail = "")
{
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
        return;
    }

    failed++;
    Console.WriteLine($"FAIL  {name}" + (detail.Length == 0 ? string.Empty : " :: " + detail));
}

Console.WriteLine("=== Security.Smoke ===");

var temp = Path.Combine(Path.GetTempPath(), "exo-security-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    TestPowerShellHostSelection();
    TestPowerShellHostRevalidation();
    TestShippedArtifactClosure();
    TestElevatedExecutionPlan();
    TestRunnerSourceContracts();
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}

if (failed > 0)
{
    Console.Error.WriteLine($"Security.Smoke failed: {failed} check(s).");
    return 1;
}

Console.WriteLine("Security.Smoke passed.");
return 0;

void TestPowerShellHostSelection()
{
    var programFiles = Path.Combine(temp, "Program Files", "PowerShell");
    var stable = CreateFile(Path.Combine(programFiles, "7", "pwsh.exe"), "stable");
    var preview = CreateFile(Path.Combine(programFiles, "7-preview", "pwsh.exe"), "preview");
    var attacker = CreateFile(Path.Combine(temp, "attacker-path", "pwsh.exe"), "attacker");
    var fileSystem = new HostFileSystem();
    var candidates = new[]
    {
        new PowerShellHostCandidate(attacker, Path.GetDirectoryName(attacker)!, PowerShellHostSource.ArbitraryPath),
        new PowerShellHostCandidate(preview, Path.GetDirectoryName(preview)!, PowerShellHostSource.PreviewProgramFiles),
        new PowerShellHostCandidate(stable, Path.GetDirectoryName(stable)!, PowerShellHostSource.StableProgramFiles),
    };

    var selected = PowerShellHostResolver.Resolve(candidates, fileSystem, requireMicrosoftSignature: true);
    Expect("stable trusted host wins over Preview and arbitrary PATH",
        PathEquals(selected.Path, stable), selected.Path);

    fileSystem.ReparsePaths.Add(fileSystem.GetFullPath(stable));
    selected = PowerShellHostResolver.Resolve(candidates, fileSystem, requireMicrosoftSignature: true);
    Expect("reparse-point host is rejected", PathEquals(selected.Path, preview), selected.Path);

    fileSystem.ReparsePaths.Clear();
    fileSystem.Signatures[fileSystem.GetFullPath(stable)] = MicrosoftSignatureStatus.Untrusted;
    selected = PowerShellHostResolver.Resolve(candidates, fileSystem, requireMicrosoftSignature: true);
    Expect("unsigned stable host is rejected when signature validation is available",
        PathEquals(selected.Path, preview), selected.Path);

    fileSystem.Signatures[fileSystem.GetFullPath(stable)] = MicrosoftSignatureStatus.TrustedMicrosoft;
    var traversal = new PowerShellHostCandidate(
        Path.Combine(programFiles, "7", "..", "..", "..", "attacker-path", "pwsh.exe"),
        Path.Combine(programFiles, "7"),
        PowerShellHostSource.StableProgramFiles);
    var traversalCandidates = new[] { traversal, candidates[2] };
    selected = PowerShellHostResolver.Resolve(traversalCandidates, fileSystem, requireMicrosoftSignature: true);
    Expect("canonical containment blocks traversal outside trusted root",
        PathEquals(selected.Path, stable), selected.Path);
}

void TestPowerShellHostRevalidation()
{
    var root = Path.Combine(temp, "managed-runtime");
    var host = CreateFile(Path.Combine(root, "pwsh.exe"), "signed host");
    var fileSystem = new HostFileSystem();
    var selection = PowerShellHostResolver.Resolve(
        [new PowerShellHostCandidate(host, root, PowerShellHostSource.StableAppManaged)],
        fileSystem,
        requireMicrosoftSignature: true);

    fileSystem.Signatures[fileSystem.GetFullPath(host)] = MicrosoftSignatureStatus.Untrusted;
    var refused = false;
    try
    {
        _ = PowerShellHostResolver.RevalidateForElevation(selection, fileSystem, requireMicrosoftSignature: true);
    }
    catch (InvalidOperationException)
    {
        refused = true;
    }

    Expect("elevation host trust is revalidated after selection", refused);
}

void TestRunnerSourceContracts()
{
    // Probe upward from the build output for the repository root (same pattern as
    // Network.Smoke). The runner is a WinUI service we must not instantiate in a
    // smoke, so the contracts read the production source that CI will compile.
    DirectoryInfo? probe = new(AppContext.BaseDirectory);
    string? repoRoot = null;
    while (probe is not null)
    {
        if (File.Exists(Path.Combine(probe.FullName, "Exo.sln")))
        {
            repoRoot = probe.FullName;
            break;
        }
        probe = probe.Parent;
    }
    Expect("Security.Smoke can locate the repository root", repoRoot is not null && Directory.Exists(repoRoot));

    var runnerPath = Path.Combine(repoRoot!, "Exo", "Services", "PowerShellRunnerService.cs");
    var discordRunnerPath = Path.Combine(repoRoot!, "Exo", "Scripts", "Discord", "Exo-Discord-Run.ps1");
    var ciPath = Path.Combine(repoRoot!, ".github", "workflows", "ci.yml");
    Expect("runner source exists", File.Exists(runnerPath));
    Expect("Discord runner script exists", File.Exists(discordRunnerPath));
    Expect("CI workflow exists", File.Exists(ciPath));

    var runner = File.ReadAllText(runnerPath);
    var discordRunner = File.ReadAllText(discordRunnerPath);
    var ci = File.ReadAllText(ciPath);

    Expect("runner resolves PowerShell through the trusted host resolver",
        runner.Contains("PowerShellHostResolver.Resolve(EnumerateTrustedPowerShellCandidates())", StringComparison.Ordinal),
        "runner must call PowerShellHostResolver.Resolve with trusted candidates only");
    Expect("runner revalidates the trusted host immediately before elevation",
        runner.Contains("PowerShellHostResolver.RevalidateForElevation", StringComparison.Ordinal));
    Expect("runner builds an elevated execution plan from the verified closure",
        runner.Contains("ElevatedShippedExecutionPlan.Create", StringComparison.Ordinal));
    Expect("runner stages and re-verifies the shipped payload inside the elevated bootstrap",
        runner.Contains("plan.artifacts", StringComparison.Ordinal) &&
        runner.Contains("payload", StringComparison.Ordinal));
    Expect("runner no longer disables shipped integrity enforcement",
        !runner.Contains("DISCOPT_SKIP_MANIFEST", StringComparison.Ordinal),
        "PowerShellRunnerService must not set DISCOPT_SKIP_MANIFEST");
    Expect("Discord runner script no longer disables shipped integrity enforcement",
        !discordRunner.Contains("$env:DISCOPT_SKIP_MANIFEST", StringComparison.Ordinal),
        "Exo-Discord-Run.ps1 must not set DISCOPT_SKIP_MANIFEST");
    Expect("CI runs Security.Smoke",
        ci.Contains("'tools/Security.Smoke/Security.Smoke.csproj'", StringComparison.Ordinal),
        "ci.yml smoke list must include Security.Smoke");
}

void TestElevatedExecutionPlan()
{
    var scriptsRoot = Path.Combine(temp, "PlanScripts");
    var entrypoint = CreateFile(Path.Combine(scriptsRoot, "Steam", "Exo-Steam-Run.ps1"), "& './Steam-Optimizer.ps1'\n");
    var optimizer = CreateFile(Path.Combine(scriptsRoot, "Steam", "Steam-Optimizer.ps1"), "'optimizer'\n");
    // CRLF on disk: manifest and plan hashes are defined over newline-normalized
    // text, so revalidation must accept a CRLF working copy.
    var shared = CreateFile(Path.Combine(scriptsRoot, "lib", "Exo.Common.ps1"), "'common'\r\n");
    var manifest = new Dictionary<string, ShippedArtifactManifestEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in new[] { entrypoint, optimizer, shared })
    {
        var relative = Path.GetRelativePath(scriptsRoot, file).Replace(Path.DirectorySeparatorChar, '/');
        manifest[relative] = Manifest(file);
    }

    var verified = ShippedArtifactClosureVerifier.Verify(scriptsRoot, entrypoint, manifest);
    var plan = ElevatedShippedExecutionPlan.Create(
        verified.Closure!,
        Path.GetDirectoryName(entrypoint)!);

    Expect("elevated plan binds every verified artifact",
        plan.Artifacts.Count == 3
        && plan.Artifacts.Any(x => x.RelativePath == "Steam/Exo-Steam-Run.ps1")
        && plan.Artifacts.Any(x => x.RelativePath == "Steam/Steam-Optimizer.ps1")
        && plan.Artifacts.Any(x => x.RelativePath == "lib/Exo.Common.ps1"));
    Expect("elevated plan maps entrypoint and working directory into a fresh stage",
        plan.EntrypointRelativePath == "Steam/Exo-Steam-Run.ps1"
        && plan.WorkingDirectoryRelativePath == "Steam");
    Expect("untampered execution-plan sources revalidate", plan.RevalidateSources().Ok);

    File.AppendAllText(optimizer, "tampered");
    var tampered = plan.RevalidateSources();
    Expect("execution plan refuses helper tampering after approval",
        !tampered.Ok && tampered.Message.Contains("Steam-Optimizer.ps1", StringComparison.OrdinalIgnoreCase),
        tampered.Message);

    var escapedWorkDir = Capture(() => ElevatedShippedExecutionPlan.Create(
        verified.Closure!,
        Path.Combine(temp, "outside-workdir")));
    Expect("execution plan refuses working directory outside verified root",
        escapedWorkDir is InvalidOperationException);
}

void TestShippedArtifactClosure()
{
    var scriptsRoot = Path.Combine(temp, "Scripts");
    var entrypoint = CreateFile(Path.Combine(scriptsRoot, "Steam", "Exo-Steam-Run.ps1"), "& './Steam-Optimizer.ps1'\n");
    var optimizer = CreateFile(Path.Combine(scriptsRoot, "Steam", "Steam-Optimizer.ps1"), "'optimizer'\n");
    var profile = CreateFile(Path.Combine(scriptsRoot, "Steam", "profiles", "competitive.json"), "{}\n");
    var helper = CreateFile(Path.Combine(scriptsRoot, "Steam", "tools", "helper.exe"), "helper-bytes");
    var dependency = CreateFile(Path.Combine(scriptsRoot, "Steam", "tools", "helper.dll"), "dll-bytes");
    var shared = CreateFile(Path.Combine(scriptsRoot, "lib", "Exo.Common.ps1"), "'common'\n");
    var unrelated = CreateFile(Path.Combine(scriptsRoot, "Discord", "Disc-Optimizer.ps1"), "'discord'\n");
    var manifest = new Dictionary<string, ShippedArtifactManifestEntry>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in new[] { entrypoint, optimizer, profile, helper, dependency, shared, unrelated })
        manifest[Relative(file)] = Manifest(file);

    var verified = ShippedArtifactClosureVerifier.Verify(scriptsRoot, entrypoint, manifest);
    Expect("entrypoint verifies its complete module and shared-library closure",
        verified.Ok && verified.Closure is { Artifacts.Count: 6 }, verified.Message);
    Expect("unrelated optimizer module is excluded from selected closure",
        verified.Closure is not null && verified.Closure.Artifacts.All(x =>
            !x.RelativePath.StartsWith("Discord/", StringComparison.OrdinalIgnoreCase)));

    foreach (var downstream in new[] { optimizer, profile, helper, dependency, shared })
    {
        var original = File.ReadAllBytes(downstream);
        File.AppendAllText(downstream, "tampered");
        var tampered = ShippedArtifactClosureVerifier.Verify(scriptsRoot, entrypoint, manifest);
        Expect($"tampered downstream artifact is rejected: {Relative(downstream)}",
            !tampered.Ok && tampered.Message.Contains(Relative(downstream), StringComparison.OrdinalIgnoreCase),
            tampered.Message);
        File.WriteAllBytes(downstream, original);
    }

    var injected = CreateFile(Path.Combine(scriptsRoot, "Steam", "tools", "injected.ps1"), "'not manifested'\n");
    var unexpected = ShippedArtifactClosureVerifier.Verify(scriptsRoot, entrypoint, manifest);
    Expect("unmanifested module artifact is rejected", !unexpected.Ok &&
        unexpected.Message.Contains("not present", StringComparison.OrdinalIgnoreCase), unexpected.Message);
    File.Delete(injected);

    var outside = CreateFile(Path.Combine(temp, "outside", "Exo-Steam-Run.ps1"), "outside");
    var escaped = ShippedArtifactClosureVerifier.Verify(scriptsRoot, outside, manifest);
    Expect("entrypoint outside ScriptsRoot is rejected", !escaped.Ok &&
        escaped.Message.Contains("ScriptsRoot", StringComparison.OrdinalIgnoreCase), escaped.Message);

    var closureFileSystem = new ClosureFileSystem();
    closureFileSystem.ReparsePaths.Add(closureFileSystem.GetFullPath(Path.GetDirectoryName(profile)!));
    var reparsed = ShippedArtifactClosureVerifier.Verify(scriptsRoot, entrypoint, manifest, closureFileSystem);
    Expect("reparse point anywhere in artifact closure is rejected", !reparsed.Ok &&
        reparsed.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase), reparsed.Message);

    var workingRoot = Path.Combine(temp, "WorkingScripts");
    CopyTree(scriptsRoot, workingRoot);
    var workingEntrypoint = Path.Combine(workingRoot, "Steam", "Exo-Steam-Run.ps1");
    var workingVerified = ShippedArtifactClosureVerifier.VerifyManagedRoots(
        scriptsRoot, workingRoot, workingEntrypoint, manifest);
    Expect("managed working-kit closure is accepted only by shipped hashes",
        workingVerified.Ok && workingVerified.Closure is { Artifacts.Count: 6 }, workingVerified.Message);

    File.AppendAllText(Path.Combine(workingRoot, "Steam", "profiles", "competitive.json"), "tampered");
    var workingTampered = ShippedArtifactClosureVerifier.VerifyManagedRoots(
        scriptsRoot, workingRoot, workingEntrypoint, manifest);
    Expect("tampered managed working-kit dependency is rejected",
        !workingTampered.Ok && workingTampered.Message.Contains("competitive.json", StringComparison.OrdinalIgnoreCase),
        workingTampered.Message);
}

string CreateFile(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return path;
}

string Relative(string path) => Path.GetRelativePath(Path.Combine(temp, "Scripts"), path)
    .Replace(Path.DirectorySeparatorChar, '/');

static ShippedArtifactManifestEntry Manifest(string path)
{
    var bytes = ShippedArtifactClosureVerifier.ReadManifestBytes(path);
    return new ShippedArtifactManifestEntry(bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
}

static bool PathEquals(string left, string right) =>
    string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

static Exception? Capture(Action action)
{
    try
    {
        action();
        return null;
    }
    catch (Exception ex)
    {
        return ex;
    }
}

static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

sealed class HostFileSystem : IPowerShellHostFileSystem
{
    public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, MicrosoftSignatureStatus> Signatures { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string GetFullPath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
    public FileAttributes GetAttributes(string path) => ReparsePaths.Contains(GetFullPath(path))
        ? File.GetAttributes(path) | FileAttributes.ReparsePoint
        : File.GetAttributes(path);
    public MicrosoftSignatureStatus GetMicrosoftSignatureStatus(string path) =>
        Signatures.TryGetValue(GetFullPath(path), out var status)
            ? status
            : MicrosoftSignatureStatus.TrustedMicrosoft;
}

sealed class ClosureFileSystem : IShippedArtifactFileSystem
{
    public HashSet<string> ReparsePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string GetFullPath(string path) => Path.GetFullPath(path);
    public bool FileExists(string path) => File.Exists(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public FileAttributes GetAttributes(string path) => ReparsePaths.Contains(GetFullPath(path))
        ? File.GetAttributes(path) | FileAttributes.ReparsePoint
        : File.GetAttributes(path);
    public IEnumerable<string> EnumerateFiles(string path) =>
        Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories);
    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
}
