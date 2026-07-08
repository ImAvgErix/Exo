Set fso = CreateObject("Scripting.FileSystemObject")
kitDir = fso.GetParentFolderName(WScript.ScriptFullName)
rootDir = fso.GetParentFolderName(kitDir)
optimizer = rootDir & "\Disc-Optimizer.ps1"
ps = "C:\Program Files\WindowsApps\Microsoft.PowerShellPreview_7.7.2.0_x64__8wekyb3d8bbwe\pwsh.exe"
CreateObject("WScript.Shell").Run """" & ps & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & optimizer & """ -Launch", 0, False
