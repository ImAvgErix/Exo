Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
kitDir = fso.GetParentFolderName(WScript.ScriptFullName)
rootDir = fso.GetParentFolderName(kitDir)
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
