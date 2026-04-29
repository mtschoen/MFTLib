#!/usr/bin/env bash
# dump-volume.sh — extract $MFT from an NTFS device via ntfscat (sudo) and
# invoke mft-cli on the resulting file.
#
# Usage:
#   scripts/dump-volume.sh dump
#   scripts/dump-volume.sh search <pattern>
#
# Environment overrides:
#   DEVICE      block device to read (default: /dev/sda1)
#   DUMP_PATH   where to cache the extracted $MFT (default: /tmp/<basename>.mft)
#   FORCE=1     re-extract even if DUMP_PATH already exists
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUILD_DIR="$ROOT/build/linux"
CLI="$BUILD_DIR/tools/mft-cli"

DEVICE="${DEVICE:-/dev/sda1}"
DUMP_PATH="${DUMP_PATH:-/tmp/$(basename "$DEVICE").mft}"

if [ "$#" -lt 1 ]; then
    echo "Usage: $0 {dump|search [pattern]}" >&2
    exit 2
fi

if ! command -v ntfscat >/dev/null 2>&1; then
    echo "error: ntfscat not found. Install ntfs-3g (pacman -S ntfs-3g | apt install ntfs-3g)." >&2
    exit 1
fi

if [ ! -x "$CLI" ]; then
    echo "==> mft-cli not built; running scripts/build-linux.sh"
    "$ROOT/scripts/build-linux.sh" >/dev/null
fi

if [ ! -f "$DUMP_PATH" ] || [ "${FORCE:-0}" = "1" ]; then
    RAW_PATH="${DUMP_PATH}.raw"
    echo "==> extracting \$MFT from $DEVICE → $DUMP_PATH (sudo required for raw block read)"
    # ntfscat refuses by default if the volume is mounted. --force is safe for a
    # read-only extraction on an idle volume; the risk is reading inconsistent
    # state if writes are happening concurrently, which is acceptable here.
    sudo ntfscat --force "$DEVICE" '$MFT' > "$RAW_PATH"
    # ntfscat --force prints exactly "Forced to continue.\n" (20 bytes) to
    # stdout before the actual $MFT data, so we always strip the first 20 bytes.
    # Without --force (idle/unmounted volumes), there's no prefix and this is
    # still safe because the FILE magic check below catches the no-prefix case.
    HEAD4=$(head -c 4 "$RAW_PATH")
    if [ "$HEAD4" = "FILE" ]; then
        mv "$RAW_PATH" "$DUMP_PATH"
    else
        echo "==> stripping 20-byte ntfscat warning prefix"
        tail -c +21 "$RAW_PATH" > "$DUMP_PATH"
        rm -f "$RAW_PATH"
    fi
    SIZE_MB=$(stat -c %s "$DUMP_PATH" | awk '{printf "%.1f", $1/1048576}')
    echo "==> dumped ${SIZE_MB} MB"
else
    SIZE_MB=$(stat -c %s "$DUMP_PATH" | awk '{printf "%.1f", $1/1048576}')
    echo "==> using cached dump at $DUMP_PATH (${SIZE_MB} MB; set FORCE=1 to re-extract)"
fi

echo
SUBCMD="$1"; shift
case "$SUBCMD" in
    dump|search)
        # mft-cli expects: <subcmd> <path> [pattern...]
        # We inject DUMP_PATH between the subcommand and any remaining args.
        exec env LD_LIBRARY_PATH="$BUILD_DIR" "$CLI" "$SUBCMD" "$DUMP_PATH" "$@"
        ;;
    *)
        echo "unknown subcommand: $SUBCMD (expected dump|search)" >&2
        exit 2
        ;;
esac
