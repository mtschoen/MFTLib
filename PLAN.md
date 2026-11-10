# MFTLib PLAN

## Goals

- **Ship MFTLib 0.3 and its consumers** (milestone: 0.3, target: none)
  Disposition: almost there. Immaculate code, an API that survives without
  breaking changes for the foreseeable future, and performance limited only
  by physics: MFTLib exists to be the fastest possible way to enumerate an
  NTFS volume. If the comparative benchmarks do not hold up the way 0.2's did,
  it is not ready, whatever else is done. Dogfood through file-wizard and
  git-wizard; the owner mints the release and nobody proposes publishing.
  Done when: the attended acceptance session passes, benchmarks beat 0.2's
  comparison set, and both consumers have run on the API for a week without
  an API change.
