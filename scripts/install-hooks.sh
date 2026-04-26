#!/usr/bin/env bash
# Install git hooks for WatchDog. Run once after cloning.
#
# Usage:
#   ./scripts/install-hooks.sh

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
HOOKS_DIR="$REPO_ROOT/.git/hooks"
SCRIPTS_DIR="$REPO_ROOT/scripts"

mkdir -p "$HOOKS_DIR"

install_hook() {
    local hook_name="$1"
    local target="$SCRIPTS_DIR/${hook_name}.sh"
    local link="$HOOKS_DIR/$hook_name"

    if [[ ! -f "$target" ]]; then
        echo "  ✗ $hook_name: source missing at $target" >&2
        return 1
    fi

    chmod +x "$target"

    if [[ -e "$link" || -L "$link" ]]; then
        rm -f "$link"
    fi

    # Absolute path so the symlink survives `git worktree` (where .git/hooks/
    # may be three levels deep instead of two).
    ln -s "$target" "$link"
    echo "  ✓ $hook_name → $target"
}

echo "Installing git hooks…"
install_hook "pre-push"
echo "Done."
