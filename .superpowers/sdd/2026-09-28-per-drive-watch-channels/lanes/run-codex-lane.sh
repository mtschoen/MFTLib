#!/usr/bin/env bash
# Runs one headless codex lane in a task worktree and records its exit code.
# Usage: run-codex-lane.sh <task id> <absolute worktree path> [model] [budget]
# The prompt is <worktree>/.superpowers/sdd/<plan>/lanes/<task id>-dispatch.md; the log lands beside it.
set -u
task_id="$1"
worktree="$2"
model="${3:-${CODEX_LANE_MODEL:-gpt-5.6-terra}}"
budget="${4:-150m}"
plan_directory="$worktree/.superpowers/sdd/2026-09-28-per-drive-watch-channels"
prompt_file="$plan_directory/lanes/$task_id-dispatch.md"
log_file="$plan_directory/lanes/$task_id.log"

if [ ! -f "$prompt_file" ]; then
  echo "missing prompt file $prompt_file" >&2
  exit 2
fi

cd "$worktree" || exit 2
timeout "$budget" codex exec -m "$model" -c model_reasoning_effort=high \
  -s workspace-write -c sandbox_workspace_write.network_access=true \
  --cd "$(cygpath -m "$worktree")" - < "$prompt_file" > "$log_file" 2>&1
echo "EXIT $?" >> "$log_file"
tail -20 "$log_file"
