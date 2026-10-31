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

# Fixture Git isolation. Every git command below must touch only the owned
# local remotes and commit as the fixture identity, whatever the caller exported:
# a parent GIT_CONFIG_COUNT block can set remote.origin.pushurl and turn the
# "offline" seeding pushes into pushes to a real forge (issue 856). The identity
# lives in an owned global config, not in GIT_AUTHOR_* variables, because the
# script under test sets its bot identity with git config user.name.
isolate_git_environment() {
	local variable
	for variable in $(compgen -v | grep -E '^(GIT_CONFIG_(KEY|VALUE)_[0-9]+|GIT_(AUTHOR|COMMITTER)_(NAME|EMAIL|DATE))$'); do
		unset "$variable"
	done
	unset GIT_CONFIG GIT_CONFIG_COUNT GIT_CONFIG_PARAMETERS GIT_DIR GIT_WORK_TREE GIT_INDEX_FILE \
		GIT_SSH GIT_SSH_COMMAND GIT_ASKPASS
	export HOME="$FIXTURE_ROOT/home"
	export XDG_CONFIG_HOME="$HOME/.config"
	mkdir -p "$XDG_CONFIG_HOME"
	export GIT_CONFIG_GLOBAL="$HOME/gitconfig"
	export GIT_CONFIG_NOSYSTEM=1
	export GIT_TERMINAL_PROMPT=0
	git config --file "$GIT_CONFIG_GLOBAL" user.name fixture
	git config --file "$GIT_CONFIG_GLOBAL" user.email fixture@example.invalid
}
isolate_git_environment

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

assert_not_contains() {
	# assert_not_contains <haystack> <needle> <label>
	case "$1" in
	*"$2"*) fail_assertion "$3: '$2' unexpectedly found in output" ;;
	*) printf 'ok   %s\n' "$3" ;;
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

# Probe mode for the isolation regression test: report who authored the seeded
# fixture history, then stop before any other test runs.
if [ "${1:-}" = "--print-fixture-identity" ]; then
	git --git-dir="$FIXTURE_GITEA/schoen/MFTLib.git" log -1 \
		--format='%an <%ae> / %cn <%ce>' main
	exit 0
fi

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

resolve_bot_identity() {
	BOT_LOGIN="claude-code"
	BOT_EMAIL="claude-code@example.invalid"
}

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

# Fakes for the two helpers refresh_existing_pull_request calls, used by the
# unit tests below that exercise the real refresh_existing_pull_request. Each
# call is logged so a test can assert on the arguments it was given, and each
# outcome is controlled by the FIND_PR_INDEX_* / UPDATE_PR_* variables the
# test sets beforehand. FIND_PR_INDEX_EXIT follows find_open_pull_request_index's
# own contract: 0 success, 2 no open match, 3 more than one open match, 1 any
# other failure.

FIND_PR_INDEX_LOG="$FIXTURE_ROOT/find_pr_index.log"
FIND_PR_INDEX_RESULT=""
FIND_PR_INDEX_EXIT=0
find_open_pull_request_index() {
	printf '%s|%s|%s\n' "$1" "$2" "$3" >>"$FIND_PR_INDEX_LOG"
	printf '%s' "$FIND_PR_INDEX_RESULT"
	return "$FIND_PR_INDEX_EXIT"
}

UPDATE_PR_LOG="$FIXTURE_ROOT/update_pr.log"
UPDATE_PR_EXIT=0
UPDATE_PR_URL=""
update_pull_request() {
	printf '%s|%s|%s|%s\n' "$1" "$2" "$3" "$4" >>"$UPDATE_PR_LOG"
	[ "$UPDATE_PR_EXIT" -eq 0 ] || return "$UPDATE_PR_EXIT"
	printf '%s' "$UPDATE_PR_URL"
}

# finalize_pull_request has its own transport-level tests further down; here it
# is only logged, so the refresh and integration tests never reach the network.
FINALIZE_LOG="$FIXTURE_ROOT/finalize.log"
finalize_pull_request() {
	printf '%s|%s|%s|%s\n' "$1" "$2" "$3" "$4" >>"$FINALIZE_LOG"
}

run_sync() {
	# run_sync <new_sha>: run main in a subshell, capturing streams and status.
	: >"$PR_LOG"
	( main "$1" ) >"$RUN_STDOUT" 2>"$RUN_STDERR"
	RUN_STATUS=$?
}

# --- unit tests: pure helpers ----------------------------------------------
start_test "missing server fails before transport"
(
    unset GITEA_URL
    source "$SCRIPT_PATH"
    resolve_owner_uid() { printf 'UNEXPECTED_TRANSPORT' >&2; return 1; }
    main "$NEW_SHA"
) >"$RUN_STDOUT" 2>"$RUN_STDERR"
RUN_STATUS=$?
assert_equals 1 "$RUN_STATUS" "missing server exits nonzero"
assert_contains "$(cat "$RUN_STDERR")" 'GITEA_URL env var is required' "actionable input error"
assert_not_contains "$(cat "$RUN_STDERR")" 'UNEXPECTED_TRANSPORT' "no transport before validation"

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

