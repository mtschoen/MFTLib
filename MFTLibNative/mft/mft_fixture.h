#pragma once

#include <cstdint>

// Geometry and expected values of the deterministic size and modified-time
// fixture written by GenerateFixtureMFT. Hand-authored rather than rolled from a
// pseudo-random generator, so a test can state every expected value as a literal.
// The record table is documented in docs/superpowers/plans and mirrored by the
// managed tests in MFTLib.Tests/MftFixtureTests.cs.
constexpr uint32_t kFixtureRecordSize = 1024;
constexpr uint64_t kFixtureRecordCount = 12;
constexpr uint64_t kFixtureModifiedBase = 132000000000000000ULL;
constexpr uint64_t kFixtureModifiedStep = 10000000ULL;
