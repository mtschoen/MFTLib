namespace MFTLib.Index;

public sealed partial class FileIndex
{
    void ValidateProducedCacheTag(MftBlockProduceResult result, string blockPath)
    {
        var stored = result.Block.Header.CacheTag;
        if (stored == _options.CacheTag)
        {
            return;
        }

        result.Block.Dispose();
        BlockFile.TryDeleteFailedCreate(blockPath, _options.Diagnostics,
            "the MFT producer's block failed the cache tag consistency check");
        throw new InvalidOperationException(
            $"The MFT producer's block carries cache tag {stored}, but the request requires {_options.CacheTag}.");
    }

    WarmStartResult RejectCacheTag(BlockFile block, string path)
    {
        var stored = block.Header.CacheTag;
        block.Dispose();
        _options.Diagnostics?.Invoke(
            $"Cache tag mismatch for '{path}': stored {stored}; requested {_options.CacheTag}.");
        TryDeleteBestEffort(path, $"cache validation failed: {BlockValidationResult.WrongCacheTag}");
        return new WarmStartResult(null, BlockValidationResult.WrongCacheTag);
    }
}
