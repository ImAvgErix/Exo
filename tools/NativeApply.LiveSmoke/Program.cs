using System;
using Exo.Services;
class Program {
  static int Main() {
    Console.WriteLine("Riot apply (native only)...");
    var r = LauncherNativeApply.Apply("riot", false, new Progress<string>(s => Console.WriteLine("  " + s)));
    foreach (var s in r.Steps) Console.WriteLine(s.ToReportLine());
    Console.WriteLine("Ok=" + r.Ok);
    using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
    var v = run?.GetValue("Exo-Riot-Yield")?.ToString() ?? "(none)";
    Console.WriteLine("Run key=" + v);
    if (v.Contains("wscript", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL still wscript"); return 1; }
    if (v.Contains(@"WindowsApps\pwsh", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("FAIL still apps stub"); return 1; }
    var det = NativeLiveDetect.DetectLauncher("riot");
    Console.WriteLine("detect isApplied=" + det.IsApplied + " " + det.StatusText);
    foreach (var f in det.Features)
      Console.WriteLine("  [" + (f.IsActive?"ON":"OFF") + "] " + f.Title);
    return v.Contains("wscript") ? 1 : 0;
  }
}
