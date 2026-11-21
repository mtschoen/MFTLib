// Reads the resident $DATA of specific MFT records. A reader knows where the $MFT's bytes live
// (segments) and how to read them; the volume, image and dump inputs differ only in that. Every
// record read is untrusted input: nothing in it is believed before it is checked.
#include "pch.h"

#include <algorithm>
// ReSharper disable once CppUnusedIncludeDirective
// aislop-ignore-next-line CppUnusedIncludeDirective -- memcpy below needs this on GCC/Clang
#include <cstring>
#include <limits>
#include <memory>
#include <vector>

#include "../framework.h"
#include "../ntfs.h"
#include "../mft_api.h"
#include "../internal.h"
#include "../core/exception_barrier.h"
#include "mft.internal.h"
#include "mft.inline_content.h"

using namespace mftlib::ntfs::detail;
using namespace mftlib::inline_content;

namespace {

constexpr uint32_t kReparsePointType = 0xC0;
// FILE_ATTRIBUTE_REPARSE_POINT, in the file attributes word of $STANDARD_INFORMATION (value offset 32).
constexpr uint32_t kReparsePointFileAttribute = 0x0400;
constexpr size_t kStandardInformationAttributesEnd = 36;
// The size of a nonresident attribute header up to and including its mapping pairs offset field.
constexpr uint32_t kNonresidentAttributeHeaderSize = 0x40;

// What one record held, judged once however many requests name it. verdict is what the record is
// when it is in use and carries the sequence number the request expects.
struct RecordEvaluation {
    bool readable = false;  // FILE magic and a valid update sequence array
    bool inUse = false;
    uint16_t sequenceNumber = 0;
    MftInlineOutcome verdict = MftInlineUnreadable;
    const uint8_t* content = nullptr;
    uint32_t contentLength = 0;
};

// The unnamed $DATA of a base file record as a verdict: resident, plain and inside its attribute is
// Returned; real but not inline is NotInline; a value the attribute cannot hold is Unreadable.
void ClassifyData(const uint8_t* record, uint32_t recordSize, const ATTRIBUTE_RECORD_HEADER* data,
                  RecordEvaluation* evaluation) {
    if (data->FormCode > 1) {
        return;  // not a form NTFS writes: stays Unreadable
    }
    if (data->FormCode == 1 && data->RecordLength < kNonresidentAttributeHeaderSize) {
        return;  // a nonresident header that does not fit its attribute: stays Unreadable
    }
    if (data->FormCode == 1 || data->Flags != 0) {
        evaluation->verdict = MftInlineNotInline;
        return;
    }
    const size_t valueOffset = data->Form.Resident.ValueOffset;
    const size_t valueLength = data->Form.Resident.ValueLength;
    const auto attributeStart = static_cast<size_t>(reinterpret_cast<const uint8_t*>(data) - record);
    if (valueOffset < kResidentAttributeHeaderSize || valueOffset > data->RecordLength ||
        valueLength > data->RecordLength - valueOffset || attributeStart + valueOffset + valueLength > recordSize) {
        return;
    }
    evaluation->verdict = MftInlineReturned;
    evaluation->content = record + attributeStart + valueOffset;
    evaluation->contentLength = static_cast<uint32_t>(valueLength);
}

// Whether a resident $STANDARD_INFORMATION marks the file a reparse point. False with *readable cleared
// when the attribute is nonresident or its value does not hold the file attributes word.
bool IsReparsePointInStandardInformation(const uint8_t* record, uint32_t recordSize,
                                         const ATTRIBUTE_RECORD_HEADER* information, bool* readable) {
    const size_t valueOffset = information->Form.Resident.ValueOffset;
    const size_t valueLength = information->Form.Resident.ValueLength;
    const auto attributeStart = static_cast<size_t>(reinterpret_cast<const uint8_t*>(information) - record);
    if (information->FormCode != 0 || valueOffset < kResidentAttributeHeaderSize ||
        valueOffset > information->RecordLength || valueLength > information->RecordLength - valueOffset ||
        valueLength < kStandardInformationAttributesEnd || attributeStart + valueOffset + valueLength > recordSize) {
        *readable = false;
        return false;
    }
    uint32_t fileAttributes = 0;
    memcpy(&fileAttributes, record + attributeStart + valueOffset + 32, sizeof(fileAttributes));
    return (fileAttributes & kReparsePointFileAttribute) != 0;
}

// What the attribute chain of an in-use record holds, as far as the inline verdict needs.
struct AttributeSurvey {
    const ATTRIBUTE_RECORD_HEADER* data = nullptr;  // the first unnamed $DATA
    size_t unnamedDataCount = 0;
    bool attributeList = false;
    bool reparsePoint = false;
    bool readable = true;
};

// The whole attribute chain is walked, because a reparse point follows the $DATA it governs and a
// malformed attribute anywhere makes the record unreadable. Readable is false when it did.
AttributeSurvey SurveyAttributes(uint8_t* record, uint32_t recordSize) {
    const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record);
    size_t offset = header->FirstAttributeOffset;
    PATTRIBUTE_RECORD_HEADER attribute = nullptr;
    AttributeSurvey survey;
    AttributeStep step = NextAttribute(record, recordSize, &offset, &attribute);
    while (step == AttributeStep::Attribute) {
        if (attribute->TypeCode == kReparsePointType) {
            survey.reparsePoint = true;
        } else if (attribute->TypeCode == AttributeList) {
            survey.attributeList = true;
        } else if (attribute->TypeCode == StandardInformation) {
            survey.reparsePoint =
                IsReparsePointInStandardInformation(record, recordSize, attribute, &survey.readable) ||
                survey.reparsePoint;
        } else if (attribute->TypeCode == Data && attribute->NameLength == 0) {
            survey.unnamedDataCount++;
            if (survey.data == nullptr) {
                survey.data = attribute;
            }
        }
        step = NextAttribute(record, recordSize, &offset, &attribute);
    }
    survey.readable = survey.readable && step != AttributeStep::Malformed;
    return survey;
}

