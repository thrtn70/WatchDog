#!/usr/bin/env bash
# Pre-push quality gate for WatchDog.
# Runs format check + lightweight build via SSH to the Windows VM.
# Heavy checks (warnaserror full build, tests, reviewer agents) run in CI.
#
# Bypass in an emergency: SKIP_PREPUSH=1 git push
#
# Exits non-zero on any failure; git aborts the push.

set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
cd "$REPO_ROOT"

if [[ "${SKIP_PREPUSH:-0}" == "1" ]]; then
    echo "[pre-push] SKIP_PREPUSH=1 — bypassing gate. Use sparingly." >&2
    exit 0
fi

readonly SSH_HOST="win11"
readonly REMOTE_PATH='C:\Code\tikrclpr'

# Color helpers (no-ops if stderr is not a tty)
if [[ -t 2 ]]; then
    C_BLUE=$'\033[34m'; C_GREEN=$'\033[32m'; C_RED=$'\033[31m'
    C_DIM=$'\033[2m';   C_RESET=$'\033[0m'
else
    C_BLUE=''; C_GREEN=''; C_RED=''; C_DIM=''; C_RESET=''
fi
log()  { printf '%s▶%s %s\n' "$C_BLUE"  "$C_RESET" "$*" >&2; }
ok()   { printf '%s✓%s %s\n' "$C_GREEN" "$C_RESET" "$*" >&2; }
fail() { printf '%s✗%s %s\n' "$C_RED"   "$C_RESET" "$*" >&2; }

# Strip the OpenSSH 10 post-quantum advisory that clutters every command.
filter_pq_warning() {
    grep -v -E '^\*\* (WARNING: connection|This session|The server may need)' || true
}

# Preflight: SSH must be reachable. Fail closed; document escape hatch.
preflight() {
    if ! ssh -o BatchMode=yes -o ConnectTimeout=5 "$SSH_HOST" 'exit' 2>/dev/null; then
        fail "Cannot reach SSH host '$SSH_HOST' — pre-push gate cannot run."
        echo "  Start the VM, or bypass for this push with: SKIP_PREPUSH=1 git push" >&2
        exit 1
    fi
}

run_remote() {
    ssh "$SSH_HOST" "$1" 2> >(filter_pq_warning >&2)
}

run_step() {
    local name="$1"; shift
    log "$name"
    if "$@"; then
        ok "$name"
        return 0
    else
        fail "$name"
        return 1
    fi
}

main() {
    log "WatchDog pre-push gate (SSH to ${SSH_HOST})"
    preflight

    local fails=0

    run_step "dotnet format --verify-no-changes" \
        run_remote "cd $REMOTE_PATH; dotnet format --verify-no-changes --no-restore" \
        || fails=$((fails + 1))

    run_step "dotnet build WatchDog.Core (lightweight)" \
        run_remote "cd $REMOTE_PATH; dotnet build src\\WatchDog.Core\\WatchDog.Core.csproj -c Release --nologo" \
        || fails=$((fails + 1))

    if [[ "$fails" -gt 0 ]]; then
        fail "Pre-push gate blocked push (${fails} check(s) failed)."
        echo "  Heavy checks (full build, tests, reviewer agents) run in CI on PR." >&2
        echo "  Bypass for emergencies: SKIP_PREPUSH=1 git push" >&2
        exit 1
    fi

    ok "Pre-push gate: all clear."
}

main "$@"