# --- unit tests: existing pull request refresh ------------------------------
# refresh_existing_pull_request is the real, unstubbed script function; only
# the two helpers it calls (find_open_pull_request_index, update_pull_request)
# are faked above, so these tests exercise the actual detection-and-PATCH
# logic without needing a live Gitea server. FIND_PR_INDEX_EXIT follows
# find_open_pull_request_index's own contract: 0 success, 2 no open match, 3
# more than one open match, 1 any other failure.

start_test "an existing open pull request has its title and body refreshed to the new sha"
SHORT_SHA="${NEW_SHA:0:12}"
SUMMARY=()
: >"$FINALIZE_LOG"
: >"$FIND_PR_INDEX_LOG"
: >"$UPDATE_PR_LOG"
FIND_PR_INDEX_RESULT="42"
FIND_PR_INDEX_EXIT=0
UPDATE_PR_EXIT=0
UPDATE_PR_URL="$FIXTURE_GITEA/schoen/consumer-refresh/pulls/42"
refresh_existing_pull_request "consumer-refresh" "main" "external/MFTLib"
REFRESH_STATUS=$?
assert_equals "0" "$REFRESH_STATUS" "reports success"
assert_contains "$(cat "$FIND_PR_INDEX_LOG")" "consumer-refresh|main|$BRANCH" \
	"looks up the existing pull request by repo, base branch and head branch"
assert_contains "$(cat "$UPDATE_PR_LOG")" \
	"consumer-refresh|42|chore: bump MFTLib pin to $SHORT_SHA|## Problem" \
	"PATCHes the existing pull request with a title naming the new sha and a body that opens with a Problem section"
assert_contains "$(cat "$UPDATE_PR_LOG")" \
	"Automated update of the external/MFTLib submodule gitlink to $NEW_SHA by sync-consumers." \
	"the refreshed body names the new sha"
assert_contains "${SUMMARY[*]}" "updated" "records the refresh as updated"
assert_contains "${SUMMARY[*]}" "#42" "names the pull request index in the summary"
assert_contains "$(cat "$FINALIZE_LOG")" "consumer-refresh|main|42|" \
	"labels and requests auto-merge on the refreshed pull request"

start_test "a failed PATCH on an existing pull request is reported as failed, not swallowed"
SUMMARY=()
: >"$FINALIZE_LOG"
: >"$FIND_PR_INDEX_LOG"
: >"$UPDATE_PR_LOG"
FIND_PR_INDEX_RESULT="42"
FIND_PR_INDEX_EXIT=0
UPDATE_PR_EXIT=1
refresh_existing_pull_request "consumer-refresh" "main" "external/MFTLib"
REFRESH_STATUS=$?
assert_equals "1" "$REFRESH_STATUS" "reports failure"
assert_contains "${SUMMARY[*]}" "failed" "records the failed PATCH"
assert_contains "${SUMMARY[*]}" "consumer-refresh" "names the repo in the summary"
assert_equals "" "$(cat "$FINALIZE_LOG")" "never labels or auto-merges after a failed PATCH"

start_test "zero open pull requests on a 409 is a contradiction: recorded as failed, never patched"
SUMMARY=()
: >"$FINALIZE_LOG"
: >"$FIND_PR_INDEX_LOG"
: >"$UPDATE_PR_LOG"
FIND_PR_INDEX_RESULT=""
FIND_PR_INDEX_EXIT=2
refresh_existing_pull_request "consumer-refresh" "main" "external/MFTLib"
REFRESH_STATUS=$?
assert_equals "1" "$REFRESH_STATUS" "reports failure"
assert_contains "${SUMMARY[*]}" "failed" "records the failure"
assert_contains "${SUMMARY[*]}" "no pull request is open" "the summary explains why: none is open"
assert_equals "" "$(cat "$UPDATE_PR_LOG")" "never calls update_pull_request"
assert_equals "" "$(cat "$FINALIZE_LOG")" "never labels or auto-merges"

start_test "more than one open pull request matching the branch is a contradiction: recorded as failed, never guessed"
SUMMARY=()
: >"$FINALIZE_LOG"
: >"$FIND_PR_INDEX_LOG"
: >"$UPDATE_PR_LOG"
FIND_PR_INDEX_RESULT=""
FIND_PR_INDEX_EXIT=3
refresh_existing_pull_request "consumer-refresh" "main" "external/MFTLib"
REFRESH_STATUS=$?
assert_equals "1" "$REFRESH_STATUS" "reports failure"
assert_contains "${SUMMARY[*]}" "failed" "records the failure"
assert_contains "${SUMMARY[*]}" "more than one pull request is open" "the summary explains why: more than one is open"
assert_equals "" "$(cat "$UPDATE_PR_LOG")" "never calls update_pull_request"
assert_equals "" "$(cat "$FINALIZE_LOG")" "never labels or auto-merges"

