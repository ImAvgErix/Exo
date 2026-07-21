' Exo-RunHidden.vbs  -  run a full command line with window style 0 (no console flash).
' Usage: wscript.exe //nologo Exo-RunHidden.vbs "full command line"
Option Explicit
If WScript.Arguments.Count < 1 Then WScript.Quit 1
Dim sh
Set sh = CreateObject("WScript.Shell")
' 0 = hide window, False = do not wait
sh.Run WScript.Arguments(0), 0, False
