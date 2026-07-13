#include "pch.h"

#define AISLOP_TU_FRAGMENT
// NOLINTBEGIN(bugprone-suspicious-include) -- component-as-TU pattern: this owner
// TU is deliberately assembled from .cpp fragments included in dependency order.
// ReSharper disable CppUnusedIncludeDirective -- fragments are compiled here, not headers
#include "mft.ntfs_io.cpp"
#include "mft.records.cpp"
#include "mft.parse_core.cpp"
#include "mft.parse.cpp"
// ReSharper restore CppUnusedIncludeDirective
// NOLINTEND(bugprone-suspicious-include)
#undef AISLOP_TU_FRAGMENT
