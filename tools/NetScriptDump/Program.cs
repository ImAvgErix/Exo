using Exo.Models;
using Exo.Services;

// NetScriptDump: writes every generated network script (apply for both presets,
// repair, benchmark) to an output directory so Windows CI runners can parse and
// execute them end-to-end. Usage: NetScriptDump <output-directory>

if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: NetScriptDump <output-directory>");
    return 2;
}

var outDir = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outDir);

var media = new NetworkMediaProfile
{
    ClientSupports6Ghz = true,
    ClientSupports5Ghz = true,
    EthernetInUse = true,
    WifiAvailable = true
};
var opts = new NetworkApplyOptions { PreferEthernetDisableWifi = true, RestartEthernet = false };

var files = new (string Name, string Text)[]
{
    ("apply-lowest-latency.ps1", NetworkApplyScriptBuilder.Build(NetworkPreset.LowestLatency, opts, media)),
    ("apply-highest-throughput.ps1", NetworkApplyScriptBuilder.Build(NetworkPreset.HighestThroughput, opts, media)),
    ("repair.ps1", NetworkApplyScriptBuilder.BuildRepair()),
    ("benchmark.ps1", NetworkApplyScriptBuilder.BuildBenchmark()),
};

foreach (var (name, text) in files)
{
    var path = Path.Combine(outDir, name);
    File.WriteAllText(path, text);
    Console.WriteLine($"wrote {path} ({text.Length} chars)");
}

Console.WriteLine($"NetScriptDump: {files.Length} scripts written to {outDir}");
return 0;
