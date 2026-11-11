Option Explicit
Dim shell, files, folder
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
folder = files.GetParentFolderName(WScript.ScriptFullName)
shell.Run Chr(34) & folder & "\bin\Minus700K.exe" & Chr(34) & " --sign-in", 1, False
