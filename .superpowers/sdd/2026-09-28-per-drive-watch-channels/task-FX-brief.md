### Task FX: flaky concurrent-open cancellation test (controller-created)

FileIndexResilienceTests.OpenAsync_SecondDriveCancelledMidScan_UnwindsTheFirstDrivesAlreadyAddedBlock
failed intermittently (FileNotFoundException on the cache block) after the open became concurrent
(task B9). Reproduce it, find the cause, decide against the specification and ruling B9-Q2 whether
production or the test is wrong, and fix it without weakening what the test pins and without any
sleep. A production fix needs the reproduction as its RED.
