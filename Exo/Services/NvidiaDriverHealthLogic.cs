using System.Text.RegularExpressions;

namespace Exo.Services;

/// <summary>
/// The parsing and the verdict, with no machine attached.
///
/// Split out for the same reason NvidiaDriverInstaller.Plan is pure: it can then be tested
/// against real pnputil output and made-up machines instead of against whatever the build
/// agent happens to have installed. The first version of the 7-Zip test did the latter and
/// ended up making a real network request from a unit test.
/// </summary>
internal static class NvidiaDriverHealthLogic
{
    /// <summary>One driver package as the driver store knows it.</summary>
    internal sealed record StorePackage(string OemInf, string OriginalName, string Provider, string Version, DateTime? Date)
    {
        public bool IsNvidia => Provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed record Finding(string Id, string Title, string Detail, bool NeedsSweep);

    internal sealed record Report(
        IReadOnlyList<StorePackage> NvidiaPackages,
        IReadOnlyList<Finding> Findings)
    {
        /// <summary>True when something here is worth a clean sweep rather than a reinstall.</summary>
        public bool NeedsSweep => Findings.Any(f => f.NeedsSweep);

        public string Headline =>
            Findings.Count == 0
                ? "NVIDIA's driver install looks clean."
                : NeedsSweep
                    ? $"{Findings.Count(f => f.NeedsSweep)} thing(s) here would survive a normal reinstall."
                    : "Nothing here needs a sweep.";
    }

    /// <summary>
    /// Parses <c>pnputil /enum-drivers</c>. Pure, so it can be tested against real output
    /// instead of against whatever this machine happens to have installed — the mistake that
    /// made the first 7-Zip test hit the network.
    ///
    /// The format is blocks of "Key: value" separated by blank lines. Localised Windows
    /// translates the key names, so matching on the English words alone would silently return
    /// nothing on a German or Japanese install; the oem##.inf line anchors each block instead,
    /// and the rest is read positionally within it.
    /// </summary>
    public static IReadOnlyList<StorePackage> ParseEnumDrivers(string output)
    {
        var packages = new List<StorePackage>();
        if (string.IsNullOrWhiteSpace(output)) return packages;

        // Split into blocks on blank lines; a block without an oem##.inf is not a package.
        foreach (var block in Regex.Split(output, @"(?:\r?\n){2,}"))
        {
            var oem = Regex.Match(block, @"\b(oem\d+\.inf)\b", RegexOptions.IgnoreCase);
            if (!oem.Success) continue;

            string Field(int index)
            {
                // Values are everything after the first colon on the line. Provider names
                // contain no colon; paths and versions can, so split once only.
                var lines = block.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Contains(':'))
                    .ToList();
                return index < lines.Count ? lines[index].Split(':', 2)[1].Trim() : "";
            }

            // Order within a pnputil block: Published Name, Original Name, Provider Name,
            // Class Name, [Class GUID,] Driver Version, Signer Name. Version and date share
            // one field as "MM/DD/YYYY x.y.z.w". The version line is found by shape rather
            // than index: newer pnputil builds insert the Class GUID line before it, so a
            // fixed Field(4) read the GUID — every Version became a GUID string and every
            // Date came back null. No other field carries a four-part dotted number.
            var original = Field(1);
            var provider = Field(2);
            var versionField = Enumerable.Range(0, 8)
                .Select(Field)
                .FirstOrDefault(v => Regex.IsMatch(v, @"\d+\.\d+\.\d+\.\d+")) ?? "";

            var version = Regex.Match(versionField, @"(\d+\.\d+\.\d+\.\d+)");
            var dateMatch = Regex.Match(versionField, @"(\d{1,2}/\d{1,2}/\d{4})");
            DateTime? date = DateTime.TryParse(dateMatch.Value, out var d) ? d : null;

            packages.Add(new StorePackage(
                oem.Groups[1].Value.ToLowerInvariant(),
                original,
                provider,
                version.Success ? version.Groups[1].Value : versionField,
                date));
        }
        return packages;
    }