# --- transport-level regression: real HTTP status handling and endpoint choice
# The tests above fake find_open_pull_request_index and update_pull_request as
# whole functions, so they never exercise the real curl calls, the endpoint
# chosen, or the status codes the script compares against. These tests
# re-source the real script in a subshell, so find_open_pull_request_index,
# update_pull_request and refresh_existing_pull_request are their actual
# implementations, and fake only curl itself, at the transport level.

run_transport_test() {
	# run_transport_test <old_endpoint_pr_number> <open_prs_json> <repo>
	# <base_branch> <paths>: runs in a subshell of its own ($(...) below), so
	# every assignment here, including the source below re-establishing
	# BRANCH, SHORT_SHA and NEW_SHA as globals inside that subshell, is local
	# to it and never reaches the rest of this file; `local` here only
	# silences shellcheck's SC2030/SC2031 cross-scope warnings, which
	# otherwise fire on unrelated later reads of the same names elsewhere in
	# this file.
	#
	# curl is faked to answer the three call shapes the real functions make:
	#   - PATCH .../pulls/{index} (update_pull_request; -o/-w and -X PATCH):
	#     answers 201, Gitea's real documented success code.
	#   - GET .../pulls?state=open&... (find_open_pull_request_index's list
	#     call; no -o/-w): answers with $open_prs_json, a JSON array of open
	#     pull requests, as real Gitea would once its state=open filter has
	#     already excluded anything merged or closed.
	#   - GET .../pulls/{base}/{head} (the retired, buggy per-pair lookup;
	#     -o/-w but no -X): answers 200 with pull request number
	#     $old_endpoint_pr_number, reproducing Gitea returning the oldest pull
	#     request for the pair regardless of state (sync-consumers job 92400
	#     on MFTLib push c029d31, which PATCHed file-wizard's long-merged PR
	#     441 instead of its open PR 478). The fixed script never calls this
	#     endpoint; a version that regresses back to calling it will reach
	#     this branch and PATCH the wrong pull request, exactly reproducing
	#     the incident.
	local old_endpoint_pr_number="$1" open_prs_json="$2" repo="$3" base_branch="$4" paths="$5"
	local BRANCH SHORT_SHA NEW_SHA SCRATCH

	# shellcheck source=scripts/sync_consumers.sh
	source "$SCRIPT_PATH"

	curl() {
		local output_file="" method="GET" url arg
		while [ $# -gt 0 ]; do
			arg="$1"
			case "$arg" in
			-o) output_file="$2"; shift 2 ;;
			-X) method="$2"; shift 2 ;;
			-H | --data-binary) shift 2 ;;
			-w) shift 2 ;;
			-sS | -sSf) shift ;;
			*) url="$arg"; shift ;;
			esac
		done

		if [ "$method" = "PATCH" ]; then
			printf '{"html_url": "https://fixture.invalid/schoen/%s/pulls/%s"}' "$FIXTURE_REPO_NAME" "${url##*/}" >"$output_file"
			printf '201'
			return 0
		fi

		case "$url" in
		*'?state=open'*)
			printf '%s' "$FIXTURE_OPEN_PRS_JSON"
			return 0
			;;
		*)
			printf '{"number": %s, "html_url": "https://fixture.invalid/schoen/%s/pulls/%s"}' \
				"$FIXTURE_OLD_ENDPOINT_PR_NUMBER" "$FIXTURE_REPO_NAME" "$FIXTURE_OLD_ENDPOINT_PR_NUMBER" >"$output_file"
			printf '200'
			return 0
			;;
		esac
	}

	FIXTURE_OLD_ENDPOINT_PR_NUMBER="$old_endpoint_pr_number"
	FIXTURE_OPEN_PRS_JSON="$open_prs_json"
	FIXTURE_REPO_NAME="$repo"
	auth_header="Authorization: token fixture-token"
	GITEA_URL="https://fixture.invalid"
	GITEA_OWNER="schoen"
	SHORT_SHA="abc123456789"
	NEW_SHA="abc123456789abc123456789abc123456789abc"
	SCRATCH="$(mktemp -d)"
	SUMMARY=()

	refresh_existing_pull_request "$repo" "$base_branch" "$paths"
	printf 'STATUS=%s\n' "$?"
	printf 'SUMMARY=%s\n' "${SUMMARY[*]}"
}

