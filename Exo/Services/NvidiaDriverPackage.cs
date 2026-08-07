using System.Xml.Linq;

namespace Exo.Services;

/// <summary>
/// Edits an extracted NVIDIA driver package's <c>setup.cfg</c> to leave out components a gaming
/// machine does not need — the job NVCleanInstall does, done from a real package manifest
/// rather than a guess at its shape.
///
/// The structure, read off an actual 591.86 <c>setup.cfg</c>:
///
/// <list type="bullet">
/// <item><c>&lt;install&gt;</c> holds one <c>&lt;sub-package name="…"&gt;</c> per component.
/// Removing an element is what leaves that component out.</item>
/// <item><c>disposition="critical"</c> marks a component the installer refuses to run without.
/// <c>Display.Driver</c> carries it, and it is never removable.</item>
/// <item>Sub-packages reference each other by name in
/// <c>&lt;dependencies&gt;&lt;package package="X"/&gt;</c>. Removing a package leaves those
/// references dangling, so they have to be cleaned up in the same pass.</item>
/// <item><c>&lt;options&gt;</c> is the documented command line: <c>clean</c>, <c>passive</c>,
/// <c>noeula</c>, <c>nofinish</c>, <c>noreboot</c> — and <c>enableTelemetry</c>.</item>
/// </list>
///
/// Nothing here modifies a driver binary. Components are omitted from an install, which is the
/// installer's own supported behaviour — the same thing unticking a box in a custom install
/// does. That distinction is why this is acceptable where patching a shipped binary would not
/// be.
/// </summary>
internal static class NvidiaDriverPackage
{
    /// <summary>
    /// Components Exo leaves out by default, and why. Everything absent from this list is kept:
    /// the safe default for a driver install is to change as little as possible.
    /// </summary>
    /// <summary>
    /// Why a given component is worth losing, for the ones where the answer is not obvious.
    /// Not a list of what gets removed — everything unprotected gets removed. This is what the
    /// diff says about it, so "leaving out CUDAToolkit.nvx" comes with the cost attached.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RemovalNotes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NVIDIA.Update"] = "Driver updater — Exo checks for drivers itself.",
            ["Update.Core"] = "Updater support component.",
            ["Display.Update"] = "Background update service.",
            ["Ansel"] = "In-game capture overlay.",
            ["Display.3DVision"] = "Discontinued stereoscopic 3D support.",
            ["Display.NVIRUSB"] = "3D Vision IR emitter driver.",
            ["Display.NView"] = "Legacy multi-monitor desktop manager.",
            ["Display.NVWMI"] = "WMI provider — only some monitoring tools read it.",
            ["CUDAToolkit.nvx"] = "CUDA runtime — games do not use it, but Blender, Resolve and local AI tools do.",
            ["cublas.nvx"] = "CUDA linear algebra — same trade as the CUDA runtime.",
        };

    /// <summary>
    /// Components that are NEVER removed, with the reason. These are the ones where removing
    /// them looks like a saving and costs something real:
    ///
    /// <list type="bullet">
    /// <item><c>HDAudio.Driver</c> — HDMI and DisplayPort audio. Remove it and sound over the
    /// monitor stops.</item>
    /// <item><c>Display.PhysX</c> — still linked by shipped games; missing it can stop them
    /// launching.</item>
    /// <item><c>Display.Optimus</c> — laptop GPU switching. Inert on a desktop but removing it
    /// on a hybrid laptop breaks display routing, and Exo will not make that call blind.</item>
    /// <item><c>MSVCRuntime*</c> — other components link against them.</item>
    /// </list>
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> NeverRemove =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Display.Driver"] = "The driver itself.",
            ["HDAudio.Driver"] = "HDMI/DisplayPort audio would stop working.",
            ["Display.PhysX"] = "Shipped games still link it.",
            ["Display.Optimus"] = "Hybrid-laptop display routing.",
            ["Display.ControlPanel"] = "Needed to change driver settings.",
            ["NVDisplayContainerLS"] = "Hosts the driver's own services.",
            ["MSVCRuntime2017"] = "Other components link against it.",
            ["MSVCRuntime2019"] = "Other components link against it.",
        };

    /// <summary>
    /// Substrings that protect a component whatever it is called. Exact names cannot keep up
    /// with a vendor that renames things between branches, and these are the ones where being
    /// wrong is silent: NGX is the DLSS runtime, so removing it costs frames in every game that
    /// uses DLSS without any error to explain why.
    /// </summary>
    internal static readonly (string Fragment, string Reason)[] NeverRemoveContaining =
    {
        ("NGX", "DLSS runtime — games would lose DLSS with nothing to say why."),
        ("Vulkan", "Vulkan runtime — some games will not launch without it."),
        ("MSVCRuntime", "Other components link against it."),
        ("PhysX", "Shipped games still link it."),
        ("HDAudio", "HDMI/DisplayPort audio would stop working."),
    };

    /// <summary>Protected by exact name or by fragment; returns the reason, or null if not.</summary>
    internal static string? ProtectedReason(string name)
    {
        if (NeverRemove.TryGetValue(name, out var exact)) return exact;
        foreach (var (fragment, reason) in NeverRemoveContaining)
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return reason;
        return null;
    }

    internal sealed record StripResult(
        string Xml,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> Kept,
        IReadOnlyList<string> RefusedToRemove,
        IReadOnlyList<string> DanglingReferencesCleaned);

    /// <summary>
    /// Returns the edited manifest plus an account of exactly what changed. Callers show that
    /// account before anything is installed — a driver install is not a place for a silent diff.
    /// </summary>
    public static StripResult Strip(string setupCfgXml, IEnumerable<string>? removeNames = null)
    {
        // Denylist, not allowlist. This used to be "remove these seven names", which meant every
        // component NVIDIA adds in a future package was kept by default — the strip could only
        // ever remove bloat someone had anticipated by name, and a vendor that adds components
        // every few branches wins that race forever. Now anything not protected goes, so new
        // bloat is excluded by default and the failure mode flips from "silently installed
        // something you did not want" to "shows you a component you might want back".
        var explicitList = removeNames is null
            ? null
            : new HashSet<string>(removeNames, StringComparer.OrdinalIgnoreCase);

        var doc = XDocument.Parse(setupCfgXml, LoadOptions.PreserveWhitespace);
        var install = doc.Root?.Element("install");
        if (install is null)
            throw new InvalidOperationException("setup.cfg has no <install> section — not a driver package manifest.");

        var removed = new List<string>();
        var kept = new List<string>();
        var refused = new List<string>();

        foreach (var sub in install.Elements("sub-package").ToList())
        {
            var name = (string?)sub.Attribute("name") ?? "";
            var disposition = (string?)sub.Attribute("disposition") ?? "";

            // A caller may still name an exact set; the default is everything unprotected.
            if (explicitList is not null && !explicitList.Contains(name)) { kept.Add(name); continue; }

            // Two independent guards. The protected set encodes what Exo knows costs something;
            // the disposition attribute is the installer's own statement that it cannot run
            // without this component. Either one is enough to refuse.
            var reason = ProtectedReason(name);
            if (reason is not null)
            {
                refused.Add($"{name}: {reason}");
                kept.Add(name);
                continue;
            }
            if (string.Equals(disposition, "critical", StringComparison.OrdinalIgnoreCase))
            {
                refused.Add($"{name}: marked critical by the installer.");
                kept.Add(name);
                continue;
            }

            sub.Remove();
            removed.Add(name);
        }

        // Surviving packages still reference the removed ones by name. Left in place the
        // installer resolves a dependency on a package that is no longer in the manifest, which
        // is how a hand-edited setup.cfg fails partway through an install.
        var dangling = new List<string>();
        foreach (var dep in install.Descendants("package").ToList())
        {
            var target = (string?)dep.Attribute("package") ?? "";
            if (!removed.Contains(target, StringComparer.OrdinalIgnoreCase)) continue;
            var owner = (string?)dep.Ancestors("sub-package").FirstOrDefault()?.Attribute("name") ?? "?";
            dep.Remove();
            dangling.Add($"{owner} -> {target}");
        }

        return new StripResult(doc.ToString(SaveOptions.DisableFormatting), removed, kept, refused, dangling);
    }

    /// <summary>The package version, read from the root element rather than a filename.</summary>
    public static string? ReadVersion(string setupCfgXml)
    {
        try { return (string?)XDocument.Parse(setupCfgXml).Root?.Attribute("version"); }
        catch { return null; }
    }

    /// <summary>Every component the package offers, in manifest order.</summary>
    public static IReadOnlyList<string> ListComponents(string setupCfgXml)
    {
        try
        {
            var install = XDocument.Parse(setupCfgXml).Root?.Element("install");
            return install is null
                ? Array.Empty<string>()
                : install.Elements("sub-package")
                    .Select(s => (string?)s.Attribute("name") ?? "")
                    .Where(n => n.Length > 0).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// The silent-install command line, built only from flags the package's own
    /// <c>&lt;options&gt;</c> section declares. Passing a flag this installer does not know is
    /// how a silent install turns into a dialog nobody is there to click.
    ///
    /// <c>-clean</c> is included deliberately: a clean install removes the previous driver's
    /// leftovers, which is the point of doing this at all. It also resets driver settings, so
    /// the NVIDIA module has to re-apply its profile afterwards.
    /// </summary>
    public static string BuildInstallArguments(string setupCfgXml)
    {
        var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var options = XDocument.Parse(setupCfgXml).Root?.Element("options");
            if (options is not null)
                foreach (var o in options.Elements())
                {
                    var n = (string?)o.Attribute("name");
                    if (!string.IsNullOrEmpty(n)) supported.Add(n);
                }
        }
        catch { /* fall through to the minimum below */ }

        var args = new List<string> { "-s" };  // silent; the installer's baseline, not in <options>
        foreach (var flag in new[] { "clean", "noeula", "nofinish", "noreboot", "passive" })
            if (supported.Contains(flag)) args.Add("-" + flag);

        return string.Join(' ', args);
    }
}
