# Elevation: attended and unattended runs

Reading a volume's MFT needs administrator rights, so a Direct sample scan of a live volume relaunches itself elevated, a Watch sample verb launches
the elevated broker, and Windows shows a UAC consent prompt. There are exactly two ways to work, and you pick by whether the owner is at
the desktop.

A full `dotnet test` run raises no prompt in either mode. The only tests that reach a sample's entry point
are `SampleProgramWatch_EntryPoint_UnknownOption_PrintsUsageAndReturnsTwo` (a command line the parser refuses) and the Benchmark entry-point test (which cannot elevate); every other test of the relaunch stubs the
dialog, `_canSelfElevate` and `_tryRunElevated`. Tests that need an elevated process skip themselves when the
process is not elevated.

## Attended: the owner is at the keyboard

Run a sample normally. This is allowed and expected, including from a lane running a smoke test.

```powershell
.\SampleProgram.Direct\bin\x64\Release\net10.0\SampleProgram.Direct.exe search C --name notes
```

Before the UAC prompt, a system-modal heads-up dialog (with a beep) names SampleProgram, says a UAC prompt will
follow and why, and lists the executable and the exact arguments. Press OK to continue or Cancel to stop and get
the manual-elevation instructions. A dismissal within 0.75 seconds is treated as an accidental key press and the
dialog is shown again. A dialog nobody answers for five minutes counts as Cancel, so a run left unattended does not
hang: no UAC prompt, the manual-elevation fallback, exit code 1.

A Watch verb that opens an index (`scan`, `watch`, `rescan`, `journal`) stays unelevated and the broker it
launches asks for elevation, so an attended, unelevated run shows the same heads-up dialog before the broker launch;
Cancel or the five-minute timeout skips the launch with the same fallback text and exit code 1. `cache` and
`elevation-status` touch no volume and show no dialog, and neither does Direct with `--source dump`.

The heads-up gate precedes creation of the scan's `BrokerSession`. One session serves the
whole run and launches its broker only when a drive needs a scan; offline drives do not
launch it. A session-construction failure is printed as `Error creating broker session: ...`.
A lazy launch failure is reported through the affected drive as `Error on drive X: ...`;
a later drive can retry a failed launch using the session's normal retry behavior.

## Double-click and bare launches

A sample started from Explorer owns its console window, which closes when the process ends. Each sample therefore
prints `Press any key to close this window.` and waits for a key press before exit when this process is the only one
attached to its console, neither input nor output is redirected, and `MFTLIB_SAMPLE_UNATTENDED` is not `1`. A run from an
existing shell never waits. The success, the usage after a parse error (exit 2) and an elevation that was declined
(exit 1) are all left on screen this way.

Direct local scans relaunch themselves elevated, waiting indefinitely for the elevated child process to finish; the
elevated run writes to `output.log` beside the executable, the unelevated console prints that path before it starts
the elevated run, and the elevated window closes without waiting.

Watch with no arguments is `watch` over every ready NTFS fixed drive: it prints which drives it chose, scans each with a
running row count (a line each 250000 rows within a scan phase), then prints changes until a key press. `--seconds N`
replaces the key press with a fixed length, and an unattended run that is elevated, or one whose input is redirected,
watches for ten seconds. An unattended run that is not elevated exits 1 without watching, as the Unattended section
below describes. `scan` makes one pass per listed drive and returns. Any drive that failed makes the exit code 1.

## Windows Defender process exclusions

`ElevationUtilities.TryAddDefenderProcessExclusions(processNames, relaunchArguments, elevation = null)` adds Windows Defender
process exclusions so scanning is not slowed. It is the one sequence both consumers use:

- not Windows: returns false and launches nothing;
- elevated: runs Windows PowerShell directly (no `runas`);
- able to self-elevate: `IElevationProvider.TryRunElevated(relaunchArguments, 60 seconds)`, whose elevated child calls
  the same member with the same names and, being elevated, runs the exclusions itself;
- otherwise: an elevated Windows PowerShell through the `runas` verb (the case of `dotnet run`).

The result is true only when the run exits with code zero. A declined UAC prompt, a process that does not start, a
non-zero exit and a timeout are all false. One PowerShell runner serves both PowerShell branches, starts the Windows PowerShell under the system directory
(`WindowsPowerShell\v1.0\powershell.exe`, by absolute path, never a name found through PATH or the current directory), and waits 30 seconds
before it kills the process; the self-elevated child gets 60 seconds, so the outer wait always outlasts the inner one.

Each name must be a plain file name: letters, digits, `.`, `-`, `_`, `+` and inner spaces, with at least one letter or
digit. Anything else (empty, whitespace-only, quotes, semicolons, backticks, dollar signs, pipes, path separators)
throws `ArgumentException` before anything runs, even off Windows. The script is one single-quoted
`Add-MpPreference -ExclusionProcess` literal per name, passed to PowerShell with `-EncodedCommand`, so a name cannot
inject a command.

Tests use `MFTLibTestExtensions.DefenderExclusionIsolation.ScriptPowerShell`, a process-global scope (use a nonparallel
fixture) that records each PowerShell launch and reports a chosen outcome. A fake `IElevationProvider` decides the
branch, so no test raises a UAC prompt, starts a real PowerShell or changes real Defender settings.

`SampleProgram.Direct exclude-defender` calls it for its own executable. The verb takes no drive and runs only when named.
An elevated run (the relaunched child) writes to `output.log` and never waits for a key. An attended, unelevated run shows
the heads-up dialog once, before the library call and so before either UAC route; Cancel or the five-minute timeout means
no library call, a printed line and exit 1. Under `MFTLIB_SAMPLE_UNATTENDED=1` an unelevated run prints that elevation was
skipped, launches nothing and exits 1.

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
declined prompt. It shows no dialog and requests no elevation, including for the Watch verbs, whose broker would
otherwise prompt. An already elevated Direct process ignores the variable; an elevated Watch run that is watching uses it to limit the watch to ten seconds when `--seconds` is omitted. Both samples skip the exit pause when unattended. Any other value, or no variable, means the attended
behavior. The variable is read by the samples only; the MFTLib library has no such switch.

While unattended:

- use `.\scripts\run-coverage.ps1 -NonInteractive` for coverage; it skips the tests that need administrator rights;
- run the non-admin suite with `dotnet test MFTLib.Tests/MFTLib.Tests.csproj -c Release -p:Platform=x64`;
- record anything that genuinely needs elevation (a volume or broker smoke) as an attended gap in the report, and
  never retry it in a loop.
