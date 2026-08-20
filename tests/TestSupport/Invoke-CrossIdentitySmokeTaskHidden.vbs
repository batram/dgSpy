Option Explicit

Dim shell, powerShellPath, runnerPath, which, command, exitCode
If WScript.Arguments.Count <> 3 Then WScript.Quit 2

powerShellPath = WScript.Arguments(0)
runnerPath = WScript.Arguments(1)
which = WScript.Arguments(2)

Set shell = CreateObject("WScript.Shell")
command = Quote(powerShellPath) & " -NoProfile -ExecutionPolicy Bypass -File " & Quote(runnerPath) & " -Which " & Quote(which)
exitCode = shell.Run(command, 0, True)
WScript.Quit exitCode

Function Quote(value)
    Quote = Chr(34) & Replace(value, Chr(34), Chr(34) & Chr(34)) & Chr(34)
End Function
