# Scan-to-Watch Session API Plan

Status: draft for a fresh MFTLib session

## Why this belongs in MFTLib first

A consumer can already reuse one `JournalBrokerClient` across discovery and live watching:

1. `ArmScanAndCatchUpAsync`
2. consume `BrokerScanResult.Records`
3. `SendStartWatchAsync` with `BrokerScanResult.AdvancedCursors`
4. consume `CreateBatchSource`

GitWizard accidentally discarded that continuity by retaining only `Records` and disposing the client after discovery. The protocol is capable, but the public API makes the correct ownership pattern easy to miss. Before publishing 0.3.0, review whether MFTLib should make a scan-to-watch session an explicit, difficult-to-misuse abstraction.

This work must be designed and reviewed in the MFTLib repository independently of GitWizard. Do not begin by copying GitWizard-specific types or lifecycle assumptions into MFTLib.

## Prerequisite: land the current reduced scan profile separately

The current MFTLib worktree contains an independent, already-validated change:

- additive `BrokerScanProfile.DirectoryIndexWithGitPointers`
- profile propagation through the broker request
- host-side filtering after the full scan
- broker tests and integration documentation

Land that as its own MFTLib commit before starting this API work. MFTLib is currently checked out as a detached submodule, so create an intentional MFTLib branch before committing. Keep the scan-profile commit reviewable and do not fold the session API into it.

## Required investigation

A fresh MFTLib agent should first answer these questions from the current code and tests:

1. Is a new public session type materially safer than improving `JournalBrokerClient` documentation and examples?
2. Which object owns the connected client after a successful scan?
3. How should ownership transfer be represented so the client cannot be disposed by both discovery and watch layers?
4. Should broker death be latched in queryable state so a consumer attaching after discovery can detect a broker that died while parked?
5. How does the abstraction handle a journal that wrapped between discovery and watch start?
6. How does it validate that the requested watch volumes match the successfully armed volumes and per-drive errors?
7. Can the same abstraction preserve the existing stop, rescan, and restart behavior without a second UAC prompt?
8. What cancellation token owns the parked period between scan completion and live-watch start?

## Candidate API shape, not a predetermined design

Evaluate an owned type such as `JournalBrokerScanSession : IAsyncDisposable` that keeps the connected client and its `BrokerScanResult` together. A useful abstraction would make these operations explicit:

- spawn/connect and arm a set of drives with a selected `BrokerScanProfile`
- expose the immutable scan result for discovery
- transition the same broker into live watching from the advanced cursors
- stop live watching and rescan using the same elevated broker
- report or latch terminal broker death
- dispose an unused, active, or failed session exactly once

Do not expose GitWizard concepts such as repository roots, UI state, or `IVolumeChangeSource`. The type must remain a generic MFT/USN broker primitive.

It is acceptable for the fresh review to conclude that no new type is warranted, but that conclusion must explain how the existing API will prevent or clearly flag the ownership mistake GitWizard made.

## Compatibility constraints

- Preserve all existing `JournalBrokerClient` public APIs and defaults.
- Preserve `BrokerScanProfile.Full` as the default overload behavior.
- Preserve per-drive errors rather than failing every drive when one cannot arm.
- Preserve the arm-before-scan and catch-up ordering guarantee.
- Preserve one pipe reader during live watch.
- Preserve `StopLiveWatchAsync` and rescan support.
- Avoid a second UAC prompt when transitioning from scan to watch.
- No GitWizard dependency or consumer-specific callback interface.

## Tests required in MFTLib

Add deterministic non-admin tests for whichever API is chosen:

- scan result and live start use the same underlying client/transport
- no second spawn or arm occurs during the transition
- advanced cursors, catch-up entries, and per-drive errors survive the handoff
- ownership is single-use and disposal is exactly once
- disposal before live start shuts down the parked broker
- cancellation during scan, parked state, live start, and active watch is safe
- broker death between scan and watch is observable to a later consumer
- volume mismatch and journal invalidation have explicit behavior
- stop, rescan, and restart still work on one broker
- existing client APIs remain source-compatible

Maintain MFTLib's 100% managed line, branch, method, and full-method coverage gates. Update `docs/broker-integration.md`, XML documentation, README references if applicable, and `CHANGELOG.md` with the final API rather than the candidate name used here.

## Deliverable and review boundary

Complete this work as an MFTLib branch and review it on its own merits. Publish or otherwise make the chosen MFTLib commit available on both remotes before changing the GitWizard submodule pointer. The GitWizard integration plan lives separately in its repository and should consume the finalized API rather than shaping it mid-implementation.
