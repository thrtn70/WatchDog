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

    ln -s "../../scripts/${hook_name}.sh" "$link"
    echo "  ✓ $hook_name → scripts/${hook_name}.sh"
}

echo "Installing git hooks…"
install_hook "pre-push"
echo "Done."
