You are an audit lane on a C# repository (MFTLib). You change no code. Work ONLY inside
C:\Users\mtsch\MFTLib-worktrees\impl-265 and write exactly one file:
C:\Users\mtsch\MFTLib-worktrees\impl-265\.superpowers\sdd\2026-09-28-per-drive-watch-channels\tranche-B-audit.md
Do not modify, stage or commit anything else; read-only git commands are allowed. Do not acquire
or release any lock. Do not run tests or builds.

Context: branch impl/265-per-drive-channels rewrote the broker and index watch layers. Many test
files were deleted and later ported by other tasks. The rule: every base-commit test method in a
file that a task deleted either has a counterpart at HEAD or is named in a porting commit message
as dropped, with its reason. Base commit: 3597586 (main). Head: the worktree HEAD.

Task (mechanical; show your method):
1. For every *.cs file under MFTLib.Tests and MFTLibTestExtensions at 3597586, list the
   [TestMethod] (and [DataTestMethod]) method names. Do the same at HEAD. Note [DataRow]s where a
   method has them.
2. For every method name present at 3597586 but absent everywhere at HEAD, search the commit
   messages of 3597586..HEAD (git log --format=%H%n%B) for that exact name. Classify each:
   (a) named as dropped with a reason (quote the commit short SHA and the reason line);
   (b) named as renamed, with the new name, and the new name exists at HEAD;
   (c) NOT ACCOUNTED FOR.
3. Split the results into Tranche B (files whose name starts with Broker, JournalBroker,
   VolumeQuery, GrowUsnJournal, MftProducer, DefaultElevated, ElevatedEntry, or lives in
   TestSupport and serves the broker) and Tranche I (Index/ and everything else).
4. For every (c) item, give the base file and a one-line description of what the method tested
   (read it with git show 3597586:<path>), and judge whether the behavior it pinned still exists at
   HEAD (name the production symbol you checked).

Output file format (plain ASCII punctuation only, no em-dashes):
### Summary
counts per tranche: base methods in affected files, ported same name, renamed, dropped with reason,
not accounted for.
### Not accounted for (Tranche B)
table: base file | method | what it tested | behavior still exists? (symbol) 
### Not accounted for (Tranche I)
same table.
### Dropped with reason
table: method | commit | reason.
### Method
the commands you ran.

Your final message: the Summary counts and the output file path.
