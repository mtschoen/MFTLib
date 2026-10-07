# Elevation: attended and unattended runs

Reading a volume's MFT needs administrator rights, so some sample verbs relaunch themselves elevated and
Windows shows a UAC consent prompt. There are exactly two ways to work, and you pick by whether the owner is at
the desktop.

A full `dotnet test` run raises no prompt in either mode. The only tests that reach a sample's entry point
are `SampleProgramWatch_EntryPoint_UnknownOption_PrintsUsageAndReturnsTwo` (a command line the parser refuses) and the Benchmark entry-point test (which cannot elevate); every other test of the relaunch stubs the
dialog, `_canSelfElevate` and `_tryRunElevated`. Tests that need an elevated process skip themselves when the
process is not elevated.

## Attended: the owner is at the keyboard

Run a sample normally. This is allowed and expected, including from a lane running a smoke test.

```powershell
.\SampleProgram.Watch\bin\x64\Release\net10.0\SampleProgram.Watch.exe scan-drive C:
```

Before the UAC prompt, a system-modal heads-up dialog (with a beep) names SampleProgram, says a UAC prompt will
follow and why, and lists the executable and the exact arguments. Press OK to continue or Cancel to stop and get
the manual-elevation instructions. A dismissal within 0.75 seconds is treated as an accidental key press and the
dialog is shown again. A dialog nobody answers for five minutes counts as Cancel, so a run left unattended does not
hang: no UAC prompt, the manual-elevation fallback, exit code 1.

`scan-drive` stays unelevated and the broker it launches asks for elevation, so an attended, unelevated
`scan-drive` run shows the same heads-up dialog before the broker launch; Cancel or the five-minute timeout
skips the launch with the same fallback text and exit code 1.

The heads-up gate precedes creation of the scan's `BrokerSession`. One session serves the
whole run and launches its broker only when a drive needs a scan; offline drives do not
launch it. A session-construction failure is printed as `Error creating broker session: ...`.
A lazy launch failure is reported through the affected drive as `Error on drive X: ...`;
a later drive can retry a failed launch using the session's normal retry behavior.

## Unattended: the owner said to proceed autonomously, or nobody is at the desktop

Set the environment variable once for the whole session; every lane and child process inherits it.

```powershell
$env:MFTLIB_SAMPLE_UNATTENDED = '1'      # PowerShell
```

```bash
export MFTLIB_SAMPLE_UNATTENDED=1        # bash
```

With the value exactly `1` and a process that is not elevated, the sample prints one line saying it is running
unattended and elevation was skipped, prints the manual-elevation fallback, and exits with code 1, the same code as a
declined prompt. It shows no dialog and requests no elevation, including for `scan-drive`, whose broker would
otherwise prompt. An already elevated process ignores the variable. Any other value, or no variable, means the attended
behavior. The variable is read by the samples only; the MFTLib library has no such switch.

While unattended:

- use `.\scripts\run-coverage.ps1 -NonInteractive` for coverage; it skips the tests that need administrator rights;
- run the non-admin suite with `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64`;
- record anything that genuinely needs elevation (a volume or broker smoke) as an attended gap in the report, and
  never retry it in a loop.