// The verdict of an in-use record. Data described through an attribute list, a second unnamed $DATA
// and a reparse point (in its own attribute or in the file attributes) are not inline: the base
// record alone does not say what the file's bytes are.
void ClassifyInUseRecord(uint8_t* record, uint32_t recordSize, RecordEvaluation* evaluation) {
    const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record);
    const auto& base = header->BaseFileRecordSegment;
    const AttributeSurvey survey = SurveyAttributes(record, recordSize);
    if (!survey.readable) {
        return;
    }
    const bool isBaseRecord =
        base.SegmentNumberLowPart == 0 && base.SegmentNumberHighPart == 0 && base.SequenceNumber == 0;
    if (!isBaseRecord || (header->Flags & kRecordDirectory) != 0 || survey.reparsePoint || survey.attributeList ||
        survey.unnamedDataCount != 1) {
        evaluation->verdict = MftInlineNotInline;
        return;
    }
    ClassifyData(record, recordSize, survey.data, evaluation);
}

bool IsAllZero(const uint8_t* bytes, uint32_t length) {
    return std::all_of(bytes, bytes + length, [](uint8_t value) { return value == 0; });
}

// Judges the record at record[0, recordSize). Fixes the record up in place, so it is judged once.
RecordEvaluation EvaluateRecord(uint8_t* record, uint32_t recordSize) {
    RecordEvaluation evaluation;
    const auto* header = reinterpret_cast<const FILE_RECORD_SEGMENT_HEADER*>(record);
    if (IsAllZero(record, recordSize)) {
        // A slot NTFS never wrote is zero-filled throughout: free, not damaged.
        evaluation.readable = true;
        return evaluation;
    }
    if (header->MultiSectorHeader.Magic != kFileRecordMagic || !ApplyFixup(record, recordSize)) {
        return evaluation;
    }
    evaluation.readable = true;
    evaluation.sequenceNumber = header->SequenceNumber;
    evaluation.inUse = (header->Flags & kRecordInUse) != 0;
    const uint32_t usedLength = header->Reserved3[0];
    if (evaluation.inUse) {
        if (usedLength > recordSize) {
            evaluation.readable = false;
        } else {
            ClassifyInUseRecord(record, usedLength, &evaluation);
        }
    }
    return evaluation;
}