start_test "a PATCH answered with HTTP 201 (Gitea's documented success code) is recorded as updated, not failed"
TRANSPORT_SUMMARY="$(run_transport_test 42 \
	'[{"number": 42, "head": {"ref": "chore/mftlib-pin-bump"}}]' \
	consumer-refresh main external/MFTLib)"
assert_contains "$TRANSPORT_SUMMARY" "STATUS=0" "refresh_existing_pull_request succeeds against a real 201 PATCH response"
assert_contains "$TRANSPORT_SUMMARY" "SUMMARY=consumer-refresh" "the consumer is named in the recorded summary"
assert_contains "$TRANSPORT_SUMMARY" "updated" "the consumer is recorded as updated, not failed"

start_test "the open pull request is patched, not the oldest match the retired base/head endpoint would have returned"
TRANSPORT_SUMMARY="$(run_transport_test 441 \
	'[{"number": 478, "head": {"ref": "chore/mftlib-pin-bump"}}]' \
	file-wizard main external/MFTLib)"
assert_contains "$TRANSPORT_SUMMARY" "STATUS=0" "refresh_existing_pull_request succeeds"
assert_contains "$TRANSPORT_SUMMARY" "updated" "the consumer is recorded as updated"
assert_contains "$TRANSPORT_SUMMARY" "#478" "pull request 478, the open one, is patched"
assert_not_contains "$TRANSPORT_SUMMARY" "441" "pull request 441, the long-merged one, is never named as patched"

# --- label and guarded auto-merge: real helpers, fake Gitea at curl ---------
# run_auto_bump_test sources the real script in a subshell and fakes only curl.
# The fake keeps a call log ($AUTO_BUMP_CURL_LOG, one "METHOD url body" line per
# call) and answers from these variables, set by the caller:
#   FAKE_LABELS_JSON, FAKE_LABEL_CREATE_STATUS, FAKE_BRANCH_JSON,
#   FAKE_BRANCH_STATUS, FAKE_LABEL_APPLY_STATUS, FAKE_MERGE_STATUS,
#   FAKE_FAILURE_OPERATION, FAKE_FAILURE_MODE.

AUTO_BUMP_CURL_LOG="$FIXTURE_ROOT/auto_bump_curl.log"
FAKE_LABELS_JSON='[]'
FAKE_LABEL_CREATE_STATUS=201
FAKE_LABEL_APPLY_STATUS=200
FAKE_BRANCH_JSON='{}'
FAKE_BRANCH_STATUS=200
FAKE_MERGE_STATUS=201
FAKE_FAILURE_OPERATION=""
FAKE_FAILURE_MODE=""
PROTECTED_MAIN_JSON='{"name": "main", "enable_status_check": true, "status_check_contexts": ["*"]}'

run_auto_bump_test() {
	# run_auto_bump_test <function> <args...>: run one real script function
	# with curl faked, then print STATUS and the recorded SUMMARY.
	local BRANCH SHORT_SHA SCRATCH PUSHED_HEAD_SHA
	# shellcheck source=scripts/sync_consumers.sh
	source "$SCRIPT_PATH"

	curl() {
		local output_file="" method="GET" url="" body="" arg failure
		while [ $# -gt 0 ]; do
			arg="$1"
			case "$arg" in
			-o) output_file="$2"; shift 2 ;;
			-X) method="$2"; shift 2 ;;
			--data-binary) body="$2"; shift 2 ;;
			-H | -w) shift 2 ;;
			-sS | -sSf) shift ;;
			*) url="$arg"; shift ;;
			esac
		done
		printf '%s %s %s\n' "$method" "$url" "$body" >>"$AUTO_BUMP_CURL_LOG"
		failure=0
		case "$FAKE_FAILURE_OPERATION $method $url" in
		"label-lookup GET "*"/schoen/consumer-a/labels?"*) failure=1 ;;
		"label-create POST "*"/schoen/consumer-a/labels") failure=1 ;;
		"label-apply POST "*"/schoen/consumer-a/issues/"*"/labels") failure=1 ;;
		"merge POST "*"/schoen/consumer-a/pulls/"*"/merge") failure=1 ;;
		esac
		if [ "$failure" = "1" ]; then
			if [ "$FAKE_FAILURE_MODE" = "transport" ]; then
				printf 'simulated %s transport failure\n' "$FAKE_FAILURE_OPERATION" >&2
				return 7
			fi
			printf '{"message":"simulated failure"}' >"$output_file"
			printf '500'
			return 0
		fi
		case "$method $url" in
		"GET "*"/labels?"*)
			printf '%s' "$FAKE_LABELS_JSON" >"$output_file"
			printf '200'
			;;
		"POST "*"/issues/"*"/labels")
			printf '[]' >"$output_file"
			printf '%s' "$FAKE_LABEL_APPLY_STATUS"
			;;
		"POST "*"/labels")
			printf '{"id": 77, "name": "auto-bump"}' >"$output_file"
			printf '%s' "$FAKE_LABEL_CREATE_STATUS"
			;;
		"GET "*"/branches/"*)
			printf '%s' "$FAKE_BRANCH_JSON" >"$output_file"
			printf '%s' "$FAKE_BRANCH_STATUS"
			;;
		"POST "*"/merge")
			printf '{}' >"$output_file"
			printf '%s' "$FAKE_MERGE_STATUS"
			;;
		*)
			printf '{}' >"$output_file"
			printf '404'
			;;
		esac
	}

	auth_header="Authorization: token fixture-token"
	GITEA_URL="https://fixture.invalid"
	GITEA_OWNER="schoen"
	BRANCH="chore/mftlib-pin-bump"
	SCRATCH="$(mktemp -d)"
	SUMMARY=()
	: >"$AUTO_BUMP_CURL_LOG"
	"$@"
	printf 'STATUS=%s\n' "$?"
	printf 'SUMMARY=%s\n' "${SUMMARY[*]}"
}

