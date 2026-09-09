namespace MFTLib;

/// <summary>Transfers completed blocks from scan frames into caller-owned outcomes.</summary>
public sealed partial class JournalBrokerClient
{
    sealed partial class ScanCollector
    {
        public void DisposeBlocks()
        {
            foreach (var outcome in _blockOutcomes.Values)
            {
                outcome.Block.Dispose();
            }

            _blockOutcomes.Clear();
        }

        void CollectBlock(BrokerFrame frame, string drive, string sectionName)
        {
            var block = TakePendingBlock(sectionName);
            if (block == null)
            {
                throw new InvalidOperationException($"No pending block for section {sectionName}.");
            }
            _blockOutcomes.Add(drive, new BlockScanOutcome(sectionName, block, frame.RowCount, frame.NamePoolUsedBytes, frame.SkippedRecordCount));
        }
    }
}
