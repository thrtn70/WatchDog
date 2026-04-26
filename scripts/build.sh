#!/usr/bin/env bash
# Build WatchDog inside the Windows VM via SSH.
#
# Usage:
#   ./scripts/build.sh                       # Release build of WatchDog.App
#   ./scripts/build.sh -c Debug              # Debug build
#   ./scripts/build.sh test                  # Run dotnet test
#   ./scripts/build.sh run                   # NOT supported — WPF needs interactive desktop
#   ./scripts/build.sh clean                 # dotnet clean
#   ./scripts/build.sh shell                 # Open an interactive PowerShell on the VM
#
# Requirements:
#   - SSH alias 'win11' configured in ~/.ssh/config
#   - C:\Code\tikrclpr symlinked to the Mac source folder inside the VM
#
# Exits with the dotnet command's exit code (0 = success, non-zero = failure).

set -euo pipefail

readonly SSH_HOST="win11"
readonly REMOTE_PATH='C:\Code\tikrclpr'
readonly DEFAULT_PROJECT='src\WatchDog.App\WatchDog.App.csproj'

# Color helpers (no-ops if stderr is not a tty)
if [[ -t 2 ]]; then
    readonly C_BLUE=$'\033[34m'
    readonly C_GREEN=$'\033[32m'
    readonly C_RED=$'\033[31m'
    readonly C_DIM=$'\033[2m'
    readonly C_RESET=$'\033[0m'
else
    readonly C_BLUE='' C_GREEN='' C_RED='' C_DIM='' C_RESET=''
fi

log()  { printf '%s▶%s %s\n' "$C_BLUE" "$C_RESET" "$*" >&2; }
ok()   { printf '%s✓%s %s\n' "$C_GREEN" "$C_RESET" "$*" >&2; }
fail() { printf '%s✗%s %s\n' "$C_RED" "$C_RESET" "$*" >&2; }

# Strip the OpenSSH 10 post-quantum advisory that sshd 9.x triggers.
# It's purely informational and clutters every build's output.
filter_pq_warning() {
    grep -v -E '^\*\* (WARNING: connection|This session|The server may need)' || true
}

# Verify SSH connectivity before sending real work.
preflight() {
    if ! ssh -o BatchMode=yes -o ConnectTimeout=5 "$SSH_HOST" 'exit' 2>/dev/null; then
        fail "Cannot reach SSH host '$SSH_HOST'."
        echo "  Check that the VM is running and ~/.ssh/config has a 'Host $SSH_HOST' block." >&2
        exit 1
    fi
}

run_remote() {
    local remote_cmd="$1"
    log "remote: ${C_DIM}${remote_cmd}${C_RESET}"
    # Pipe stderr through PQ filter; preserve real exit code via PIPESTATUS.
    ssh "$SSH_HOST" "$remote_cmd" 2> >(filter_pq_warning >&2)
}

cmd_build() {
    local config="${1:-Release}"
    local project="${2:-$DEFAULT_PROJECT}"
    preflight
    run_remote "cd $REMOTE_PATH; dotnet build $project -c $config"
    ok "Build complete (${config})"
}

cmd_test() {
    preflight
    run_remote "cd $REMOTE_PATH; dotnet test --nologo"
    ok "Tests complete"
}

cmd_clean() {
    preflight
    run_remote "cd $REMOTE_PATH; dotnet clean"
    ok "Clean complete"
}

cmd_shell() {
    preflight
    log "Opening interactive PowerShell on $SSH_HOST (Ctrl-D to exit)"
    ssh -t "$SSH_HOST" "cd $REMOTE_PATH; powershell"
}

main() {
    local subcommand="${1:-build}"
    shift || true

    case "$subcommand" in
        build) cmd_build "${1:-Release}" "${2:-$DEFAULT_PROJECT}" ;;
        debug) cmd_build "Debug" "${1:-$DEFAULT_PROJECT}" ;;
        test)  cmd_test ;;
        clean) cmd_clean ;;
        shell) cmd_shell ;;
        run)
            fail "Cannot run WPF app via SSH — WPF requires an interactive desktop session."
            echo "  Open Windows Terminal inside the VM and run:" >&2
            echo "    cd C:\\Code\\tikrclpr; dotnet run --project src\\WatchDog.App" >&2
            exit 2
            ;;
        -c|--config)
            cmd_build "$1" "${2:-$DEFAULT_PROJECT}"
            ;;
        -h|--help|help)
            sed -n '2,/^$/p' "$0" | sed 's/^# \?//'
            ;;
        *)
            fail "Unknown subcommand: $subcommand"
            echo "  Run '$0 --help' for usage." >&2
            exit 2
            ;;
    esac
}

main "$@"
