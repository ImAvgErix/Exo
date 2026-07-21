' Exo Discord launcher - prefer official Update.exe (modern Discord host integrity).
' Falls back to Discord.exe, then Discord-Optimizer -Launch only if host files are missing.
Option Explicit
Dim fso, shell, kitDir, rootDir, localApp, discordRoot, updateExe, exe
Dim bestApp, folder, ver, bestVer, ps, optimizer, portable, stable, fallback

Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")

kitDir = fso.GetParentFolderName(WScript.ScriptFullName)
rootDir = fso.GetParentFolderName(kitDir)
localApp = shell.ExpandEnvironmentStrings("%LOCALAPPDATA%")
discordRoot = localApp & "\Discord"
updateExe = discordRoot & "\Update.exe"

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

' 1) Official squirrel launcher - most reliable on modern Discord (modules/host check)
If fso.FileExists(updateExe) And bestApp <> "" Then
  If fso.FileExists(bestApp & "\Discord.exe") Then
    shell.Run """" & updateExe & """ --processStart Discord.exe", 1, False
    WScript.Quit 0
  End If
End If

' 2) Direct Discord.exe if Update is missing
If bestApp <> "" Then
  exe = bestApp & "\Discord.exe"
  If fso.FileExists(exe) Then
    shell.Run """" & exe & """", 1, False
    WScript.Quit 0
  End If
End If

' 3) Heal path: no host found
optimizer = rootDir & "\Discord-Optimizer.ps1"
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
If fso.FileExists(optimizer) Then
  shell.Run """" & ps & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & optimizer & """ -Launch", 0, False
End If

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
