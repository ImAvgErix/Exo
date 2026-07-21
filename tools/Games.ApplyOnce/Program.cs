using Exo.Services;
Console.WriteLine("=== APPLY OPTIMIZED (verify) ===");
var games = new GameOptimizerService();
var before = games.DetectGame(GameOptimizerService.GameIdMarvelRivals);
Console.WriteLine("BEFORE: " + before.StatusText);
foreach (var f in before.Features) Console.WriteLine($"  [{(f.IsActive?"ON":"off")}] {f.Title}");

var sw = System.Diagnostics.Stopwatch.StartNew();
var prog = new Progress<string>(s => Console.WriteLine($"  [{sw.ElapsedMilliseconds}ms] {s}"));
var (ok, msg) = await games.ApplyAsync(GameOptimizerService.GameIdMarvelRivals, "optimized", prog);
sw.Stop();
Console.WriteLine(ok ? $"APPLY OK ({sw.ElapsedMilliseconds}ms): {msg}" : $"APPLY FAIL: {msg}");

var after = games.DetectGame(GameOptimizerService.GameIdMarvelRivals);
Console.WriteLine("AFTER: " + after.StatusText + " applied=" + after.IsApplied);
foreach (var f in after.Features) Console.WriteLine($"  [{(f.IsActive?"ON":"off")}] {f.Title} — {f.Detail}");

// Prove Engine.ini changed
var eng = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Marvel","Saved","Config","Windows","Engine.ini");
var text = File.ReadAllText(eng);
Console.WriteLine("Engine has profile=optimized: " + text.Contains("profile=optimized", StringComparison.OrdinalIgnoreCase));
Console.WriteLine("Engine has MipMapLODBias=0: " + text.Contains("r.MipMapLODBias=0", StringComparison.OrdinalIgnoreCase));
Console.WriteLine("Engine has MipMapLODBias=5: " + text.Contains("r.MipMapLODBias=5", StringComparison.OrdinalIgnoreCase));
Environment.Exit(ok ? 0 : 1);