// The outcome of one request given what its record held.
MftInlineOutcome OutcomeFor(const RecordEvaluation& evaluation, uint32_t expectedSequenceNumber) {
    if (!evaluation.readable) {
        return MftInlineUnreadable;
    }
    if (!evaluation.inUse || static_cast<uint32_t>(evaluation.sequenceNumber) != expectedSequenceNumber) {
        return MftInlineSuperseded;
    }
    return evaluation.verdict;
}

// The requests [first, end) that one read serves, and the bytes it covers: records in one segment
// whose gaps stay within kCoalesceGapBytes and whose span stays within kMaximumReadBytes.
struct Group {
    uint64_t first = 0;
    uint64_t end = 0;
    uint64_t firstByte = 0;
    uint64_t lastByte = 0;  // exclusive
    const Segment* segment = nullptr;
};

// One ReadMftInlineContent call in progress.
struct Call {
    MftInlineReader* reader = nullptr;
    MftInlineRequest* requests = nullptr;
    uint64_t requestCount = 0;
    uint8_t* buffer = nullptr;
    uint64_t bufferLength = 0;
    const MftParseControl* control = nullptr;
    MftInlineReadResult* result = nullptr;
    uint64_t next = 0;
    uint64_t bytesUsed = 0;

    uint64_t RecordSize() const { return reader->geometry.recordSize; }
    uint64_t BufferRemaining() const { return bufferLength - bytesUsed; }
};

// The segment holding bytes [offset, offset + length), or null when they straddle segments or lie
// outside them. The segments ascend and are gap free, so this is a binary search for the last one
// starting at or before offset: every call reads a single group, and a scan from the first run on
// each call would cost every earlier run again.
const Segment* FindSegment(const Call* call, uint64_t offset, uint64_t length) {
    const auto& segments = call->reader->segments;
    uint64_t probes = 0;  // counted locally; only an armed test hook publishes it
    const auto after =
        std::upper_bound(segments.begin(), segments.end(), offset, [&probes](uint64_t value, const Segment& segment) {
            probes++;
            return value < segment.mftOffset;
        });
    NoteInlineSegmentProbes(probes);
    if (after == segments.begin()) {
        return nullptr;
    }
    const Segment& segment = *(after - 1);
    return offset + length <= segment.mftOffset + segment.byteCount ? &segment : nullptr;
}

bool IsCancelRequested(const MftParseControl* control) {
    return ShouldForceCancel() || (control != nullptr && LoadSharedInt32(&control->cancelRequested) != 0);
}

// The group that starts at the call's next request, which is below the end of the MFT.
Group BuildGroup(const Call* call) {
    const uint64_t recordSize = call->RecordSize();
    Group group;
    group.first = call->next;
    group.end = call->next + 1;
    group.firstByte = call->requests[group.first].recordNumber * recordSize;
    group.lastByte = group.firstByte + recordSize;
    group.segment = FindSegment(call, group.firstByte, recordSize);
    if (group.segment == nullptr) {
        return group;
    }
    const uint64_t segmentEnd = group.segment->mftOffset + group.segment->byteCount;
    while (group.end < call->requestCount) {
        const uint64_t number = call->requests[group.end].recordNumber;
        // A descending record ends the group, so every record answered from the scratch bytes lies inside them
        // even when a resumed call's requests were reordered after its first call checked them.
        if (number >= call->reader->totalRecords || number < call->requests[group.end - 1].recordNumber) {
            break;
        }
        const uint64_t start = number * recordSize;
        const uint64_t end = start + recordSize;
        const uint64_t gap = start > group.lastByte ? start - group.lastByte : 0;
        if (end > segmentEnd || gap > kCoalesceGapBytes || end - group.firstByte > kMaximumReadBytes) {
            break;
        }
        group.lastByte = (std::max)(group.lastByte, end);
        group.end++;
    }
    return group;
}

