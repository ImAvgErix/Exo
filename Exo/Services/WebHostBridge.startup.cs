using System.Text.Json;

namespace Exo.Services;

public sealed partial class WebHostBridge
{
    private object ListStartupEntries()
    {
        var entries = _services.StartupServices.ListStartupEntries();
        return new
        {
            ok = true,
            entries = entries.Select(e => new { e.Name, e.Location, e.Command, e.Enabled, e.Source })
        };
    }

    private object SetStartupEntry(JsonElement p, bool hasParams)
    {
        var name = ReadString(p, hasParams, "name");
        var location = ReadString(p, hasParams, "location");
        var enabled = ReadBool(p, hasParams, "enabled");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(location))
        {
            return new { ok = false, error = "name and location are required." };
        }

        var applied = _services.StartupServices.SetStartupEnabled(name, location, enabled);
        _services.StartupServices.SaveSnapshot();
        return new { ok = applied, error = applied ? null : "Could not change the startup entry." };
    }

    private object SaveStartupSnapshot()
    {
        _services.StartupServices.SaveSnapshot();
        return new { ok = true };
    }

    private object ListServices()
    {
        var services = _services.StartupServices.ListServices();
        return new
        {
            ok = true,
            services = services.Select(s => new { s.Name, s.DisplayName, s.State, s.StartMode, s.Account })
        };
    }

    private object ScanStorage()
    {
        var items = _services.Storage.Scan();
        return new
        {
            ok = true,
            totalBytes = items.Sum(i => i.Bytes),
            items = items.Select(i => new { i.Path, i.Bytes, i.Kind })
        };
    }

    private object CleanStorage()
    {
        var progress = new Progress<string>();
        var freed = _services.Storage.Clean(progress);
        return new { ok = true, freedBytes = freed };
    }

    private object StorageJournal()
    {
        var items = _services.Storage.Journal();
        return new
        {
            ok = true,
            items = items.Select(i => new { i.Path, i.Bytes, i.Kind })
        };
    }
}
