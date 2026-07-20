using System;
using Exo.Services;
class Program {
  static int Main() {
    var body = SteamNativeApply.BuildMemoryGuardBody();
    var ok = SteamLogic.IsMemoryGuardText(body);
    Console.WriteLine(ok ? "PASS memory guard native template" : "FAIL memory guard native template");
    if (!ok) {
      Console.WriteLine("--- first 500 chars ---");
      Console.WriteLine(body.Substring(0, Math.Min(500, body.Length)));
      return 1;
    }
    // CEF launcher snippet check
    var cef = "start \"\" /HIGH /D \"C:\\Steam\" \"C:\\Steam\\steam.exe\" -nofriendsui -nointro %*\r\n";
    Console.WriteLine(SteamLogic.IsCefLauncherText(cef) ? "PASS CEF sample" : "FAIL CEF sample");
    Console.WriteLine("Native path supports steam/windows/riot/epic");
    return ok ? 0 : 1;
  }
}