// Reads MFT bytes [firstByte, firstByte + byteCount) of the group's segment into the scratch buffer at their place in
// the group, and returns how many valid bytes arrived.
uint64_t ReadGroupBytes(const Call* call, const Group& group, uint64_t firstByte, uint64_t byteCount) {
    MftInlineReader& reader = *call->reader;
    const uint64_t arrived =
        reader.read(reader, group.segment->sourceOffset + (firstByte - group.segment->mftOffset),
                    reader.scratch.data() + (firstByte - group.firstByte), static_cast<size_t>(byteCount));
    call->result->readCalls++;
    call->result->bytesRead += arrived;
    return arrived;
}

// Reads the group's bytes into the reader's scratch buffer as one read and returns the MFT offset where its valid bytes
// end: group.lastByte for a complete read, less for a short or failed one.
uint64_t ReadGroup(const Call* call, const Group& group) {
    call->reader->scratch.Reserve(static_cast<size_t>(group.lastByte - group.firstByte));
    return group.firstByte + ReadGroupBytes(call, group, group.firstByte, group.lastByte - group.firstByte);
}

// Whether the bytes of one record are in the scratch buffer. A record the group read covered is. One it did not is read
// again on its own, a bounded read observed by cancellation, unless the group was that one record, whose own read
// has then already failed. *cancelled is set, and false returned, when cancellation stops the retry.
bool HaveRecordBytes(const Call* call, const Group& group, uint64_t validEnd, uint64_t recordStart, bool* cancelled) {
    const uint64_t recordSize = call->RecordSize();
    if (recordStart + recordSize <= validEnd) {
        return true;
    }
    if (group.lastByte - group.firstByte == recordSize) {
        return false;
    }
    if (IsCancelRequested(call->control)) {
        *cancelled = true;
        return false;
    }
    return ReadGroupBytes(call, group, recordStart, recordSize) == recordSize;
}

// Answers the group's requests from its scratch bytes, whose valid bytes end at validEnd. Stops, leaving call->next at
// the first request it did not answer, when the next content would not fit the remaining buffer or cancellation stops
// a retry. Each record is judged on its own bytes alone, so its answer never depends on which records share its group.
void AnswerGroup(Call* call, const Group& group, uint64_t validEnd) {
    uint64_t evaluatedNumber = (std::numeric_limits<uint64_t>::max)();
    RecordEvaluation evaluation;
    for (uint64_t index = group.first; index < group.end; index++) {
        MftInlineRequest& request = call->requests[index];
        if (request.recordNumber != evaluatedNumber) {
            const uint64_t recordStart = request.recordNumber * call->RecordSize();
            bool cancelled = false;
            if (HaveRecordBytes(call, group, validEnd, recordStart, &cancelled)) {
                evaluation = EvaluateRecord(call->reader->scratch.data() + (recordStart - group.firstByte),
                                            call->reader->geometry.recordSize);
            } else if (cancelled) {
                call->result->cancelled = 1;
                return;
            } else {
                evaluation = RecordEvaluation{};  // its own read failed or came up short: Unreadable
            }
            evaluatedNumber = request.recordNumber;
        }
        const MftInlineOutcome outcome = OutcomeFor(evaluation, request.expectedSequenceNumber);
        if (outcome == MftInlineReturned) {
            if (evaluation.contentLength > call->BufferRemaining()) {
                return;
            }
            request.outcome = outcome;
            request.contentOffset = call->bytesUsed;
            request.contentLength = evaluation.contentLength;
            if (evaluation.contentLength != 0) {
                memcpy(call->buffer + call->bytesUsed, evaluation.content, evaluation.contentLength);
            }
            call->bytesUsed += evaluation.contentLength;
        } else {
            request.outcome = outcome;
        }
        call->next = index + 1;
    }
}

// Starts a request's answer as Unreadable with no content, so a request the call never reaches reads as unanswered.
void ResetAnswer(MftInlineRequest* request) {
    request->outcome = MftInlineUnreadable;
    request->contentOffset = 0;
    request->contentLength = 0;
}

