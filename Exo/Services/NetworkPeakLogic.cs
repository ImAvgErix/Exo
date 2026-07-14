using System.Text.RegularExpressions;
using Exo.Models;

namespace Exo.Services;

/// <summary>
/// Pure peak-path decisions for Internet optimizer (no elevation, no I/O).
/// Used by detect, apply-script generation, and smoke tests — single source of truth.
/// </summary>
public static class NetworkPeakLogic
{
    public sealed record PresetKnobs(
        string AutotuneNetsh,
        string AutotunePs,
        string Rsc,
        string Lso,           // "0" | "1"
        string InterruptMod,  // "0" | "1"
        string FlowControl,   // "0" | "3"
        string IdleRestrict,  // "1" = prevent NIC idle (latency)
        bool NagleOff);

    public sealed record PathDecision(
        string PolicyLine,
        string PreferredBandTarget,
        bool DisableWifiWhenPreferEth,
        bool KeepWifiBecauseEthNoIp);

    /// <summary>Documented tradeoffs only — latency vs highest download.</summary>
    public static PresetKnobs KnobsFor(NetworkPreset preset)
    {
        var latency = preset == NetworkPreset.LowestLatency;
        // Balanced uses latency-leaning host knobs but without aggressive nagle (treat as latency stack lite)
        var bulk = preset == NetworkPreset.HighestThroughput;
        return new PresetKnobs(
            AutotuneNetsh: bulk ? "experimental" : "normal",
            AutotunePs: bulk ? "Experimental" : "Normal",
            Rsc: bulk ? "enabled" : "disabled",
            Lso: bulk ? "1" : "0",
            InterruptMod: bulk ? "1" : "0",
            FlowControl: bulk ? "3" : "0",
            IdleRestrict: bulk ? "0" : "1",
            NagleOff: latency);
    }

    /// <summary>Raw NIC advanced-property facts (null = not exposed by driver).</summary>
    public sealed record NicPeakFacts(
        bool? FlowControlOn,
        bool? InterruptModerationOn,
        bool? IdleRestrictOn,
        bool? SelectiveSuspendOn);

    /// <summary>
    /// Preset-aware NIC peak: latency wants flow/IM off + idle-restrict on;
    /// highest download wants flow/IM on + idle-restrict off. Missing props do not fail.
    /// </summary>
    public static (bool Ok, string Hints) EvaluateNicPeak(NetworkPreset preset, NicPeakFacts f)
    {
        var bulk = preset == NetworkPreset.HighestThroughput;
        var bits = new List<string>();
        var ok = true;

        if (f.FlowControlOn is bool fcOn)
        {
            // bulk: on is intentional; latency: on is a gap
            if (bulk)
            {
                bits.Add(fcOn ? "FlowCtrl on" : "FlowCtrl off");
                if (!fcOn) ok = false;
            }
            else
            {
                bits.Add(fcOn ? "FlowCtrl on" : "FlowCtrl off");
                if (fcOn) ok = false;
            }
        }

        if (f.InterruptModerationOn is bool imOn)
        {
            if (bulk)
            {
                bits.Add(imOn ? "IM on" : "IM off");
                if (!imOn) ok = false;
            }
            else
            {
                bits.Add(imOn ? "IM on" : "IM off");
                if (imOn) ok = false;
            }
        }

        if (f.IdleRestrictOn is bool idleOn)
        {
            // IdleRestriction Enabled = prevent low-power idle (latency good)
            if (bulk)
            {
                bits.Add(idleOn ? "IdleRestrict on" : "IdleRestrict off");
                if (idleOn) ok = false;
            }
            else
            {
                bits.Add(idleOn ? "IdleRestrict on" : "IdleRestrict off");
                if (!idleOn) ok = false;
            }
        }

        // Selective suspend: always prefer off when exposed (both presets)
        if (f.SelectiveSuspendOn is bool ssOn)
        {
            bits.Add(ssOn ? "SelSuspend on" : "SelSuspend off");
            if (ssOn) ok = false;
        }

        var hints = bits.Count > 0 ? string.Join(", ", bits) : "—";
        return (ok, hints);
    }

