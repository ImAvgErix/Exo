' OptiHub Discord launcher - direct Discord.exe when healthy (no PowerShell hitch).
' Falls back to Disc-Optimizer -Launch only if OpenAsar/kernel files are missing.
Option Explicit
Dim fso, shell, kitDir, rootDir, localApp, discordRoot, appDir, exe, resources
Dim openAsar, versionDll, configIni, ffmpeg, ffmpegReal, eqAsar
Dim bestApp, folder, ver, bestVer, healthy, ps, optimizer, portable, stable, fallback

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

kitDir = fso.GetParentFolderName(WScript.ScriptFullName)
rootDir = fso.GetParentFolderName(kitDir)
localApp = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%")
discordRoot = localApp & "\Discord"
eqAsar = shell.ExpandEnvironmentStrings("%APPDATA%") & "\Equicord\equicord.asar"

bestApp = ""
bestVer = ""
If fso.FolderExists(discordRoot) Then
  For Each folder In fso.GetFolder(discordRoot).SubFolders
    If LCase(Left(folder.Name, 4)) = "app-" Then
      ver = Mid(folder.Name, 5)
      If bestApp = "" Or CompareVer(ver, bestVer) > 0 Then
        bestApp = folder.Path
        bestVer = ver
      End If
    End If
  Next
End If

healthy = False
If bestApp <> "" Then
  exe = bestApp & "\Discord.exe"
  resources = bestApp & "\resources"
  openAsar = resources & "\_app.asar"
  versionDll = bestApp & "\version.dll"
  configIni = bestApp & "\config.ini"
  ffmpeg = bestApp & "\ffmpeg.dll"
  ffmpegReal = bestApp & "\ffmpeg_real.dll"
  If fso.FileExists(exe) And fso.FileExists(openAsar) And fso.FileExists(versionDll) And _
     fso.FileExists(configIni) And fso.FileExists(ffmpeg) And fso.FileExists(ffmpegReal) Then
    ' proxy ffmpeg is small; stock is large
    If fso.GetFile(ffmpeg).Size < 500000 And fso.GetFile(ffmpegReal).Size > 500000 And _
       fso.GetFile(openAsar).Size > 10000 And fso.GetFile(openAsar).Size < 500000 Then
      If fso.FileExists(eqAsar) Then
        If fso.GetFile(eqAsar).Size > 1000000 Then healthy = True
      End If
    End If
  End If
End If

If healthy Then
  ' Direct start - no PowerShell cold start hitch
  shell.Run """" & exe & """", 1, False
  WScript.Quit 0
End If

' Heal path: mods missing (usually after Discord update)
optimizer = rootDir & "\Disc-Optimizer.ps1"
portable = kitDir & "\tools\pwsh\pwsh.exe"
stable = shell.ExpandEnvironmentStrings("%ProgramFiles%\PowerShell\7\pwsh.exe")
fallback = shell.ExpandEnvironmentStrings("%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe")
If fso.FileExists(portable) Then
  ps = portable
ElseIf fso.FileExists(stable) Then
  ps = stable
Else
  ps = fallback
End If
shell.Run """" & ps & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & optimizer & """ -Launch", 0, False

Function CompareVer(a, b)
  Dim pa, pb, i, na, nb
  pa = Split(a, ".")
  pb = Split(b, ".")
  For i = 0 To 3
    na = 0: nb = 0
    If i <= UBound(pa) Then If IsNumeric(pa(i)) Then na = CLng(pa(i))
    If i <= UBound(pb) Then If IsNumeric(pb(i)) Then nb = CLng(pb(i))
    If na > nb Then CompareVer = 1: Exit Function
    If na < nb Then CompareVer = -1: Exit Function
  Next
  CompareVer = 0
End Function
