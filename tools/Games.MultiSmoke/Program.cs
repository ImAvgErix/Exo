using Exo.Services;
var g = new GameOptimizerService();
var ids = new[] {
  GameOptimizerService.GameIdBlackOps7,
  GameOptimizerService.GameIdValorant,
  GameOptimizerService.GameIdLeague,
  GameOptimizerService.GameIdPredecessor,
  GameOptimizerService.GameIdMarvelRivals,
};
foreach (var id in ids) {
  Console.WriteLine("=== " + id + " ===");
  try {
    var (ok,msg) = await g.ApplyAsync(id, "optimized", GameOptimizerService.DisplayBorderless);
    Console.WriteLine((ok?"OK":"FAIL") + " " + msg);
    var d = g.DetectGame(id);
    Console.WriteLine("detect: " + d.StatusText + " applied=" + d.IsApplied);
  } catch (Exception ex) {
    Console.WriteLine("EX " + ex.Message);
  }
}