    /// <summary>
    /// Autotune level must match preset knobs (normal vs experimental).
    /// Unknown / unread ("—", empty) skips — same as null LSO/RSC — so a probe gap
    /// never marks the row "not checked" after a successful apply.
    /// </summary>
    public static bool AutotuneMatches(NetworkPreset preset, string? autoTuning)
    {
        if (string.IsNullOrWhiteSpace(autoTuning) || autoTuning is "—") return true;
        var want = KnobsFor(preset).AutotuneNetsh;
        var got = autoTuning.Trim();
        // netsh may report "normal", "Normal", or rare multi-token; compare first token
        var token = got.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        return token.Equals(want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>RSC enabled state matches preset (null unknown = skip).</summary>
    public static bool RscMatches(NetworkPreset preset, bool? rscEnabled)
    {
        if (rscEnabled is null) return true;
        var wantOn = KnobsFor(preset).Rsc.Equals("enabled", StringComparison.OrdinalIgnoreCase);
        return rscEnabled == wantOn;
    }

    /// <summary>LSO enabled state matches preset (null unknown = skip).</summary>
    public static bool LsoMatches(NetworkPreset preset, bool? lsoEnabled)
    {
        if (lsoEnabled is null) return true;
        var wantOn = KnobsFor(preset).Lso == "1";
        return lsoEnabled == wantOn;
    }

    /// <summary>
    /// Score a single Preferred Band display value. Higher wins.
    /// Prefer-* beats Only-*; 2.4 never chosen when higher exists.
    /// </summary>
    public static int ScoreBandDisplayValue(string? value, bool want6)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        var v = value.Trim();
        var isOnly = Regex.IsMatch(v, @"(?i)\bonly\b|\bexclusive\b");
        var isPref = Regex.IsMatch(v, @"(?i)prefer|preferred|preferable|priority|favou?r");

        if (Regex.IsMatch(v, @"(?i)2\.4|2,4|2400|2GHz|2\s*GHz"))
            return isOnly ? -200 : isPref ? -100 : -50;

        if (Regex.IsMatch(v, @"(?i)no\s*pref|no\s*preference|auto|default|disabled|not\s*set|any\s*band|best\s*performance|\b802\.11\s*auto\b"))
            return 1;

        if (Regex.IsMatch(v, @"(?i)6\s*GHz|6GHz|6,?0\s*GHz|Wi-?Fi\s*6E|802\.11be.*6|band\s*6"))
        {
            if (want6) return isOnly ? 45 : isPref ? 100 : 90;
            return isOnly ? 5 : 25;
        }

        if (Regex.IsMatch(v, @"(?i)5\s*GHz|5GHz|5\.2|5,0|5\.0|5800|band\s*5|802\.11a(?!x)|802\.11ac|802\.11n.*5"))
            return isOnly ? 35 : isPref ? 80 : 70;

        return 0;
    }

    /// <summary>Pick best Prefer band display value from vendor ValidDisplayValues.</summary>
    public static string? SelectBandDisplayValue(IEnumerable<string?> values, bool want6)
    {
        var list = values
            .Select(v => v?.Trim())
            .Where(v => !string.IsNullOrEmpty(v))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) return null;

        string? best = null;
        var bestScore = int.MinValue;
        foreach (var v in list)
        {
            var s = ScoreBandDisplayValue(v, want6);
            if (s > bestScore || (s == bestScore && best is not null && string.CompareOrdinal(v, best) < 0))
            {
                bestScore = s;
                best = v;
            }
        }

        if (best is not null && bestScore > 1)
            return best;

        // Last resort: any 6/5 string that is not only/2.4
        if (want6)
        {
            var fb6 = list.FirstOrDefault(x =>
                Regex.IsMatch(x, @"(?i)6") && !Regex.IsMatch(x, @"(?i)2\.4|only"));
            if (fb6 is not null) return fb6;
        }

        return list.FirstOrDefault(x =>
            Regex.IsMatch(x, @"(?i)5") && !Regex.IsMatch(x, @"(?i)2\.4|only|6"));
    }