HEAD_ONE="1111111111111111111111111111111111111111"
HEAD_TWO="2222222222222222222222222222222222222222"

start_test "an existing auto-bump label is reused, applied by id, and auto-merge is requested when the base rule has status checks"
FAKE_LABELS_JSON='[{"id": 5, "name": "bug"}, {"id": 9, "name": "auto-bump"}]'
FAKE_BRANCH_JSON="$PROTECTED_MAIN_JSON"
FAKE_MERGE_STATUS=201
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_OUTPUT" "STATUS=0" "never fails the fan-out"
assert_contains "$AUTO_BUMP_OUTPUT" "auto-merge requested" "records the auto-merge request"
assert_not_contains "$AUTO_BUMP_OUTPUT" "warning" "records no warning"
assert_not_contains "$AUTO_BUMP_CALLS" "POST https://fixture.invalid/api/v1/repos/schoen/consumer-a/labels" "does not create a label that exists"
assert_contains "$AUTO_BUMP_CALLS" 'issues/12/labels {"labels":[9]}' "applies the existing label id"
assert_contains "$AUTO_BUMP_CALLS" "pulls/12/merge" "requests the merge schedule"
assert_contains "$AUTO_BUMP_CALLS" '"head_commit_id":"'"$HEAD_ONE"'"' "checks the merge payload carries the pushed head sha"
assert_contains "$AUTO_BUMP_CALLS" '"merge_when_checks_succeed":true' "asks to merge when checks succeed"
assert_contains "$AUTO_BUMP_CALLS" '"Do":"squash"' "squash merges"
assert_contains "$AUTO_BUMP_CALLS" '"delete_branch_after_merge":true' "deletes the bump branch after merging"

start_test "a missing auto-bump label is created, then applied"
FAKE_LABELS_JSON='[{"id": 5, "name": "bug"}]'
FAKE_BRANCH_JSON="$PROTECTED_MAIN_JSON"
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_CALLS" "POST https://fixture.invalid/api/v1/repos/schoen/consumer-a/labels" "creates the label"
assert_contains "$AUTO_BUMP_CALLS" "Opened by an automatic dependency bump; merges itself on green CI" "uses the label description"
assert_contains "$AUTO_BUMP_CALLS" 'issues/12/labels {"labels":[77]}' "applies the id of the created label"
assert_not_contains "$AUTO_BUMP_OUTPUT" "warning" "records no warning"

SKIP_TEXT="auto-merge skipped: main does not require all status checks (*)"

start_test "auto-merge is skipped when the status check list names checks but lacks the * pattern"
FAKE_LABELS_JSON='[{"id": 9, "name": "auto-bump"}]'
FAKE_BRANCH_JSON='{"name": "main", "enable_status_check": true, "status_check_contexts": ["ci/build", "ci/test"]}'
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_OUTPUT" "$SKIP_TEXT" "records the skip"
assert_contains "$AUTO_BUMP_CALLS" 'issues/12/labels' "still labels the pull request"
assert_contains "$AUTO_BUMP_CALLS" '/branches/main' "reads the branch, not branch_protections"
assert_not_contains "$AUTO_BUMP_CALLS" "branch_protections" "never calls branch_protections"
assert_not_contains "$AUTO_BUMP_CALLS" "/merge" "never calls the merge endpoint"

start_test "auto-merge is skipped when enable_status_check is false, even with the * pattern"
FAKE_BRANCH_JSON='{"name": "main", "enable_status_check": false, "status_check_contexts": ["*"]}'
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
assert_contains "$AUTO_BUMP_OUTPUT" "$SKIP_TEXT" "skips when status checks are disabled"
assert_not_contains "$(cat "$AUTO_BUMP_CURL_LOG")" "/merge" "no merge call with checks disabled"

