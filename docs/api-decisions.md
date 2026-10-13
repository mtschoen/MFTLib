# Public API decisions

Owner rulings on the shape of the public surface, each with the reason it was made. Read this page before
proposing to remove, move or reshape a public member. A review that finds "MFTLib never calls this itself" or
"this is not an MFT primitive" has found a fact, not a reason: check the ruling here first, and reopen one only
with evidence that its stated reason no longer holds (for example, the consumers' needs have diverged).

Record every new ruling here in the same change that implements or files it: the decision, the reason, and
what would justify revisiting it.

## Elevation helpers live in MFTLib

**Decision (2026-10-13, API review finding F9):** `IElevationProvider` and `ElevationUtilities` stay public in
MFTLib, unchanged.

**Reason:** both consumers had their own elevation code, it turned out to be the same code, and it was hoisted
here so it exists once. The 2026-10-12 API review recommended moving it back out because the library does not
call it; a comparison of the consumers on 2026-10-13 found their use identical, so removal would only recreate
the duplication:

- Both decide broker-backed versus in-process scanning with `IsElevated()` and `CanSelfElevate()`, which is a
  decision about MFT scanning.
- Both add Windows Defender process exclusions with the same sequence: already elevated, run directly; able to
  self-elevate, `TryRunElevated`; otherwise an elevated PowerShell.
- The samples self-elevate through the same provider ([elevation](elevation.md)).

The broker launcher does not use `TryRunElevated`, on purpose: that call waits for the elevated child to exit,
and the broker has to keep running on its pipe. That is not evidence the helper is unused.

**Implementation (2026-10-13, [MFTLib issue 472](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/472)):**
the Defender sequence is `ElevationUtilities.TryAddDefenderProcessExclusions(processNames, relaunchArguments, elevation = null)`,
one method on the existing type; [elevation](elevation.md#windows-defender-process-exclusions) holds its contract.

**Revisit only if:** one consumer needs elevation behavior the other cannot share.

## The enumeration producer is a product feature, chosen per drive

**Decision (2026-10-13, API review finding F6):** the directory-walk producer stays public. It is how an index
is built where the MFT is not available: Linux, non-NTFS volumes, and a run without elevation. The producer
choice belongs to each drive, not to the whole open, so one index can hold an MFT-scanned drive beside a
directory-walked one ([MFTLib issue 468](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/468)).

**Reason:** the review found no shipping caller and recommended moving the producer into the test package. The
absence of a caller was a gap in the consumers, not the intent: file-wizard documents the producer but exposes
no way to select it
([file-wizard issue 561](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/561)), and git-wizard
carries a separate hand-rolled walk
([git-wizard issue 311](https://gitea.fleet.sticktoitive.net/schoen/git-wizard/issues/311)). The whole-open
policy predated the per-drive refactor.

**Unchanged rule:** a drive whose MFT scan fails is `Failed`; it is never rebuilt by a directory walk on the
caller's behalf. Enumeration runs only for a drive that asks for it.

**Revisit only if:** both consumers decide against indexing anything but elevated NTFS volumes.

## One key type for drives, one method per lifecycle verb

**Decision (2026-10-13, API review findings F7 and F8, ruled together):** a `DriveKey` struct replaces the bare
`char` drive letter (and the test package's `string` letters) across the public surface. It normalizes case
once, converts implicitly from `char` and never from `string`. Each lifecycle verb of `FileIndex` (start
watching, stop watching, wait for catch-up, rescan) is one method taking `IEnumerable<DriveKey>` and returning
one `DriveOperationResult` per requested drive, with no overloads
([MFTLib issue 470](https://gitea.fleet.sticktoitive.net/schoen/MFTLib/issues/470)).

**Reason:** the letter was the key of the whole API with no owner: case rules, parameter types and collection
types differed from member to member, both consumers up-cased what the library returned, and the four verbs had
an uneven set of single, list and all-drives overloads. A bare `IEnumerable<char>` would accept a `string`
("CD" as two drives), which is why the key has its own type. The struct is also the one place a key could later
become something other than an ASCII letter for Linux and enumeration roots.

**Deferred on purpose:** whether a key may be a label rather than a letter.

**Implementation note (2026-10-13):** the default struct value is invalid; its `Letter` and `ToString`
and public key-accepting operations reject it with `ArgumentException`. Key-bearing record constructors
and `Drive` init accessors validate it too. Members carrying keys are named `Drive`; the letter accessor
on the key itself is `Letter`. Lifecycle requests are materialized once, reject duplicates before work
starts and preserve request order, matching the list contract. Empty sequences succeed with no results
unless cancelled. These choices keep identity and admission in one owner without adding public validation
members or a string conversion.

## Open progress counts settle order and total

**Decision (2026-10-13, API review finding F13):** `IndexDriveOpened` keeps both `SettledDriveCount` and
`TotalDriveCount`. The Watch sample now prints one line per settled drive, so the notification has a sample
caller.

**Reason:** the notification was built to order for file-wizard's "drive k of N" display during warm starts
([file-wizard issue 382](https://gitea.fleet.sticktoitive.net/schoen/file-wizard/issues/382)). Drives settle
concurrently, so the settle counter is what lets a handler order overlapping reports, and the total keeps each
report self-describing. A single consumer is the design, not drift. The gap the review found was that no sample
showed it.

**Revisit only if:** file-wizard stops rendering open progress.