    /// <summary>True when adapter facts indicate Wi‑Fi (not Ethernet 802.3).</summary>
    public static bool IsWifiAdapter(
        string? physicalMediaType,
        string? mediaType,
        string? interfaceDescription,
        string? name)
    {
        var pm = physicalMediaType ?? "";
        var m = mediaType ?? "";
        var d = interfaceDescription ?? "";
        var n = name ?? "";

        if (Regex.IsMatch(pm, @"(?i)Native 802\.11|802\.11|Wireless")) return true;
        if (Regex.IsMatch(pm, @"(?i)^802\.3$")) return false;
        if (Regex.IsMatch(m, @"(?i)Native 802|802\.11|Wireless|Wi-?Fi")) return true;
        // USB/PCIe Wi‑Fi vendors + common names (not Bluetooth PAN as primary signal)
        if (Regex.IsMatch(d, @"(?i)Wi-?Fi|Wireless|802\.11|WLAN|MediaTek.*Wi|Intel.*Wi-?Fi|Realtek.*802\.11|Killer.*Wireless|Qualcomm.*Wi|Broadcom.*802|AX\d{3,4}|BE\d{3,4}|Wi-Fi\s*\d"))
            return true;
        if (Regex.IsMatch(n, @"(?i)^Wi-?Fi|Wireless|WLAN")) return true;
        // Explicit non-wifi virtual/tunnel
        if (Regex.IsMatch(d, @"(?i)Bluetooth|Virtual|Hyper-V|vEthernet|TAP-|TUN-|WireGuard|OpenVPN|Wintun|Meta\s*Tunnel"))
            return false;
        return false;
    }

    /// <summary>
    /// Path policy: usable Ethernet (Up + real IPv4) → prefer eth 100% / disable Wi‑Fi.
    /// Link without IP → keep Wi‑Fi. Wi‑Fi only → prefer 6 then 5.
    /// </summary>
    public static PathDecision DecidePath(
        bool ethAvailable,
        bool ethUp,
        bool ethInUse,
        bool wifiAvailable,
        bool wifiUp,
        bool supports6Ghz,
        bool supports5Ghz,
        bool wifi6,
        bool wifi7)
    {
        var bandTarget = supports6Ghz ? "6GHz" : supports5Ghz ? "5GHz" : "Auto";
        string policy;
        var disableWifi = false;
        var keepWifiNoIp = false;

        if (ethInUse)
        {
            disableWifi = wifiAvailable;
            policy = wifiAvailable
                ? "Ethernet ready → prefer Ethernet 100%, disable Wi‑Fi"
                : "Ethernet ready → Ethernet only";
        }
        else if (ethUp && !ethInUse)
        {
            keepWifiNoIp = true;
            policy = "Ethernet linked (no IP yet) → keep Wi‑Fi until Ethernet has address";
        }
        else if (wifiUp)
        {
            policy = $"Wi‑Fi only → prefer {bandTarget}" +
                     (wifi7 ? " (Wi‑Fi 7 client)" : wifi6 ? " (Wi‑Fi 6/6E client)" : "");
        }
        else if (ethAvailable)
            policy = "Ethernet present (down) → prefer when linked + IP";
        else if (wifiAvailable)
            policy = $"Wi‑Fi only → prefer {bandTarget}";
        else
            policy = "No physical adapter detected";

        return new PathDecision(policy, bandTarget, disableWifi, keepWifiNoIp);
    }

    /// <summary>Should path policy disable Wi‑Fi given prefer-eth option?</summary>
    public static bool ShouldDisableWifi(bool preferEthernetOption, bool ethInUse, bool wifiAvailable) =>
        preferEthernetOption && ethInUse && wifiAvailable;

