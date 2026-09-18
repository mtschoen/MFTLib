#!/usr/bin/env bash
# Tests for scripts/sync_consumers.sh. Runs entirely against local fixture
# repositories built with git init, with every Gitea transport call stubbed, so
# it needs no network and no token. Run: bash scripts/sync_consumers.tests.sh
#
# The fixtures mirror the fleet layout that matters: the consumer sits beside
# MFTLib under one owner directory and pins it with the relative submodule url
# ../MFTLib.git, exactly as schoen/file-wizard and schoen/git-wizard do. That
# makes the relative-url resolution under test the real production shape rather
# than a stand-in for it.

set -u
set -o pipefail

SCRIPT_DIRECTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT_PATH="$SCRIPT_DIRECTORY/sync_consumers.sh"

FIXTURE_ROOT="$(mktemp -d)"
FIXTURE_GITEA="$FIXTURE_ROOT/gitea"
FIXTURE_WORK="$FIXTURE_ROOT/work"
PR_LOG="$FIXTURE_ROOT/pull_requests.log"
RUN_STDOUT="$FIXTURE_ROOT/stdout.txt"
RUN_STDERR="$FIXTURE_ROOT/stderr.txt"
RUN_STATUS=0

TESTS_RUN=0
FAILURES=0
CURRENT_TEST=""

cleanup() { rm -rf "$FIXTURE_ROOT"; }
trap cleanup EXIT

start_test() {
	CURRENT_TEST="$1"
	TESTS_RUN=$((TESTS_RUN + 1))
	printf '%s\n' "--- $1"
}

fail_assertion() {
	FAILURES=$((FAILURES + 1))
	printf 'FAIL [%s] %s\n' "$CURRENT_TEST" "$1" >&2
}

assert_equals() {
	# assert_equals <expected> <actual> <label>
	if [ "$1" != "$2" ]; then
		fail_assertion "$3: expected '$1', got '$2'"
	else
		printf 'ok   %s\n' "$3"
	fi
}

assert_contains() {
	# assert_contains <haystack> <needle> <label>
	case "$1" in
	*"$2"*) printf 'ok   %s\n' "$3" ;;
	*) fail_assertion "$3: '$2' not found in output" ;;
	esac
}

# --- fixtures ---------------------------------------------------------------

seed_bare_repo() {
	# seed_bare_repo <name>: create a bare repo with one commit on main.
	local name="$1"
	local bare="$FIXTURE_GITEA/schoen/$name.git"
	local work="$FIXTURE_WORK/$name-seed"
	mkdir -p "$FIXTURE_GITEA/schoen"
	git init -q --bare --initial-branch=main "$bare"
	git clone -q "$bare" "$work" 2>/dev/null
	(
		cd "$work" || exit 1
		git config user.name fixture
		git config user.email fixture@example.invalid
		echo seeded >README.md
		git add README.md
		git commit -qm "seed $name"
		git push -q origin HEAD:refs/heads/main
	)
}

append_mftlib_commit() {
	# append_mftlib_commit <text>: add a commit to the MFTLib fixture, echo its sha.
	local work="$FIXTURE_WORK/mftlib-work"
	if [ ! -d "$work" ]; then
		git clone -q "$FIXTURE_GITEA/schoen/MFTLib.git" "$work"
	fi
	(
		cd "$work" || exit 1
		git config user.name fixture
		git config user.email fixture@example.invalid
		printf '%s\n' "$1" >>history.txt
		git add history.txt
		git commit -qm "change: $1"
		git push -q origin HEAD:refs/heads/main
		git rev-parse HEAD
	)
}

make_consumer() {
	# make_consumer <name> <gitlink_sha> <submodule_url>
	local name="$1" gitlink_sha="$2" submodule_url="$3"
	local work="$FIXTURE_WORK/$name-work"
	seed_bare_repo "$name"
	git clone -q "$FIXTURE_GITEA/schoen/$name.git" "$work" 2>/dev/null
	(
		cd "$work" || exit 1
		git config user.name fixture
		git config user.email fixture@example.invalid
		printf '[submodule "external/MFTLib"]\n\tpath = external/MFTLib\n\turl = %s\n' \
			"$submodule_url" >.gitmodules
		mkdir -p external
		git add .gitmodules
		git update-index --add --cacheinfo "160000,$gitlink_sha,external/MFTLib"
		git commit -qm "pin MFTLib at ${gitlink_sha:0:12}"
		git push -q origin HEAD:refs/heads/main
	)
}