bool IsAscending(const MftInlineRequest* requests, uint64_t count) {
    for (uint64_t index = 1; index < count; index++) {
        if (requests[index].recordNumber < requests[index - 1].recordNumber) {
            return false;
        }
    }
    return true;
}

// Answers requests from startIndex until one group has been read, so a caller sees the answers of every read as soon
// as it completes. Requests that need no read (past the end of the MFT, or straddling two runs) are answered on the
// way, without I/O: a call makes one run lookup per distinct record it meets before its one group read, and a request
// for the record just found straddling is answered without searching again. A short or failed group read adds one
// bounded read per record it did not deliver, inside the same call. The first call of a request array (startIndex 0)
// checks the order of the whole array once.
void ReadRequests(Call* call, uint64_t startIndex) {
    MftInlineReadResult* result = call->result;
    if (startIndex == 0 && !IsAscending(call->requests, call->requestCount)) {
        result->failed = 1;
        SetErrorMessage(result->errorMessage, L"The requests are not in ascending record order");
        return;
    }
    uint64_t straddlingRecord = (std::numeric_limits<uint64_t>::max)();
    while (call->next < call->requestCount) {
        if (IsCancelRequested(call->control)) {
            result->cancelled = 1;
            break;
        }
        MftInlineRequest& request = call->requests[call->next];
        ResetAnswer(&request);
        if (request.recordNumber >= call->reader->totalRecords) {
            request.outcome = MftInlineSuperseded;
            call->next++;
            continue;
        }
        if (request.recordNumber == straddlingRecord) {
            call->next++;  // stays Unreadable, as its first request did
            continue;
        }
        const Group group = BuildGroup(call);
        if (group.segment == nullptr) {
            // A record straddling two runs or outside every run cannot be read as one piece.
            straddlingRecord = request.recordNumber;
            call->next++;
            continue;
        }
        for (uint64_t index = group.first + 1; index < group.end; index++) {
            ResetAnswer(&call->requests[index]);
        }
        AnswerGroup(call, group, ReadGroup(call, group));
        break;
    }
    result->nextRequest = call->next;
    result->bytesUsed = call->bytesUsed;
}

}  // namespace

extern "C" {
// Answers the requests of one call from its startIndex on, in ascending record order, filling each
// request's outcome and placing each returned content in the call's buffer. The buffer is at least
// MFT_INLINE_MINIMUM_BUFFER_BYTES, so every call answers at least one request unless it is cancelled or fails.
// Neighbouring wanted records are read together as one group, and a call makes at most one group read
// (plus a single-record reread of each record a short or failed group read did not deliver) before it
// returns, so the caller can report each completed call. result->nextRequest tells where the
// next call starts; a call that reaches requestCount is the last.
EXPORT void ReadMftInlineContent(MftInlineReader* reader, const MftInlineReadCall* request,
                                 MftInlineReadResult* result) {
    if (result == nullptr) {
        return;
    }
    *result = MftInlineReadResult{};
    if (reader == nullptr || request == nullptr || request->requests == nullptr || request->buffer == nullptr ||
        request->startIndex > request->requestCount || request->bufferLength < MFT_INLINE_MINIMUM_BUFFER_BYTES) {
        result->failed = 1;
        SetErrorMessage(result->errorMessage, L"The inline content read arguments are invalid");
        return;
    }
    result->nextRequest = request->startIndex;
    NativeBoundary(
        [&] {
            Call call;
            call.reader = reader;
            call.requests = request->requests;
            call.requestCount = request->requestCount;
            call.buffer = request->buffer;
            call.bufferLength = request->bufferLength;
            call.control = request->control;
            call.result = result;
            call.next = request->startIndex;
            ReadRequests(&call, request->startIndex);
            return 0;
        },
        [&](const wchar_t* message) {
            result->failed = 1;
            SetErrorMessage(result->errorMessage, message);
            return 0;
        });
}

// Frees a reader. Safe with null. Never closes the handle or dump input the reader borrowed.
EXPORT void CloseMftInlineReader(MftInlineReader* reader) { const std::unique_ptr<MftInlineReader> owned(reader); }
}
