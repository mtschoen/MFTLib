// Included inside the parse-core anonymous namespace; do not compile directly.
#ifndef AISLOP_TU_FRAGMENT
    #error "mft.paths.cpp is a fragment included by mft.parse_core.cpp"
#endif

void PopulatePathSlice(SliceRange range, const CompactOutput& source, const PathLookup& lookup, uint64_t totalRecords,
                       SliceResult& slice, ResolveProgressState* progressState) {
    std::vector<uint16_t> pathBuffer;
    slice.entries.reserve(range.end - range.start);
    constexpr uint64_t kReportInterval = 4096;
    uint64_t localCount = 0;

    for (uint64_t i = range.start; i < range.end; i++) {
        if ((i - range.start) % kCancelCheckRecords == 0 && IsCancelRequested(progressState->control)) {
            return;
        }
        const auto& src = source.entries[i];
        ParsedEntry entry{};
        entry.recordNumber = src.recordNumber;
        entry.parentRecordNumber = src.parentRecordNumber;
        entry.flags = src.flags;
        entry.fileAttributes = src.fileAttributes;
        entry.sequenceNumber = src.sequenceNumber;
        entry.size = src.size;
        entry.modifiedTime = src.modifiedTime;
        if (ResolvePath(src.recordNumber, lookup, totalRecords, pathBuffer)) {
            entry.name = reinterpret_cast<const WCHAR*>(pathBuffer.data());
            entry.nameLength = static_cast<uint16_t>(pathBuffer.size());
        } else if ((src.flags & kRecordInUse) == 0) {
            entry.flags |= MFT_ENTRY_FLAG_PATH_UNRESOLVED;
            entry.name = reinterpret_cast<const WCHAR*>(source.strings + src.stringOffset);
            entry.nameLength = src.stringLength;
        } else {
            entry.name = nullptr;
            entry.nameLength = 0;
        }
        slice.append(entry);

        if (++localCount >= kReportInterval) {
            progressState->reportBatch(localCount);
            localCount = 0;
        }
    }

    if (localCount > 0) {
        progressState->reportBatch(localCount);
    }
}

// Resolve a full path for every parsed entry through scan.lookup (non-null), fanning out across
// numThreads workers. Returns false when cancellation stopped it; nothing is published then.
bool ResolveAllPaths(const ScanContext& scan, unsigned numThreads, ParseState& state, MftParseResult* result,
                     const ProgressHook& progress) {
    const PathLookup& lookup = *scan.lookup;
    const uint64_t totalRecords = scan.totalRecords;
    uint64_t usedCount = state.output.entryCount;
    CompactOutput paths;
    paths.entryCapacity = usedCount;
    paths.entries =
        ShouldFailAlloc()
            ? nullptr
            : static_cast<MftCompactEntry*>(malloc(static_cast<size_t>(usedCount) * sizeof(MftCompactEntry)));
    if (paths.entries == nullptr) {
        return true;
    }

    paths.stringCapacity = std::max<uint64_t>(state.output.stringUnits, 1024);
    paths.strings = ShouldFailAlloc()
                        ? nullptr
                        : static_cast<uint16_t*>(malloc(static_cast<size_t>(paths.stringCapacity) * sizeof(uint16_t)));
    if (paths.strings == nullptr) {
        free(paths.entries);
        return true;
    }

    ResolveProgressState progressState;
    progressState.callback = progress.callback;
    progressState.context = progress.context;
    progressState.control = scan.control;
    progressState.wallStart = progress.wallStart;
    progressState.totalEntries = usedCount;

    std::vector<SliceResult> pathSlices(numThreads);
    ForEachRange(usedCount, numThreads, [&](unsigned index, SliceRange range) {
        PopulatePathSlice(range, state.output, lookup, totalRecords, pathSlices[index], &progressState);
    });

    if (IsCancelRequested(scan.control)) {
        free(paths.entries);
        free(paths.strings);
        return false;
    }

    bool appendOk = true;
    for (unsigned ti = 0; ti < numThreads; ti++) {
        if (!AppendSlice(paths, pathSlices[ti], nullptr)) {
            appendOk = false;
            break;
        }
    }

    if (!appendOk) {
        free(paths.entries);
        free(paths.strings);
        return true;
    }

    // Atomic publication on success
    result->pathEntries = paths.entries;
    result->pathStrings = paths.strings;
    result->pathStringUnits = paths.stringUnits;
    free(state.output.entries);
    free(state.output.strings);
    state.output.entries = nullptr;
    state.output.strings = nullptr;
    progressState.reportFinal();
    state.output.entryCount = 0;
    state.output.stringUnits = 0;
    state.output.entryCapacity = 0;
    state.output.stringCapacity = 0;
    return true;
}