start_test "auto-merge is skipped when no contexts are configured"
FAKE_BRANCH_JSON='{"name": "main", "enable_status_check": true, "status_check_contexts": []}'
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
assert_contains "$AUTO_BUMP_OUTPUT" "$SKIP_TEXT" "skips with empty contexts"
assert_not_contains "$(cat "$AUTO_BUMP_CURL_LOG")" "/merge" "no merge call with empty contexts"

start_test "auto-merge is skipped when the branch cannot be read (non-200)"
FAKE_BRANCH_JSON='{"message": "forbidden"}'
FAKE_BRANCH_STATUS=403
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main 12 "$HEAD_ONE")"
assert_contains "$AUTO_BUMP_OUTPUT" "could not read branch main" "records why it skipped"
assert_not_contains "$(cat "$AUTO_BUMP_CURL_LOG")" "/merge" "never guesses; no merge call"
FAKE_BRANCH_STATUS=200

finalize_two_consumers() {
	local first_status
	finalize_pull_request consumer-a main 12 "$HEAD_ONE"
	first_status=$?
	finalize_pull_request consumer-b main 12 "$HEAD_TWO"
	return "$first_status"
}

for OPERATION in label-lookup label-create label-apply merge; do
	for MODE in http transport; do
		start_test "a $OPERATION $MODE failure warns and the second consumer still processes"
		FAKE_FAILURE_OPERATION="$OPERATION"
		FAKE_FAILURE_MODE="$MODE"
		FAKE_LABELS_JSON='[{"id": 9, "name": "auto-bump"}]'
		if [ "$OPERATION" = "label-create" ]; then
			FAKE_LABELS_JSON='[{"id": 5, "name": "bug"}]'
		fi
		FAKE_BRANCH_JSON="$PROTECTED_MAIN_JSON"
		AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_two_consumers)"
		AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
		if [ "$OPERATION" = "merge" ]; then
			if [ "$MODE" = "http" ]; then
				EXPECTED_DETAIL="warning: auto-merge request returned 500"
			else
				EXPECTED_DETAIL="warning: auto-merge request returned curl-error"
			fi
		else
			EXPECTED_DETAIL="warning: could not apply label auto-bump to PR #12"
		fi
		assert_contains "$AUTO_BUMP_OUTPUT" "STATUS=0" "still returns success"
		assert_contains "$AUTO_BUMP_OUTPUT" "$(printf '%-28s %-18s' consumer-a warning)" "records a warning row for the first consumer"
		assert_contains "$AUTO_BUMP_OUTPUT" "$EXPECTED_DETAIL" "records the failure detail"
		assert_contains "$AUTO_BUMP_OUTPUT" "$(printf '%-28s %-18s' consumer-b auto-bump)" "records success for the second consumer"
		assert_contains "$AUTO_BUMP_CALLS" "consumer-a/pulls/12/merge" "still requests the first consumer merge"
		assert_contains "$AUTO_BUMP_CALLS" "consumer-b/pulls/12/merge" "processes the second consumer"
	done
done
FAKE_FAILURE_OPERATION=""
FAKE_FAILURE_MODE=""

start_test "a pull request number that cannot be read is a recorded warning with no API calls"
AUTO_BUMP_OUTPUT="$(run_auto_bump_test finalize_pull_request consumer-a main "" "$HEAD_ONE")"
assert_contains "$AUTO_BUMP_OUTPUT" "could not read the pull request number" "records the warning"
assert_equals "" "$(cat "$AUTO_BUMP_CURL_LOG")" "makes no calls"

start_test "a refreshed pull request re-requests auto-merge with the new head sha"
refresh_with_new_head() {
	find_open_pull_request_index() { printf '42'; }
	update_pull_request() { printf 'https://fixture.invalid/schoen/consumer-a/pulls/42'; }
	SHORT_SHA="aaaaaaaaaaaa"
	PUSHED_HEAD_SHA="$HEAD_TWO"
	refresh_existing_pull_request consumer-a main external/MFTLib
}
FAKE_LABELS_JSON='[{"id": 9, "name": "auto-bump"}]'
FAKE_BRANCH_JSON="$PROTECTED_MAIN_JSON"
AUTO_BUMP_OUTPUT="$(run_auto_bump_test refresh_with_new_head)"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_OUTPUT" "STATUS=0" "refresh succeeds"
assert_contains "$AUTO_BUMP_CALLS" 'issues/42/labels {"labels":[9]}' "labels the refreshed pull request"
assert_contains "$AUTO_BUMP_CALLS" "pulls/42/merge" "re-requests auto-merge"
assert_contains "$AUTO_BUMP_CALLS" '"head_commit_id":"'"$HEAD_TWO"'"' "uses the newly pushed head sha"

