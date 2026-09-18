#!/usr/bin/env bash
# Submodule matching for the MFTLib consumer fan-out. Sourced by
# scripts/sync_consumers.sh and exercised directly by
# scripts/sync_consumers.tests.sh.
#
# Everything here is pure: no network, no git invocation, no global state. The
# only question these functions answer is whether a repository's .gitmodules
# declares a submodule that points at the MFTLib repository being published.
# Keeping that apart from the Gitea transport in sync_consumers.sh is what
# makes the consumer rule testable against fixture text alone.

normalize_repo_url() {
	# normalize_repo_url <url>: reduce a repository URL to the comparable form
	# host/owner/repo by dropping the scheme, any userinfo, a trailing slash
	# and a trailing .git, then lowercasing. Both sides of a consumer match go
	# through this, so the comparison never depends on how either was spelled.
	# An scp-style git@host:path url keeps its colon and therefore never
	# matches; that fails closed, and the caller's zero-match guard reports it.
	local url="$1"
	case "$url" in
	*://*) url="${url#*://}" ;;
	esac
	case "$url" in
	*@*) url="${url#*@}" ;;
	esac
	url="${url%/}"
	url="${url%.git}"
	printf '%s' "$url" | tr '[:upper:]' '[:lower:]'
}

resolve_submodule_url() {
	# resolve_submodule_url <superproject_url> <submodule_url>: echo the URL a
	# relative submodule url points at. This mirrors git's own rule, verified
	# against `git submodule init` output: each leading ../ removes one
	# trailing path component from the superproject url, ./ appends to the
	# superproject url unchanged, and an absolute url is returned as-is. A bare
	# name stays unresolved, which is also what git does with it.
	local superproject="${1%/}"
	local submodule_url="$2"
	local resolved_prefix=0

	case "$submodule_url" in
	*://* | /*)
		printf '%s' "$submodule_url"
		return 0
		;;
	esac

	while [[ "$submodule_url" == ../* ]]; do
		submodule_url="${submodule_url#../}"
		superproject="${superproject%/*}"
		resolved_prefix=1
	done
	if [[ "$submodule_url" == ./* ]]; then
		submodule_url="${submodule_url#./}"
		resolved_prefix=1
	fi

	if [ "$resolved_prefix" -eq 1 ]; then
		printf '%s/%s' "$superproject" "$submodule_url"
	else
		printf '%s' "$submodule_url"
	fi
}

parse_gitmodules() {
	# parse_gitmodules: read a .gitmodules document on stdin and print one
	# "path<TAB>url" line for every [submodule] section declaring both keys.
	awk '
		function flush_section() {
			if (in_submodule && path != "" && url != "") printf "%s\t%s\n", path, url
			in_submodule = 0
			path = ""
			url = ""
		}
		/^[[:space:]]*[#;]/ { next }
		/^[[:space:]]*\[/ {
			flush_section()
			in_submodule = (tolower($0) ~ /^[[:space:]]*\[submodule[[:space:]]/)
			next
		}
		{
			separator = index($0, "=")
			if (separator == 0) next
			key = substr($0, 1, separator - 1)
			value = substr($0, separator + 1)
			gsub(/^[[:space:]]+|[[:space:]]+$/, "", key)
			gsub(/^[[:space:]]+|[[:space:]]+$/, "", value)
			if (value ~ /^".*"$/) value = substr(value, 2, length(value) - 2)
			key = tolower(key)
			if (key == "path") path = value
			else if (key == "url") url = value
		}
		END { flush_section() }
	'
}

mftlib_submodule_paths() {
	# mftlib_submodule_paths <mftlib_repo_url> <superproject_url>
	#     <gitmodules_text>: print the path of every submodule whose url
	# resolves to that MFTLib repository, one per line. Prints nothing when the
	# superproject pins nothing from here, which is what makes it not a
	# consumer. The submodule path comes from .gitmodules rather than being
	# assumed to be external/MFTLib.
	local mftlib_repo_url="$1"
	local superproject="$2"
	local gitmodules_text="$3"
	local expected resolved path url
	expected="$(normalize_repo_url "$mftlib_repo_url")"

	while IFS=$'\t' read -r path url; do
		[ -n "$path" ] && [ -n "$url" ] || continue
		resolved="$(resolve_submodule_url "$superproject" "$url")"
		if [ "$(normalize_repo_url "$resolved")" = "$expected" ]; then
			printf '%s\n' "$path"
		fi
	done <<<"$(printf '%s' "$gitmodules_text" | parse_gitmodules)"
}
