# SPDX-License-Identifier: GPL-3.0-or-later
#
# Shared helpers, sourced by the other scripts. Not executable on its own.
#
# The help text IS the script's own header comment. Every one of these already
# opens with what it does and how to call it, and a usage string maintained
# separately from that is a usage string that goes stale -- so this prints the
# header rather than repeating it.

# Print a script's leading comment block, minus the shebang and the licence.
wwtb_usage() {
    awk '
        NR == 1 && /^#!/ { next }
        /^#/ {
            if ($0 ~ /SPDX-License-Identifier/) next
            sub(/^# ?/, "")
            print
            next
        }
        { exit }
    ' "$1"
}

# Handle --help before anything else, including before the checks that would
# otherwise refuse to run. Asking a script what it takes must not require
# already having set up what it needs.
wwtb_help() {
    local script="$1"
    shift

    for argument in "$@"; do
        case "$argument" in
            --help | -h)
                wwtb_usage "$script"
                exit 0
                ;;
        esac
    done
}

# Refuse an argument rather than ignoring it.
#
# Every one of these scripts used to test its flag with a bare equality, so
# `--instal` built without installing and said nothing about why -- the failure
# being that it did exactly what you asked, quietly, having understood none of
# it.
wwtb_unknown() {
    local script="$1" argument="$2"

    echo "error: unknown argument: $argument" >&2
    echo >&2
    wwtb_usage "$script" >&2
    exit 2
}

# The Apple Developer Team ID, from the environment or from the one place it is
# written down.
#
# Announced when it falls back, on purpose: a default that is silent is a
# default that signs somebody else's clone with this team and fails two minutes
# later on a certificate error that never mentions it.
wwtb_team() {
    if [ -n "${DEVELOPMENT_TEAM:-}" ]; then
        return 0
    fi

    local root props
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
    props="$root/Directory.Build.props"

    DEVELOPMENT_TEAM=$(sed -n 's/.*<DevelopmentTeam[^>]*>\([^<]*\)<.*/\1/p' "$props" | head -1)

    if [ -z "$DEVELOPMENT_TEAM" ]; then
        echo "error: DEVELOPMENT_TEAM is not set and none is in Directory.Build.props." >&2
        echo "       Find yours with scripts/team-id.sh" >&2
        exit 1
    fi

    export DEVELOPMENT_TEAM
    echo "==> Team           $DEVELOPMENT_TEAM  (from Directory.Build.props; set DEVELOPMENT_TEAM to override)"
}
