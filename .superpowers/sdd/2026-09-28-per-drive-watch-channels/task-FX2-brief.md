### Task FX2: flaky recovery-disposal test (controller-created)

FileIndexWatchRecoveryTests.DisposeDuringRecovery_CancelsIt failed once in a full suite run and did
not recur. Reproduce it, find the cause, decide against the specification (disposal cancels and
awaits queued recoveries) whether production or the test is wrong, and fix it without weakening what
the test pins and without any sleep. If it cannot be reproduced, analyse every interleaving that
could fail it and fix any that can be proven.
