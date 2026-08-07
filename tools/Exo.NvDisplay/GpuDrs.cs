// NVIDIA driver profile settings (DRS) applied through NVAPI directly.
//
// Parses Profile Inspector .nip packs (UTF-16 XML) and writes BOTH the Base Profile
// and per-application profiles (Exo - Valorant, etc.). The previous native path only
// wrote Base — every per-title Prefer-max / PRF / FG pin never reached the driver while
// Apply still stamped gameProfilesApplied green.
//
// Exit codes: 0 = all applicable settings verified, 3 = partial, 1 = hard fail, 2 = bad pack.

using System.Xml.Linq;
using NvAPIWrapper.DRS;

namespace Exo.NvDisplay;

internal static class GpuDrs
{
    private sealed record NipSetting(uint Id, uint Value, byte[]? Binary, string Name);

    private sealed record NipProfile(
        string Name,
        bool IsBase,
        IReadOnlyList<string> Executables,
        IReadOnlyList<NipSetting> Settings);

    /// <summary>Reads every profile in a .nip (Base + application profiles).</summary>
    private static List<NipProfile> ParseNipProfiles(string path)
    {
        var list = new List<NipProfile>();
        var doc = XDocument.Load(path);

        foreach (var profile in doc.Descendants("Profile"))
        {
            var profileName = profile.Element("ProfileName")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(profileName)) continue;

            var isBase = string.Equals(profileName, "Base Profile", StringComparison.OrdinalIgnoreCase);
            var exes = new List<string>();
            foreach (var s in profile.Descendants("Executeables").Descendants("string"))
            {
                var e = s.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(e)) exes.Add(e);
            }
            // Some packs use "Executables" spelling
            foreach (var s in profile.Descendants("Executables").Descendants("string"))
            {
                var e = s.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(e) && !exes.Contains(e, StringComparer.OrdinalIgnoreCase))
                    exes.Add(e);
            }

            var settings = new List<NipSetting>();
            foreach (var ps in profile.Descendants("ProfileSetting"))
            {
                var idText = ps.Element("SettingID")?.Value?.Trim();
                var valText = ps.Element("SettingValue")?.Value?.Trim();
                var type = ps.Element("ValueType")?.Value?.Trim();
                var name = ps.Element("SettingNameInfo")?.Value?.Trim() ?? idText ?? "?";

                if (!uint.TryParse(idText, out var id))
                {
                    Console.WriteLine($"[DRS] skip {name}: unparseable id ({idText})");
                    continue;
                }

                if (string.Equals(type, "Dword", StringComparison.OrdinalIgnoreCase))
                {
                    if (!uint.TryParse(valText, out var val))
                    {
                        Console.WriteLine($"[DRS] skip {name}: unparseable dword ({valText})");
                        continue;
                    }
                    settings.Add(new NipSetting(id, val, null, name));
                }
                else if (string.Equals(type, "Qword", StringComparison.OrdinalIgnoreCase))
                {
                    if (!ulong.TryParse(valText, out var qval))
                    {
                        Console.WriteLine($"[DRS] skip {name}: unparseable qword ({valText})");
                        continue;
                    }
                    settings.Add(new NipSetting(id, 0, BitConverter.GetBytes(qval), name));
                }
                else
                {
                    Console.WriteLine($"[DRS] skip {name}: value type {type} not supported");
                }
            }

            if (settings.Count == 0 && !isBase) continue;
            list.Add(new NipProfile(profileName, isBase, exes, settings));
        }

        return list;
    }

    public static int Apply(string nipPath)
    {
        if (!File.Exists(nipPath))
        {
            Console.Error.WriteLine($"[DRS] Profile pack not found: {nipPath}");
            return 2;
        }

        List<NipProfile> profiles;
        try { profiles = ParseNipProfiles(nipPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DRS] Could not read {Path.GetFileName(nipPath)}: {ex.Message}");
            return 2;
        }

        var baseProfile = profiles.FirstOrDefault(p => p.IsBase);
        var appProfiles = profiles.Where(p => !p.IsBase).ToList();
        if (baseProfile is null || baseProfile.Settings.Count == 0)
        {
            Console.Error.WriteLine("[DRS] Pack contained no usable Base Profile settings.");
            return 2;
        }

        Console.WriteLine(
            $"[DRS] {Path.GetFileName(nipPath)}: base={baseProfile.Settings.Count} setting(s), " +
            $"app-profiles={appProfiles.Count}");

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var global = session.CurrentGlobalProfile;
            if (global is null)
            {
                Console.Error.WriteLine("[DRS] Driver returned no global profile.");
                return 1;
            }

            var baseUnsupported = new HashSet<uint>();
            var baseWritten = WriteSettings(global, baseProfile.Settings, baseUnsupported, "Base");
            var appsWritten = 0;
            var appsFailed = 0;

            foreach (var app in appProfiles)
            {
                try
                {
                    // NvAPIWrapper.FindProfileByName throws NVAPI_PROFILE_NOT_FOUND (does not
                    // return null). Prefer the live profile already bound to the game exe —
                    // stock NVIDIA titles often already own VALORANT-Win64-Shipping.exe etc.,
                    // and CreateApplication then fails with EXECUTABLE_ALREADY_IN_USE while an
                    // empty "Exo - Title" profile never receives the game.
                    var prof = ResolveAppProfile(session, app)
                               ?? DriverSettingsProfile.CreateProfile(session, app.Name, null);
                    EnsureApplications(prof, app.Executables, app.Name);
                    var unsup = new HashSet<uint>();
                    WriteSettings(prof, app.Settings, unsup, app.Name);
                    appsWritten++;
                    Console.WriteLine(
                        $"[DRS] app-profile ok: {app.Name} -> '{prof.Name}' " +
                        $"(exes={app.Executables.Count}, settings={app.Settings.Count})");
                }
                catch (Exception ex)
                {
                    appsFailed++;
                    Console.WriteLine($"[DRS] app-profile fail: {app.Name}: {Reason(ex)}");
                }
            }

            session.Save();

            // Fresh-session readback
            var baseVerified = 0;
            var baseApplicable = baseProfile.Settings.Count - baseUnsupported.Count;
            var appVerified = 0;
            using (var check = DriverSettingsSession.CreateAndLoad())
            {
                var liveGlobal = check.CurrentGlobalProfile;
                foreach (var s in baseProfile.Settings)
                {
                    if (baseUnsupported.Contains(s.Id)) continue;
                    try
                    {
                        var got = liveGlobal?.GetSetting(s.Id)?.CurrentValue;
                        if (Matches(got, s)) baseVerified++;
                        else Console.WriteLine($"[DRS] not verified (Base): {s.Name} wanted {Describe(s)}, got {got?.ToString() ?? "nothing"}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DRS] not verified (Base): {s.Name}: {Reason(ex)}");
                    }
                }

                foreach (var app in appProfiles)
                {
                    try
                    {
                        var live = ResolveAppProfile(check, app);
                        if (live is null)
                        {
                            Console.WriteLine($"[DRS] app-profile missing after save: {app.Name}");
                            continue;
                        }
                        var ok = 0;
                        var need = 0;
                        foreach (var s in app.Settings)
                        {
                            need++;
                            try
                            {
                                var got = live.GetSetting(s.Id)?.CurrentValue;
                                if (Matches(got, s)) ok++;
                            }
                            catch { /* count miss */ }
                        }
                        if (need > 0 && ok >= Math.Max(1, need * 2 / 3))
                        {
                            appVerified++;
                            Console.WriteLine($"[DRS] app-profile verified: {app.Name} on '{live.Name}' ({ok}/{need})");
                        }
                        else
                            Console.WriteLine($"[DRS] app-profile weak verify: {app.Name} on '{live.Name}' ({ok}/{need})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DRS] app-profile verify fail: {app.Name}: {Reason(ex)}");
                    }
                }
            }

            Console.WriteLine(
                $"[DRS] summary base written={baseWritten} verified={baseVerified}/{Math.Max(0, baseApplicable)} " +
                $"app written={appsWritten} verified={appVerified}/{appProfiles.Count} failed={appsFailed}");
            // Machine-readable line for PowerShell Import-ExoNipProfile
            Console.WriteLine($"[DRS] app-profiles written={appsWritten} verified={appVerified} expected={appProfiles.Count}");

            if (baseApplicable <= 0) return 1;
            var baseOk = baseVerified == baseApplicable;
            var appsOk = appProfiles.Count == 0 || (appVerified == appProfiles.Count && appsFailed == 0);
            if (baseOk && appsOk) return 0;
            if (baseVerified > 0 || appVerified > 0) return 3;
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DRS] session failed: {Reason(ex)}");
            return 1;
        }
    }

    private static int WriteSettings(
        DriverSettingsProfile profile,
        IReadOnlyList<NipSetting> settings,
        HashSet<uint> unsupported,
        string label)
    {
        var written = 0;
        foreach (var s in settings)
        {
            try
            {
                if (s.Binary is not null) profile.SetSetting(s.Id, s.Binary);
                else profile.SetSetting(s.Id, s.Value);
                written++;
            }
            catch (Exception ex)
            {
                if (IsUnsupportedByDriver(ex)) unsupported.Add(s.Id);
                else Console.WriteLine($"[DRS] write failed ({label}): {s.Name} ({s.Id}): {Reason(ex)}");
            }
        }
        return written;
    }

    /// <summary>
    /// Locate the DRS profile that actually owns this title.
    /// FindProfileByName throws when missing; FindApplicationProfile is the reliable path
    /// for titles NVIDIA already shipped a stock profile for.
    /// </summary>
    private static DriverSettingsProfile? ResolveAppProfile(DriverSettingsSession session, NipProfile app)
    {
        foreach (var exe in app.Executables)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            try
            {
                var byExe = session.FindApplicationProfile(exe);
                if (byExe is not null) return byExe;
            }
            catch
            {
                // Not bound yet — keep looking.
            }
        }

        try
        {
            return session.FindProfileByName(app.Name);
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureApplications(DriverSettingsProfile profile, IReadOnlyList<string> executables, string friendly)
    {
        foreach (var exe in executables)
        {
            if (string.IsNullOrWhiteSpace(exe)) continue;
            try
            {
                var existing = profile.GetApplicationByName(exe);
                if (existing is not null) continue;
            }
            catch { /* not found */ }

            try
            {
                // applicationName, friendlyName, launcherName, filesInFolder, isMetro, commandLine
                ProfileApplication.CreateApplication(
                    profile,
                    exe,
                    friendly,
                    string.Empty,
                    Array.Empty<string>(),
                    false,
                    string.Empty);
            }
            catch (Exception ex)
            {
                // EXECUTABLE_ALREADY_IN_USE means stock NVIDIA (or another) profile owns the
                // exe — settings were written on the resolved profile via ResolveAppProfile.
                Console.WriteLine($"[DRS] app bind {exe} on {friendly}: {Reason(ex)}");
            }
        }
    }

    /// <summary>Reports Base + Exo app profiles against a pack (or Base-only if pack is Base-only).</summary>
    public static int Status(string nipPath)
    {
        if (!File.Exists(nipPath))
        {
            Console.Error.WriteLine($"[DRS] Profile pack not found: {nipPath}");
            return 2;
        }

        List<NipProfile> profiles;
        try { profiles = ParseNipProfiles(nipPath); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DRS] Could not read pack: {ex.Message}");
            return 2;
        }

        var baseProfile = profiles.FirstOrDefault(p => p.IsBase);
        var appProfiles = profiles.Where(p => !p.IsBase).ToList();
        if (baseProfile is null) return 2;

        try
        {
            using var session = DriverSettingsSession.CreateAndLoad();
            var global = session.CurrentGlobalProfile;
            var match = 0;
            foreach (var s in baseProfile.Settings)
            {
                object? got = null;
                try { got = global?.GetSetting(s.Id)?.CurrentValue; } catch { }
                if (Matches(got, s)) match++;
                else Console.WriteLine($"[DRS] {s.Name}: wanted {Describe(s)}, got {got?.ToString() ?? "nothing"}");
            }
            Console.WriteLine($"[DRS] status base matched={match} of {baseProfile.Settings.Count}");

            var appOk = 0;
            foreach (var app in appProfiles)
            {
                var live = ResolveAppProfile(session, app);
                if (live is null)
                {
                    Console.WriteLine($"[DRS] status missing app-profile: {app.Name}");
                    continue;
                }
                var ok = 0;
                foreach (var s in app.Settings)
                {
                    try
                    {
                        var got = live.GetSetting(s.Id)?.CurrentValue;
                        if (Matches(got, s)) ok++;
                    }
                    catch { }
                }
                if (app.Settings.Count > 0 && ok >= Math.Max(1, app.Settings.Count * 2 / 3))
                    appOk++;
                Console.WriteLine($"[DRS] status app {app.Name} on '{live.Name}': {ok}/{app.Settings.Count}");
            }
            Console.WriteLine($"[DRS] status app-profiles matched={appOk} of {appProfiles.Count}");
            Console.WriteLine($"[DRS] app-profiles written={appOk} verified={appOk} expected={appProfiles.Count}");

            var baseOk = match == baseProfile.Settings.Count;
            var appsOk = appProfiles.Count == 0 || appOk == appProfiles.Count;
            if (baseOk && appsOk) return 0;
            if (match > 0 || appOk > 0) return 3;
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DRS] session failed: {Reason(ex)}");
            return 1;
        }
    }

    private static bool Matches(object? got, NipSetting s)
    {
        if (got is null) return false;
        if (s.Binary is not null)
            return got is byte[] b && b.Length == s.Binary.Length && b.SequenceEqual(s.Binary);
        try { return Convert.ToUInt32(got) == s.Value; }
        catch { return false; }
    }

    private static string Describe(NipSetting s) =>
        s.Binary is not null ? BitConverter.ToUInt64(s.Binary, 0).ToString() : s.Value.ToString();

    private static string Reason(Exception ex) =>
        ex is NvAPIWrapper.Native.Exceptions.NVIDIANotSupportedException
            ? "not supported on this driver/GPU"
            : ex.Message;

    private static bool IsUnsupportedByDriver(Exception ex) =>
        ex is NvAPIWrapper.Native.Exceptions.NVIDIANotSupportedException ||
        ex.Message.Contains("NVAPI_SETTING_NOT_FOUND", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("NVAPI_NOT_SUPPORTED", StringComparison.OrdinalIgnoreCase);
}