    /// <summary>
    /// Turns raw state into findings. Pure — every input is passed in, so the whole decision
    /// can be exercised against made-up machines.
    /// </summary>
    /// <param name="packages">Everything the driver store holds.</param>
    /// <param name="liveDriverVersion">The driver actually bound to the GPU, from the class key.</param>
    /// <param name="orphanServices">nv* services present with no matching device.</param>
    /// <param name="deviceProblemCode">CM_PROB_* on the display device, 0 when healthy.</param>
    public static Report Evaluate(
        IReadOnlyList<StorePackage> packages,
        string? liveDriverVersion,
        IReadOnlyList<string> orphanServices,
        int deviceProblemCode)
    {
        var nvidia = packages.Where(p => p.IsNvidia).ToList();
        var findings = new List<Finding>();

        // Stale packages are the reason DDU exists. A normal reinstall adds a package; it does
        // not remove the ones before it, so they accumulate and an upgrade can bind the wrong
        // one. One package is healthy, two is normal mid-upgrade, more than that is residue.
        if (nvidia.Count > 2)
        {
            findings.Add(new Finding(
                "stale-packages",
                $"{nvidia.Count} NVIDIA driver packages in the store",
                "Windows keeps every driver you have ever installed. A reinstall adds to this list, "
                + "it does not clean it, and an upgrade can bind an old one. "
                + string.Join(", ", nvidia.OrderBy(p => p.Version).Select(p => $"{p.OemInf} {p.Version}")),
                NeedsSweep: true));
        }

        // A service with no device behind it is left over from a driver that is gone. It costs
        // a start attempt every boot and, for NvContainer, a running process.
        if (orphanServices.Count > 0)
        {
            findings.Add(new Finding(
                "orphan-services",
                $"{orphanServices.Count} NVIDIA service(s) with nothing behind them",
                "Left by a driver that is no longer installed: " + string.Join(", ", orphanServices),
                NeedsSweep: true));
        }

        // 43 is CM_PROB_FAILED_POST_START — the classic "driver installed, device won't start"
        // that a reinstall will not fix because the store entry itself is bad.
        if (deviceProblemCode != 0)
        {
            findings.Add(new Finding(
                "device-problem",
                $"The display device reports problem code {deviceProblemCode}",
                deviceProblemCode == 43
                    ? "Code 43 means the driver loaded and the device refused to start. Reinstalling over the top usually will not fix it."
                    : "The device is not running cleanly on its current driver.",
                NeedsSweep: true));
        }

        // Worth saying, never worth a sweep on its own: the bound driver is not the newest in
        // the store. Usually harmless, occasionally the reason a new driver "did not apply".
        if (liveDriverVersion is not null && nvidia.Count > 0)
        {
            // The live version arrives in NVIDIA numbering ("591.86") while the store speaks
            // Windows four-part ("32.0.15.9186") — a substring test between the two formats
            // can never match, so this finding used to fire on essentially every machine.
            // Convert the store versions and compare numerically.
            var converted = nvidia
                .Select(p => NvidiaDriverLookup.ConvertWindowsVersion(p.Version))
                .OfType<string>()
                .ToList();
            if (converted.Count > 0)
            {
                var newest = converted.Aggregate((a, b) =>
                    NvidiaDriverLookup.CompareVersions(a, b) >= 0 ? a : b);
                if (NvidiaDriverLookup.CompareVersions(newest, liveDriverVersion) > 0)
                {
                    findings.Add(new Finding(
                        "not-newest",
                        "The running driver is not the newest one in the store",
                        $"Bound: {liveDriverVersion}. Newest present: {newest}. Usually harmless.",
                        NeedsSweep: false));
                }
            }
        }

        return new Report(nvidia, findings);
    }
}