make_plain_repo() {
	# make_plain_repo <name> [pin_sha]: a repo with no submodule. With pin_sha it
	# also carries a legacy .mftlib/pin file, the shape issue #194 reported.
	local name="$1" pin_sha="${2:-}"
	local work="$FIXTURE_WORK/$name-work"
	seed_bare_repo "$name"
	git clone -q "$FIXTURE_GITEA/schoen/$name.git" "$work" 2>/dev/null
	(
		cd "$work" || exit 1
		git config user.name fixture
		git config user.email fixture@example.invalid
		if [ -n "$pin_sha" ]; then
			mkdir -p .mftlib
			printf '%s\n' "$pin_sha" >.mftlib/pin
			git add .mftlib/pin
			git commit -qm "legacy pin file only"
			git push -q origin HEAD:refs/heads/main
		fi
	)
}

gitlink_of() {
	# gitlink_of <name> <branch>: echo the sha the external/MFTLib gitlink records.
	git --git-dir="$FIXTURE_GITEA/schoen/$1.git" ls-tree "$2" -- external/MFTLib | awk '{print $3}'
}

subject_of() {
	# subject_of <name> <branch>: echo the branch tip's commit subject.
	git --git-dir="$FIXTURE_GITEA/schoen/$1.git" log -1 --format=%s "$2"
}

# --- setup ------------------------------------------------------------------

mkdir -p "$FIXTURE_GITEA/schoen" "$FIXTURE_WORK"
seed_bare_repo MFTLib
OLD_SHA="$(append_mftlib_commit first)"
NEW_SHA="$(append_mftlib_commit second)"

# shellcheck source=scripts/sync_consumers.sh
source "$SCRIPT_PATH"

GITEA_URL="$FIXTURE_GITEA"
GITEA_OWNER="schoen"
SELF_REPO="MFTLib"
export GITEA_TOKEN="fixture-token"
SELF_REPO_URL="$GITEA_URL/$GITEA_OWNER/$SELF_REPO"
# mktemp paths can contain uppercase, and normalize_repo_url lowercases, so
# expected values have to be lowercased the same way.
FIXTURE_GITEA_LOWER="$(printf '%s' "$FIXTURE_GITEA" | tr '[:upper:]' '[:lower:]')"

# --- transport stubs --------------------------------------------------------
# These must be defined after the source above: sourcing runs the script's own
# definitions, and the last definition of a bash function is the one that runs.
# Only the Gitea transport is replaced. Detection, the gitlink bump, the clone,
# the commit and the push all execute for real against the local fixtures.

resolve_owner_uid() { printf '1'; }

authed_url() { printf '%s' "$1"; }

list_consumer_candidates() {
	local name
	for name in $FIXTURE_REPOS; do
		printf '%s\t%s\t%s\n' "$name" "main" "$FIXTURE_GITEA/schoen/$name.git"
	done
}

api_read_file() {
	local repo="$1" branch="$2" path="$3"
	local output
	if ! output="$(git --git-dir="$FIXTURE_GITEA/schoen/$repo.git" show "$branch:$path" 2>/dev/null)"; then
		return 2
	fi
	printf '%s' "$output"
}

api_read_gitlink_sha() {
	local repo="$1" branch="$2" path="$3"
	local entry entry_type sha
	entry="$(git --git-dir="$FIXTURE_GITEA/schoen/$repo.git" ls-tree "$branch" -- "$path" 2>/dev/null)"
	[ -n "$entry" ] || return 2
	read -r _ entry_type sha _ <<<"$entry"
	[ "$entry_type" = "commit" ] || return 1
	printf '%s' "$sha"
}

open_pull_request() {
	local repo="$1" base_branch="$2" paths="$3"
	printf '%s|%s|%s|%s\n' "$repo" "$base_branch" "$BRANCH" "${paths//$'\n'/,}" >>"$PR_LOG"
	record "$repo" "updated" "PR opened (fixture)"
	return 0
}

run_sync() {
	# run_sync <new_sha>: run main in a subshell, capturing streams and status.
	: >"$PR_LOG"
	( main "$1" ) >"$RUN_STDOUT" 2>"$RUN_STDERR"
	RUN_STATUS=$?
}

# --- unit tests: pure helpers ----------------------------------------------

start_test "normalize_repo_url drops scheme, userinfo, .git and case"
assert_equals "gitea.example.invalid/schoen/mftlib" \
	"$(normalize_repo_url "https://claude-code:secret@Gitea.Example.invalid/schoen/MFTLib.git")" \
	"normalizes an authenticated https url"
