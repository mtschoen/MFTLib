#pragma once

#include <cstdint>

// Geometry and expected values of the deterministic metadata and freed-record
// fixture written by GenerateFixtureMFT. Hand-authored rather than rolled from a
// pseudo-random generator, so a test can state every expected value as a literal.
// The record table is mirrored by the managed MftFixtureTests and IncludeFreedTests.
constexpr uint32_t kFixtureRecordSize = 1024;
constexpr uint64_t kFixtureRecordCount = 24;
constexpr uint64_t kFixtureModifiedBase = 132000000000000000ULL;
constexpr uint64_t kFixtureModifiedStep = 10000000ULL;
