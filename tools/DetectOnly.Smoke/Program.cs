using System;
using System.Linq;
using Exo.Services;
class P {
  static void Main() {
    foreach (var name in new[]{"windows","riot","epic","steam"}) {
      var s = name switch {
        "windows" => NativeLiveDetect.DetectWindows(),
        "steam" => NativeLiveDetect.DetectSteam(),
        "riot" => NativeLiveDetect.DetectLauncher("riot"),
        _ => NativeLiveDetect.DetectLauncher("epic")
      };
      var offs = s.Features.Where(f => !f.IsActive).Select(f => f.Title).ToList();
      Console.WriteLine($"{name}: isApplied={s.IsApplied} | {s.StatusText}");
      if (offs.Count > 0) Console.WriteLine("  OFF: " + string.Join(", ", offs));
      else Console.WriteLine("  ALL ON");
    }
  }
}