assert_equals "$FIXTURE_GITEA_LOWER/schoen/mftlib" \
	"$(normalize_repo_url "$FIXTURE_GITEA/schoen/MFTLib.git")" \
	"normalizes a local path url"

start_test "resolve_submodule_url matches git's own relative-url rule"
SUPER="https://gitea.example.invalid/schoen/file-wizard.git"
assert_equals "https://gitea.example.invalid/schoen/MFTLib.git" \
	"$(resolve_submodule_url "$SUPER" "../MFTLib.git")" "one level up stays inside the owner"
assert_equals "https://gitea.example.invalid/MFTLib.git" \
	"$(resolve_submodule_url "$SUPER" "../../MFTLib.git")" "two levels up leaves the owner"
assert_equals "https://gitea.example.invalid/schoen/file-wizard.git/MFTLib.git" \
	"$(resolve_submodule_url "$SUPER" "./MFTLib.git")" "dot-slash appends to the repo url"
assert_equals "MFTLib.git" \
	"$(resolve_submodule_url "$SUPER" "MFTLib.git")" "a bare name stays unresolved"
assert_equals "https://elsewhere.invalid/o/MFTLib.git" \
	"$(resolve_submodule_url "$SUPER" "https://elsewhere.invalid/o/MFTLib.git")" "an absolute url passes through"

start_test "parse_gitmodules reads path and url from every submodule section"
PARSED="$(printf '%s\n' \
	'[submodule "external/MFTLib"]' \
	'	path = external/MFTLib' \
	'	url = ../MFTLib.git' \
	'# a comment line' \
	'[submodule "vendor/other"]' \
	'	url = https://elsewhere.invalid/o/other.git' \
	'	path = "vendor/other"' | parse_gitmodules)"
assert_equals "$(printf 'external/MFTLib\t../MFTLib.git\nvendor/other\thttps://elsewhere.invalid/o/other.git')" \
	"$PARSED" "parses both sections, either key order, quotes and comments"

start_test "mftlib_submodule_paths matches only a submodule that resolves here"
CONSUMER_URL="$FIXTURE_GITEA/schoen/file-wizard.git"
gitmodules_for() { printf '[submodule "external/MFTLib"]\n\tpath = external/MFTLib\n\turl = %s\n' "$1"; }
assert_equals "external/MFTLib" \
	"$(mftlib_submodule_paths "$SELF_REPO_URL" "$CONSUMER_URL" "$(gitmodules_for "../MFTLib.git")")" \
	"matches the real consumer shape"
assert_equals "" \
	"$(mftlib_submodule_paths "$SELF_REPO_URL" "$CONSUMER_URL" "$(printf '[submodule "vendor/other"]\n\tpath = vendor/other\n\turl = ../other.git\n')")" \
	"ignores an unrelated submodule"
assert_equals "" \
	"$(mftlib_submodule_paths "$SELF_REPO_URL" "$CONSUMER_URL" "$(gitmodules_for "https://elsewhere.invalid/schoen/MFTLib.git")")" \
	"ignores a same-named repo on a different host"
assert_equals "" \
	"$(mftlib_submodule_paths "$SELF_REPO_URL" "$CONSUMER_URL" "$(gitmodules_for "../../MFTLib.git")")" \
	"ignores a relative url that resolves outside the owner"
assert_equals "" \
	"$(mftlib_submodule_paths "$SELF_REPO_URL" "$CONSUMER_URL" "")" \
	"ignores an empty .gitmodules"

# --- integration tests: full main run against fixture repos ----------------

start_test "a gitlink-only consumer gets a bump branch and a pull request"
FIXTURE_REPOS="consumer-a"
make_consumer consumer-a "$OLD_SHA" "../MFTLib.git"
run_sync "$NEW_SHA"
assert_equals "0" "$RUN_STATUS" "exits successfully"
assert_equals "$NEW_SHA" "$(gitlink_of consumer-a "$BRANCH")" "bump branch records the new sha"
assert_equals "$OLD_SHA" "$(gitlink_of consumer-a main)" "the default branch is untouched"
assert_equals "chore: bump MFTLib pin to ${NEW_SHA:0:12}" "$(subject_of consumer-a "$BRANCH")" "commit subject names the short sha"
assert_contains "$(cat "$PR_LOG")" "consumer-a|main|$BRANCH|external/MFTLib" "a pull request was opened against main"
assert_contains "$(cat "$RUN_STDOUT")" "matched 1 consumers" "the summary counts the match"
assert_contains "$(cat "$RUN_STDOUT")" "consumer-a" "the summary names the repo"

