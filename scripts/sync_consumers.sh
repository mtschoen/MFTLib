#!/usr/bin/env bash
# Fan out an MFTLib pin bump to every consumer repository on the
# fleet Gitea instance. Invoked by .gitea/workflows/sync-consumers.yml after
# a push to main.
#
# Required one-time setup (not performed by this script): create an Actions
# secret named MFTLIB_SYNC_TOKEN on this repository holding a Gitea personal
# access token for the claude-code bot, scoped at least write:repository and
# read:repository. claude-code must also be a write collaborator on every
# consumer repository this script updates, or repos/search will not surface
# private ones and the push/PR-create calls below will 403.
#
# Convention: a consumer repository pins MFTLib with a git submodule whose url
# resolves to this repository. Both consumers declare that submodule at
# external/MFTLib with the relative url ../MFTLib.git; the path is read from
# .gitmodules rather than assumed. The gitlink is the single source of truth
# for the pin, so the bump commits the new sha into the gitlink and opens a
# pull request. A repository with no such submodule is not a consumer.
#
# Matching zero consumers is a failure, not a success. A fan-out that bumps
# nothing while reporting green suppresses the signal that the pins drifted,
# which is how both consumers fell four MFTLib pull requests behind (#194).
#
# Usage: sync_consumers.sh <new-sha>
# Required env: GITEA_TOKEN
# Optional env: GITEA_URL (default https://gitea.fleet.sticktoitive.net),
#               GITEA_OWNER (default schoen), SELF_REPO (default MFTLib)
#
# Tests: bash scripts/sync_consumers.tests.sh

set -u
set -o pipefail

SCRIPT_DIRECTORY="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=./mftlib_submodules.sh
source "$SCRIPT_DIRECTORY/mftlib_submodules.sh"

GITEA_URL="${GITEA_URL:-https://gitea.fleet.sticktoitive.net}"
GITEA_OWNER="${GITEA_OWNER:-schoen}"
SELF_REPO="${SELF_REPO:-MFTLib}"

BRANCH="chore/mftlib-pin-bump"
GITMODULES_FILE=".gitmodules"
GITLINK_MODE="160000"

declare -a SUMMARY

record() {
	# record <repo> <result> <detail>. Never pass a credential-bearing URL
	# here; detail lines get printed in the workflow log.
	SUMMARY+=("$(printf '%-28s %-18s %s' "$1" "$2" "$3")")
}

authed_url() {
	# authed_url <clone_url>: embed claude-code:GITEA_TOKEN as basic-auth
	# credentials in a git-smart-HTTP URL. The REST API accepts an
	# `Authorization: token` header, but that is an API-only convention;
	# git's own HTTP transport wants credentials in the URL (or a
	# credential helper), so clone/push use this form instead.
	local url="$1"
	local scheme="${url%%://*}"
	local rest="${url#*://}"
	printf '%s://claude-code:%s@%s' "$scheme" "$GITEA_TOKEN" "$rest"
}

api_contents() {
	# api_contents <repo> <branch> <path>: fetch one path's contents-API
	# document into $SCRATCH/contents.json. Returns 2 when the path does not
	# exist on that branch and 1 on any other transport failure.
	local repo="$1" branch="$2" path="$3"
	local encoded_branch encoded_path http_status
	encoded_branch="$(printf '%s' "$branch" | jq -sRr @uri)"
	encoded_path="$(printf '%s' "$path" | jq -sRr @uri)"
	http_status="$(curl -sS -o "$SCRATCH/contents.json" -w '%{http_code}' -H "$auth_header" \
		"$GITEA_URL/api/v1/repos/$GITEA_OWNER/$repo/contents/$encoded_path?ref=$encoded_branch")" || return 1
	case "$http_status" in
	200) return 0 ;;
	404) return 2 ;;
	*) return 1 ;;
	esac
}

api_read_file() {
	# api_read_file <repo> <branch> <path>: echo a text file's decoded
	# contents. Returns the same codes as api_contents.
	local repo="$1" branch="$2" path="$3"
	local decoded
	api_contents "$repo" "$branch" "$path" || return $?
	decoded="$(jq -r '.content // empty' "$SCRATCH/contents.json" | tr -d '\n' | base64 -d)" || return 1
	printf '%s' "$decoded"
}

api_read_gitlink_sha() {
	# api_read_gitlink_sha <repo> <branch> <submodule_path>: echo the commit sha
	# the gitlink at that path records. Returns 2 when the path does not exist
	# on that branch and 1 when it exists but is not a usable submodule gitlink,
	# so a malformed entry can never be committed into a consumer.
	local repo="$1" branch="$2" path="$3"
	local entry_type sha
	api_contents "$repo" "$branch" "$path" || return $?
	entry_type="$(jq -r '.type // empty' "$SCRATCH/contents.json")"
	[ "$entry_type" = "submodule" ] || return 1
	sha="$(jq -r '.sha // empty' "$SCRATCH/contents.json")"
	case "$sha" in
	'' | *[!0-9a-fA-F]*) return 1 ;;
	esac
	printf '%s' "$sha"
}

