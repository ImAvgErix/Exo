using Exo.Services;
var g = new GameOptimizerService();
var prog = new Progress<string>(s => Console.WriteLine("  " + s));

// BO7 borderless
Console.WriteLine("=== BO7 borderless ===");
var (ok, msg) = await g.ApplyAsync(GameOptimizerService.GameIdBlackOps7, "optimized", GameOptimizerService.DisplayBorderless, prog);
Console.WriteLine(ok ? "OK " + msg : "FAIL " + msg);
var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Activision","Call of Duty","players","s.1.0.cod25.txt0");
foreach (var line in File.ReadAllLines(p))
  if (line.Contains("DisplayMode@") || line.Contains("PreferredDisplayMode@") || line.Contains("Exo Games"))
    Console.WriteLine("  " + line.Trim());

// Valorant exclusive
Console.WriteLine("\n=== VAL exclusive ===");
var (vok, vmsg) = await g.ApplyAsync(GameOptimizerService.GameIdValorant, "optimized", GameOptimizerService.DisplayExclusive, prog);
Console.WriteLine(vok ? "OK " + vmsg : "FAIL " + vmsg);
var gus = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT","Saved","Config","WindowsClient","GameUserSettings.ini");
foreach (var line in File.ReadAllLines(gus))
  if (line.Contains("Fullscreen") || line.Contains("Exo"))
    Console.WriteLine("  " + line.Trim());

// League borderless
Console.WriteLine("\n=== LEAGUE borderless ===");
var (lok, lmsg) = await g.ApplyAsync(GameOptimizerService.GameIdLeague, "optimized", GameOptimizerService.DisplayBorderless, prog);
Console.WriteLine(lok ? "OK " + lmsg : "FAIL " + lmsg);
foreach (var line in File.ReadAllLines(@"C:\Riot Games\League of Legends\Config\game.cfg"))
  if (line.Contains("WindowMode") || line.Contains("Exo"))
    Console.WriteLine("  " + line.Trim());

// Pred leave (no change to mode)
Console.WriteLine("\n=== PRED leave ===");
var before = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Predecessor","Saved","Config","WindowsClient","GameUserSettings.ini"));
var mBefore = System.Text.RegularExpressions.Regex.Match(before, @"FullscreenMode=(\d+)");
var (pok, pmsg) = await g.ApplyAsync(GameOptimizerService.GameIdPredecessor, "optimized", GameOptimizerService.DisplayLeave, prog);
Console.WriteLine(pok ? "OK " + pmsg : "FAIL " + pmsg);
var after = File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Predecessor","Saved","Config","WindowsClient","GameUserSettings.ini"));
var mAfter = System.Text.RegularExpressions.Regex.Match(after, @"FullscreenMode=(\d+)");
Console.WriteLine($"FullscreenMode before={mBefore.Groups[1].Value} after={mAfter.Groups[1].Value} (should match if leave)");