start_test "a newly opened pull request is labeled and auto-merged using the number Gitea returned"
open_with_new_head() {
	PUSHED_HEAD_SHA="$HEAD_ONE"
	SHORT_SHA="aaaaaaaaaaaa"
	# Wrap the fake curl so the pulls POST answers 201 with a number.
	eval "$(declare -f curl | sed '1s/curl/fake_label_curl/')"
	curl() {
		local arg previous="" method="GET" output_file="" body=""
		for arg in "$@"; do
			[ "$previous" = "-o" ] && output_file="$arg"
			[ "$previous" = "-X" ] && method="$arg"
			[ "$previous" = "--data-binary" ] && body="$arg"
			previous="$arg"
		done
		case "${method} ${*: -1}" in
		"POST "*"/schoen/consumer-a/pulls")
			printf '%s %s %s\n' "$method" "${*: -1}" "$body" >>"$AUTO_BUMP_CURL_LOG"
			printf '{"number": 31, "html_url": "https://fixture.invalid/schoen/consumer-a/pulls/31"}' >"$output_file"
			printf '201'
			;;
		*) fake_label_curl "$@" ;;
		esac
	}
	open_pull_request consumer-a main external/MFTLib
}
AUTO_BUMP_OUTPUT="$(run_auto_bump_test open_with_new_head)"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_OUTPUT" "STATUS=0" "open succeeds"
assert_contains "$AUTO_BUMP_CALLS" 'issues/31/labels' "labels the new pull request"
assert_contains "$AUTO_BUMP_CALLS" 'by sync-consumers.","labels":[9]}' "includes the existing label in the creation payload"
assert_contains "$AUTO_BUMP_CALLS" "pulls/31/merge" "requests auto-merge for the new pull request"
assert_contains "$AUTO_BUMP_CALLS" '"head_commit_id":"'"$HEAD_ONE"'"' "checks the merge payload carries the pushed head"

start_test "a newly opened pull request creates and includes a missing auto-bump label"
FAKE_LABELS_JSON='[{"id": 5, "name": "bug"}]'
AUTO_BUMP_OUTPUT="$(run_auto_bump_test open_with_new_head)"
AUTO_BUMP_CALLS="$(cat "$AUTO_BUMP_CURL_LOG")"
assert_contains "$AUTO_BUMP_OUTPUT" "STATUS=0" "open succeeds"
assert_contains "$AUTO_BUMP_CALLS" "POST https://fixture.invalid/api/v1/repos/schoen/consumer-a/labels" "creates the missing label"
assert_contains "$AUTO_BUMP_CALLS" 'by sync-consumers.","labels":[77]}' "includes the created label in the creation payload"
assert_contains "$AUTO_BUMP_CALLS" 'issues/31/labels {"labels":[77]}' "keeps the idempotent label application"

# --- bot identity: real resolve_bot_identity against a faked /api/v1/user ---

run_identity_test() {
	# run_identity_test <user_status> <user_json>: run the real function with
	# curl faked, then print STATUS, LOGIN and EMAIL.
	local SCRATCH
	# shellcheck source=scripts/sync_consumers.sh
	source "$SCRIPT_PATH"
	curl() {
		local output_file="" url="" arg
		while [ $# -gt 0 ]; do
			arg="$1"
			case "$arg" in
			-o) output_file="$2"; shift 2 ;;
			-H | -w) shift 2 ;;
			-sS | -sSf) shift ;;
			*) url="$arg"; shift ;;
			esac
		done
		printf '%s' "$FAKE_USER_JSON" >"$output_file"
		printf '%s' "$FAKE_USER_STATUS"
	}
	auth_header="Authorization: token fixture-token"
	GITEA_URL="https://fixture.invalid"
	SCRATCH="$(mktemp -d)"
	resolve_bot_identity
	printf 'STATUS=%s\nLOGIN=%s\nEMAIL=%s\n' "$?" "$BOT_LOGIN" "$BOT_EMAIL"
}

start_test "the bot identity is read from /api/v1/user"
FAKE_USER_STATUS=200
FAKE_USER_JSON='{"login": "claude-code", "email": "claude-code@example.invalid"}'
IDENTITY_OUTPUT="$(run_identity_test)"
assert_contains "$IDENTITY_OUTPUT" "STATUS=0" "succeeds"
assert_contains "$IDENTITY_OUTPUT" "LOGIN=claude-code" "reads the login"
assert_contains "$IDENTITY_OUTPUT" "EMAIL=claude-code@example.invalid" "reads the email"