resolve_owner_uid() {
	local body
	body="$(curl -sSf -H "$auth_header" "$GITEA_URL/api/v1/users/$GITEA_OWNER")" || return 1
	printf '%s' "$body" | jq -r '.id // empty'
}

list_consumer_candidates() {
	# Print "name<TAB>default_branch<TAB>clone_url" for every repo owned by
	# GITEA_OWNER, paginating repos/search until a short page is returned.
	local page=1
	local limit=50
	while :; do
		local body
		body="$(curl -sSf -H "$auth_header" \
			"$GITEA_URL/api/v1/repos/search?uid=$OWNER_UID&exclusive=true&limit=$limit&page=$page")" || return 1
		local count
		count="$(printf '%s' "$body" | jq -r '.data | length // empty' 2>/dev/null)" || return 1
		case "$count" in
		'' | *[!0-9]*) return 1 ;;
		esac
		[ "$count" -eq 0 ] && break
		printf '%s' "$body" | jq -r '.data[] | [.name, .default_branch, .clone_url] | @tsv' || return 1
		[ "$count" -lt "$limit" ] && break
		page=$((page + 1))
	done
}

bump_gitlink() {
	# bump_gitlink <repo_dir> <branch> <newline_separated_paths>: create the
	# bump branch and commit the new sha into every listed gitlink.
	local repo_dir="$1" branch="$2" paths="$3"
	local path
	(
		cd "$repo_dir" || exit 1
		git config user.name claude-code
		git config user.email claude-code@noreply.sticktoitive.net
		git checkout --quiet -b "$branch"
		while IFS= read -r path; do
			[ -n "$path" ] || continue
			# --cacheinfo writes the gitlink straight into the index. The new
			# MFTLib commit is absent from this shallow clone and never needs
			# to be present, because nothing here checks the submodule out.
			git update-index --cacheinfo "$GITLINK_MODE,$NEW_SHA,$path" || exit 1
		done <<<"$paths"
		git commit --quiet -m "chore: bump MFTLib pin to $SHORT_SHA"
	)
}

open_pull_request() {
	# open_pull_request <repo> <base_branch> <newline_separated_paths>
	local repo="$1" base_branch="$2" paths="$3"
	local detail pr_body pr_status pr_response
	detail="${paths//$'\n'/, }"
	pr_body="$(jq -n --arg title "chore: bump MFTLib pin to $SHORT_SHA" \
		--arg head "$BRANCH" --arg base "$base_branch" \
		--arg body "Automated update of the $detail submodule gitlink to $NEW_SHA by sync-consumers." \
		'{title: $title, head: $head, base: $base, body: $body}')"
	pr_response="$SCRATCH/pr_response.json"
	pr_status="$(curl -sS -o "$pr_response" -w '%{http_code}' -X POST \
		-H "$auth_header" -H "Content-Type: application/json" \
		--data-binary "$pr_body" \
		"$GITEA_URL/api/v1/repos/$GITEA_OWNER/$repo/pulls")"

	case "$pr_status" in
	200 | 201)
		record "$repo" "updated" "PR opened ($(jq -r '.html_url // "no url"' "$pr_response"))"
		return 0
		;;
	409)
		record "$repo" "updated" "PR already open, force-push refreshed it"
		return 0
		;;
	*)
		record "$repo" "failed" "PR create returned $pr_status"
		return 1
		;;
	esac
}

update_consumer() {
	# update_consumer <repo> <default_branch> <clone_url> <newline_separated_paths>
	local repo="$1" branch="$2" clone_url="$3" paths="$4"
	local repo_dir="$SCRATCH/$repo"
	rm -rf "$repo_dir"

	local push_url
	push_url="$(authed_url "$clone_url")"

	# No --recurse-submodules: the consumer's submodule content is irrelevant
	# to a gitlink bump, and fetching it would pull all of MFTLib's history.
	if ! git clone --quiet --depth 1 --branch "$branch" "$push_url" "$repo_dir" >/dev/null 2>&1; then
		record "$repo" "failed" "clone of $branch failed"
		return 1
	fi

	if ! bump_gitlink "$repo_dir" "$BRANCH" "$paths"; then
		record "$repo" "failed" "local gitlink commit failed"
		return 1
	fi

	if ! git -C "$repo_dir" push --quiet --force "$push_url" "HEAD:refs/heads/$BRANCH" >/dev/null 2>&1; then
		record "$repo" "failed" "force-push to $BRANCH failed"
		return 1
	fi

	open_pull_request "$repo" "$branch" "$paths"
}