    /// <summary>True when IPv4 is real (not empty, not APIPA).</summary>
    public static bool IsUsableIpv4(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip is "—" or "-") return false;
        if (ip.StartsWith("169.254.", StringComparison.Ordinal)) return false;
        return System.Net.IPAddress.TryParse(ip, out var a) &&
               a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
    }

    /// <summary>Infer client 5/6 GHz support from free-text driver/property blobs.</summary>
    public static void InferBandSupport(string blob, ref bool band5, ref bool band6, ref bool ax, ref bool be)
    {
        if (string.IsNullOrEmpty(blob)) return;
        if (Regex.IsMatch(blob, @"(?i)6\s*GHz|6GHz|Wi-?Fi\s*6E|Prefer\s*6|6\s*GHz\s*prefer|band\s*6"))
            band6 = true;
        if (Regex.IsMatch(blob, @"(?i)5\s*GHz|5GHz|Prefer\s*5|5\s*GHz\s*prefer|5\.2\s*GHz|band\s*5"))
            band5 = true;
        if (Regex.IsMatch(blob, @"(?i)802\.11be|Wi-?Fi\s*7"))
        {
            be = true;
            band6 = true;
        }
        if (Regex.IsMatch(blob, @"(?i)802\.11ax|Wi-?Fi\s*6"))
            ax = true;
        if (Regex.IsMatch(blob, @"(?i)802\.11a|802\.11n|802\.11ac"))
            band5 = true;
    }

    /// <summary>Folklore assignments that must never appear as applied values in apply script.</summary>
    public static readonly string[] ForbiddenApplyPatterns =
    {
        "Set-Dword $tcp 'MaxUserPort'",
        "Set-Dword $mm 'SystemResponsiveness' 0",
        "Set-Dword $mm 'NetworkThrottlingIndex' 4294967295",
        "Set-Dword $mm 'NetworkThrottlingIndex' -1",
        "Set-Dword $tcp 'LargeSystemCache' 1",
        "Set-Dword $tcp 'TcpWindowSize'",
        "Set-Dword $tcp 'GlobalMaxTcpWindowSize'",
        "Set-Dword $tcp 'EnableTCPChimney' 1",
    };

    /// <summary>Required host-stack markers in every apply script.</summary>
    public static readonly string[] RequiredHostMarkers =
    {
        "SystemResponsiveness' 10",
        "NetworkThrottlingIndex' 10",
        "NonBestEffortLimit' 0",
        "DisableTaskOffload' 0",
        "congestionprovider=cubic",
        "wantBand6Live",
        "Select-BandDisplayValue",
    };

    /// <summary>Audit a generated apply script for peak host markers and folklore absence.</summary>
    public static (bool Ok, List<string> Issues) AuditApplyScript(string script, NetworkPreset preset)
    {
        var issues = new List<string>();
        if (string.IsNullOrWhiteSpace(script))
        {
            issues.Add("empty script");
            return (false, issues);
        }

        foreach (var m in RequiredHostMarkers)
        {
            if (script.IndexOf(m, StringComparison.OrdinalIgnoreCase) < 0)
                issues.Add("missing required: " + m);
        }

        foreach (var f in ForbiddenApplyPatterns)
        {
            if (script.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                issues.Add("forbidden present: " + f);
        }

        var knobs = KnobsFor(preset);
        if (script.IndexOf("autotuninglevel=" + knobs.AutotuneNetsh, StringComparison.OrdinalIgnoreCase) < 0)
            issues.Add("autotune mismatch for " + preset + " expect " + knobs.AutotuneNetsh);
        if (script.IndexOf("rsc=" + knobs.Rsc, StringComparison.OrdinalIgnoreCase) < 0)
            issues.Add("rsc mismatch for " + preset + " expect " + knobs.Rsc);
        // LSO registry value appears as Set-Adv ... '*LsoV2IPv4' 0|1
        if (script.IndexOf("*LsoV2IPv4' " + knobs.Lso, StringComparison.Ordinal) < 0 &&
            script.IndexOf("*LsoV2IPv4' " + knobs.Lso, StringComparison.OrdinalIgnoreCase) < 0)
        {
            // builder emits: Set-Adv $n '*LsoV2IPv4' 0
            if (script.IndexOf("'*LsoV2IPv4' " + knobs.Lso, StringComparison.Ordinal) < 0)
                issues.Add("LSO mismatch for " + preset + " expect " + knobs.Lso);
        }

        if (script.IndexOf("'*FlowControl' " + knobs.FlowControl, StringComparison.Ordinal) < 0)
            issues.Add("FlowControl mismatch for " + preset + " expect " + knobs.FlowControl);

        return (issues.Count == 0, issues);
    }
}