start_test "a failed or incomplete /api/v1/user call fails instead of inventing an identity"
FAKE_USER_STATUS=401
FAKE_USER_JSON='{"message": "unauthorized"}'
IDENTITY_OUTPUT="$(run_identity_test)"
assert_contains "$IDENTITY_OUTPUT" "STATUS=1" "fails on a non-200"
FAKE_USER_STATUS=200
FAKE_USER_JSON='{"login": "claude-code", "email": ""}'
IDENTITY_OUTPUT="$(run_identity_test)"
assert_contains "$IDENTITY_OUTPUT" "STATUS=1" "fails on an empty email"

start_test "the run aborts before any bump when the bot identity cannot be read"
FIXTURE_REPOS="consumer-noid"
make_consumer consumer-noid "$OLD_SHA" "../MFTLib.git"
: >"$PR_LOG"
(
	resolve_bot_identity() { return 1; }
	main "$NEW_SHA"
) >"$RUN_STDOUT" 2>"$RUN_STDERR"
RUN_STATUS=$?
assert_equals "1" "$RUN_STATUS" "exits non-zero"
assert_contains "$(cat "$RUN_STDERR")" "refusing to author bump commits" "says why"
assert_equals "" "$(cat "$PR_LOG")" "opens no pull request"

# --- integration tests: full main run against fixture repos ----------------

start_test "a gitlink-only consumer gets a bump branch and a pull request"
FIXTURE_REPOS="consumer-a"
make_consumer consumer-a "$OLD_SHA" "../MFTLib.git"
run_sync "$NEW_SHA"
assert_equals "0" "$RUN_STATUS" "exits successfully"
assert_equals "$NEW_SHA" "$(gitlink_of consumer-a "$BRANCH")" "bump branch records the new sha"
assert_equals "$OLD_SHA" "$(gitlink_of consumer-a main)" "the default branch is untouched"
assert_equals "chore: bump MFTLib pin to ${NEW_SHA:0:12}" "$(subject_of consumer-a "$BRANCH")" "commit subject names the short sha"
assert_equals 'claude-code@example.invalid' \
    "$(git --git-dir="$FIXTURE_GITEA/schoen/consumer-a.git" log -1 --format=%ae "$BRANCH")" \
    "commit author email is the one read from the API"
assert_equals 'claude-code' \
    "$(git --git-dir="$FIXTURE_GITEA/schoen/consumer-a.git" log -1 --format=%an "$BRANCH")" \
    "commit author name is the login read from the API"
assert_equals 'claude-code@example.invalid' \
    "$(git --git-dir="$FIXTURE_GITEA/schoen/consumer-a.git" log -1 --format=%ce "$BRANCH")" \
    "committer email matches the author"
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

start_test "fixture setup ignores inherited Git overrides and never pushes to a foreign remote"
# Issue 856: a parent environment that points remote.origin.pushurl elsewhere
# and exports author variables must not redirect the seeding pushes or rename
# the fixture author. The foreign remote is an owned local bare repository, so
# contact is observable as any ref appearing in it.
FOREIGN_REMOTE="$FIXTURE_ROOT/foreign-remote.git"
git init -q --bare --initial-branch=main "$FOREIGN_REMOTE"
# GIT_CONFIG is the target file of git config, so it must not capture fixture writes.
OWNED_CONFIG="$FIXTURE_ROOT/owned-config"
printf '[user]
	name = original-owner
	email = owner@example.invalid
' >"$OWNED_CONFIG"
OWNED_CONFIG_BEFORE="$(cat "$OWNED_CONFIG")"
PROBE_IDENTITY="$(
	GIT_CONFIG="$OWNED_CONFIG" GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=remote.origin.pushurl GIT_CONFIG_VALUE_0="$FOREIGN_REMOTE" \
		GIT_AUTHOR_NAME=intruder GIT_AUTHOR_EMAIL=intruder@example.invalid \
		GIT_COMMITTER_NAME=intruder GIT_COMMITTER_EMAIL=intruder@example.invalid \
		bash "$SCRIPT_DIRECTORY/sync_consumers.tests.sh" --print-fixture-identity 2>/dev/null
)"
assert_equals "fixture <fixture@example.invalid> / fixture <fixture@example.invalid>" \
	"$PROBE_IDENTITY" "seeded history is authored by the fixture identity"
assert_equals "" "$(git --git-dir="$FOREIGN_REMOTE" for-each-ref)" \
	"the foreign push destination was never contacted"
assert_equals "$OWNED_CONFIG_BEFORE" "$(cat "$OWNED_CONFIG")" 	"the inherited GIT_CONFIG file was left unchanged"

# --- report -----------------------------------------------------------------

printf '\n%s\n' "sync_consumers.tests.sh: $TESTS_RUN tests, $FAILURES assertion failures"
if [ "$FAILURES" -ne 0 ]; then
	exit 1
fi
exit 0