process_repo() {
	local repo="$1" branch="$2" clone_url="$3"
	[ "$repo" = "$SELF_REPO" ] && return 0
	candidates_seen=$((candidates_seen + 1))

	local gitmodules exit_code
	gitmodules="$(api_read_file "$repo" "$branch" "$GITMODULES_FILE")"
	exit_code=$?
	if [ "$exit_code" -eq 2 ]; then
		return 0 # no submodules at all, so nothing pins MFTLib
	fi
	if [ "$exit_code" -ne 0 ]; then
		attempted=$((attempted + 1))
		failed=$((failed + 1))
		record "$repo" "failed" "could not read $GITMODULES_FILE"
		return 0
	fi

	local paths
	paths="$(mftlib_submodule_paths "$SELF_REPO_URL" "$clone_url" "$gitmodules")"
	if [ -z "$paths" ]; then
		return 0 # has submodules, none of them MFTLib
	fi
	matched=$((matched + 1))

	local path current_sha stale_paths=""
	while IFS= read -r path; do
		[ -n "$path" ] || continue
		current_sha="$(api_read_gitlink_sha "$repo" "$branch" "$path")"
		exit_code=$?
		if [ "$exit_code" -ne 0 ]; then
			attempted=$((attempted + 1))
			failed=$((failed + 1))
			record "$repo" "failed" "$path is declared in $GITMODULES_FILE but is not a readable gitlink"
			return 0
		fi
		if [ "$current_sha" != "$NEW_SHA" ]; then
			stale_paths+="$path"$'\n'
		fi
	done <<<"$paths"

	if [ -z "$stale_paths" ]; then
		record "$repo" "skipped" "already pinned to $SHORT_SHA"
		return 0
	fi
	stale_paths="${stale_paths%$'\n'}"

	attempted=$((attempted + 1))
	if ! update_consumer "$repo" "$branch" "$clone_url" "$stale_paths"; then
		failed=$((failed + 1))
	fi
}

main() {
	NEW_SHA="${1:?usage: sync_consumers.sh <new-sha>}"
	if [[ ! "$NEW_SHA" =~ ^[0-9a-fA-F]{40}$ ]]; then
		echo "sync_consumers.sh: invalid sha '$NEW_SHA' (must be a 40-char hex string)" >&2
		exit 1
	fi
	SHORT_SHA="${NEW_SHA:0:12}"
	SELF_REPO_URL="$GITEA_URL/$GITEA_OWNER/$SELF_REPO"
	: "${GITEA_TOKEN:?GITEA_TOKEN env var is required}"

	for tool in curl git jq base64; do
		if ! command -v "$tool" >/dev/null 2>&1; then
			echo "sync_consumers.sh: required tool '$tool' not found on PATH" >&2
			exit 1
		fi
	done

	SCRATCH="$(mktemp -d)"
	trap 'rm -rf "$SCRATCH"' EXIT
	auth_header="Authorization: token $GITEA_TOKEN"

	SUMMARY=()
	candidates_seen=0
	matched=0
	attempted=0
	failed=0

	if ! OWNER_UID="$(resolve_owner_uid)" || [ -z "$OWNER_UID" ]; then
		echo "sync_consumers.sh: could not resolve a Gitea user id for owner '$GITEA_OWNER'" >&2
		exit 1
	fi

	local candidates
	if ! candidates="$(list_consumer_candidates)"; then
		echo "sync_consumers.sh: failed to list consumer candidate repositories from Gitea" >&2
		exit 1
	fi
	if [ -z "$candidates" ]; then
		echo "sync_consumers.sh: repos/search returned no repos for owner $GITEA_OWNER" >&2
		exit 1
	fi

	local name default_branch clone_url
	while IFS=$'\t' read -r name default_branch clone_url; do
		[ -z "$name" ] && continue
		process_repo "$name" "$default_branch" "$clone_url"
	done <<<"$candidates"

	echo
	echo "sync-consumers summary for sha $NEW_SHA:"
	printf '%-28s %-18s %s\n' "REPO" "RESULT" "DETAIL"
	for row in ${SUMMARY[@]+"${SUMMARY[@]}"}; do
		echo "$row"
	done
	echo "examined $candidates_seen repos, matched $matched consumers, attempted $attempted bumps, $failed failed"

	if [ "$matched" -eq 0 ]; then
		echo "sync_consumers.sh: no repository owned by $GITEA_OWNER pins $SELF_REPO with a submodule gitlink" >&2
		echo "sync_consumers.sh: examined $candidates_seen candidates with $failed read failures; refusing to report success on zero matches" >&2
		exit 1
	fi

	# Any failure is a failure, not just total failure: a consumer whose bump
	# errored is still pinned to an older MFTLib, and a green run would hide it.
	if [ "$failed" -gt 0 ]; then
		echo "sync_consumers.sh: $failed of $attempted attempted repos failed and are still pinned to an older MFTLib" >&2
		exit 1
	fi

	exit 0
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
	main "$@"
fi