start_test "an already current consumer is skipped without failing"
FIXTURE_REPOS="consumer-current"
make_consumer consumer-current "$NEW_SHA" "../MFTLib.git"
run_sync "$NEW_SHA"
assert_equals "0" "$RUN_STATUS" "exits successfully"
assert_contains "$(cat "$RUN_STDOUT")" "skipped" "reports the repo as skipped"
assert_equals "" "$(cat "$PR_LOG")" "opens no pull request"

start_test "a consumer with several stale gitlinks bumps each of them"
FIXTURE_REPOS="consumer-multi"
make_consumer consumer-multi "$OLD_SHA" "../MFTLib.git"
MULTI_WORK="$FIXTURE_WORK/consumer-multi-work"
(
	cd "$MULTI_WORK" || exit 1
	printf '[submodule "third_party/MFTLib"]\n\tpath = third_party/MFTLib\n\turl = ../MFTLib.git\n' >>.gitmodules
	mkdir -p third_party
	git add .gitmodules
	git update-index --add --cacheinfo "160000,$OLD_SHA,third_party/MFTLib"
	git commit -qm "second gitlink"
	git push -q origin HEAD:refs/heads/main
)
run_sync "$NEW_SHA"
assert_equals "0" "$RUN_STATUS" "exits successfully"
assert_equals "$NEW_SHA" "$(gitlink_of consumer-multi "$BRANCH")" "bumps external/MFTLib"
assert_equals "$NEW_SHA" \
	"$(git --git-dir="$FIXTURE_GITEA/schoen/consumer-multi.git" ls-tree "$BRANCH" -- third_party/MFTLib | awk '{print $3}')" \
	"bumps third_party/MFTLib"

start_test "zero matched consumers fails loudly instead of reporting success"
FIXTURE_REPOS="no-submodules"
make_plain_repo no-submodules
run_sync "$NEW_SHA"
assert_equals "1" "$RUN_STATUS" "exits non-zero"
assert_contains "$(cat "$RUN_STDERR")" "no repository owned by schoen pins MFTLib with a submodule gitlink" \
	"names the zero-match condition"
assert_contains "$(cat "$RUN_STDERR")" "refusing to report success on zero matches" \
	"says why it failed"
assert_equals "" "$(cat "$PR_LOG")" "opens no pull request"

start_test "a legacy .mftlib/pin-only repo is not a consumer (issue #194)"
FIXTURE_REPOS="pin-only"
make_plain_repo pin-only "$OLD_SHA"
assert_equals "$OLD_SHA" \
	"$(git --git-dir="$FIXTURE_GITEA/schoen/pin-only.git" show "main:.mftlib/pin")" "the fixture carries a pin file"
run_sync "$NEW_SHA"
assert_equals "1" "$RUN_STATUS" "exits non-zero on the shape the old script accepted"
assert_contains "$(cat "$RUN_STDERR")" "no repository owned by schoen pins MFTLib" \
	"reports the zero-match failure"

start_test "an unreadable gitlink fails the run beside a consumer that bumped cleanly"
FIXTURE_REPOS="consumer-broken consumer-a2"
make_consumer consumer-a2 "$OLD_SHA" "../MFTLib.git"
seed_bare_repo consumer-broken
BROKEN_WORK="$FIXTURE_WORK/consumer-broken-work"
git clone -q "$FIXTURE_GITEA/schoen/consumer-broken.git" "$BROKEN_WORK" 2>/dev/null
(
	cd "$BROKEN_WORK" || exit 1
	git config user.name fixture
	git config user.email fixture@example.invalid
	printf '[submodule "external/MFTLib"]\n\tpath = external/MFTLib\n\turl = ../MFTLib.git\n' >.gitmodules
	git add .gitmodules
	git commit -qm "declares a submodule but commits no gitlink"
	git push -q origin HEAD:refs/heads/main
)
run_sync "$NEW_SHA"
assert_equals "1" "$RUN_STATUS" "exits non-zero"
assert_contains "$(cat "$RUN_STDOUT")" "consumer-broken" "reports the broken repo"
assert_contains "$(cat "$RUN_STDOUT")" "is not a readable gitlink" "explains the failure"
assert_equals "$NEW_SHA" "$(gitlink_of consumer-a2 "$BRANCH")" "still bumps the healthy consumer"

# --- report -----------------------------------------------------------------

printf '\n%s\n' "sync_consumers.tests.sh: $TESTS_RUN tests, $FAILURES assertion failures"
if [ "$FAILURES" -ne 0 ]; then
	exit 1
fi
exit 0
