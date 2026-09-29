### Task C4: Port client-side scan tests

**Files:** Create `MFTLib.Tests/BrokerMftBlockProducerTests.cs`, `BrokerMftBlockProducerProtocolTests.cs`, `BrokerBlockContractTests.cs`, `GrowUsnJournalClientTests.cs`, `VolumeQueryClientTests.cs` (singular query), `MftProducerEndToEndTests.cs`, `TestSupport/BrokerBlockTestBase.cs` from their base-commit versions onto `BrokerTestHarness`. `MftProducerEndToEndTests` also re-covers the session-dependent scenarios A3 listed, through `BrokerMftBlockProducer` and `FileIndex.RescanAsync`. `BrokerProcessTests.BlockSections.cs` ports `JournalBrokerClientTests.BlockSectionsAndProgress.cs` (section naming, capacity from volume information, lifetime aliasing), including `DisposeAsync_WithAScanStillInFlight_DisposesTheLeftoverBlockAndLifetime` (`:321`; now `BrokerProcess.DisposeAsync` with an open scan channel releases the unpublished block and its section lifetime), the section-disposal cases at `:236` and `:292`, and the progress cases at `:354` and `:396`; the drive-letter normalization cases at `JournalBrokerClientTests.BlockSectionsAndProgress.cs:472-499` become `BrokerDriveLetterTests.cs`.

- [ ] Port; commit "Port producer, block contract and control-request client tests to BrokerProcess".

**Gate:** green. **Depends on:** C2. **Parallel with:** B5, C5, C7.

