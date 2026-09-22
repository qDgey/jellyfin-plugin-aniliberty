#!/usr/bin/env bash
# Install aniliberty-tree: the script, its config and an hourly systemd timer.
#
#   sudo ./scripts/install.sh --source /data/anime
#   curl -fsSL https://raw.githubusercontent.com/qDgey/jellyfin-plugin-aniliberty/main/scripts/install.sh | sudo bash -s -- --source /data/anime
#
# Options (all optional; without --source you are asked interactively):
#   --source DIR     archive root, repeatable (several disks/folders)
#   --series DIR     series tree for the "Shows" library   (default /srv/aniliberty-series)
#   --movies DIR     movie tree for the "Movies" library    (default /srv/aniliberty-movies)
#   --user USER      user Jellyfin runs as                  (default jellyfin)
#   --depth N        grouping levels to descend (years, categories...) (default 4)
#   --exclude REGEX  folder/file names to skip
#   --prefer CODEC   avc | hevc: default version when both exist (default avc)
#   --no-run         don't build the trees now
#   --no-timer       don't enable the hourly timer
#   --yes            don't ask for confirmation
#   --uninstall      remove script, units and timer (keeps the config and the trees)
set -euo pipefail

REPO_RAW="${ANILIBERTY_REPO_RAW:-https://raw.githubusercontent.com/qDgey/jellyfin-plugin-aniliberty/main/scripts}"
BIN=/usr/local/bin/aniliberty-tree.py
CONF=/etc/aniliberty-tree.conf
UNIT_DIR=/etc/systemd/system
DROPIN="$UNIT_DIR/aniliberty-tree.service.d"

SOURCES=() SERIES="" MOVIES="" RUN_USER="" DEPTH="" EXCLUDE="" PREFER="" RUN=1 TIMER=1 YES=0 UNINSTALL=0

die() { echo "error: $*" >&2; exit 1; }
say() { echo "==> $*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --source) SOURCES+=("${2:?}"); shift 2 ;;
    --series) SERIES="${2:?}"; shift 2 ;;
    --movies) MOVIES="${2:?}"; shift 2 ;;
    --user) RUN_USER="${2:?}"; shift 2 ;;
    --depth) DEPTH="${2:?}"; shift 2 ;;
    --exclude) EXCLUDE="${2:?}"; shift 2 ;;
    --prefer) PREFER="${2:?}"; shift 2 ;;
    --no-run) RUN=0; shift ;;
    --no-timer) TIMER=0; shift ;;
    --yes|-y) YES=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) grep -E '^#( |$)' "${BASH_SOURCE[0]:-$0}" 2>/dev/null | sed -n '2,19p' | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown option $1 (see --help)" ;;
  esac
done

[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
command -v python3 >/dev/null || die "python3 is required"
HAVE_SYSTEMD=0; [ -d /run/systemd/system ] && HAVE_SYSTEMD=1

if [ "$UNINSTALL" -eq 1 ]; then
  if [ "$HAVE_SYSTEMD" -eq 1 ]; then
    systemctl disable --now aniliberty-tree.timer 2>/dev/null || true
    rm -rf "$UNIT_DIR/aniliberty-tree.service" "$UNIT_DIR/aniliberty-tree.timer" "$DROPIN"
    systemctl daemon-reload
  fi
  rm -f "$BIN"
  say "removed. Kept $CONF and the symlink trees (delete them by hand if you no longer need them)."
  exit 0
fi

# Values from an existing config are the defaults on reinstall.
if [ -f "$CONF" ]; then
  # shellcheck disable=SC1090
  set -a; . "$CONF"; set +a
fi
TTY=0; { [ -t 0 ] || { : </dev/tty; } 2>/dev/null; } && TTY=1
ask() { # ask VAR "question" default
  local answer=""
  if [ "$YES" -eq 0 ] && [ "$TTY" -eq 1 ]; then
    read -r -p "$2 [$3]: " answer </dev/tty || true
  fi
  printf -v "$1" '%s' "${answer:-$3}"
}

if [ ${#SOURCES[@]} -eq 0 ]; then
  ask src "Archive folders with AniLiberty torrents, separated by ':'" "${ANILIBERTY_SOURCES:-${ANILIBERTY_SOURCE:-/media/aniliberty}}"
  IFS=: read -r -a SOURCES <<<"$src"
fi
[ -n "$SERIES" ] || ask SERIES "Series tree (Jellyfin \"Shows\" library)" "${ANILIBERTY_SERIES:-/srv/aniliberty-series}"
[ -n "$MOVIES" ] || ask MOVIES "Movie tree (Jellyfin \"Movies\" library)" "${ANILIBERTY_MOVIES:-/srv/aniliberty-movies}"
if [ -z "$RUN_USER" ]; then
  default_user=jellyfin; id jellyfin >/dev/null 2>&1 || default_user="$(logname 2>/dev/null || echo root)"
  ask RUN_USER "User Jellyfin runs as" "$default_user"
fi
DEPTH="${DEPTH:-${ANILIBERTY_MAX_DEPTH:-4}}"
EXCLUDE="${EXCLUDE:-${ANILIBERTY_EXCLUDE:-}}"
PREFER="${PREFER:-${ANILIBERTY_PREFER:-avc}}"
case "$PREFER" in avc|hevc) ;; *) die "--prefer must be avc or hevc" ;; esac

id "$RUN_USER" >/dev/null 2>&1 || die "user $RUN_USER does not exist"
as_user() { if [ "$RUN_USER" = root ]; then "$@"; else runuser -u "$RUN_USER" -- "$@"; fi; }
for s in "${SOURCES[@]}" "$SERIES" "$MOVIES" "$EXCLUDE"; do
  case "$s" in *"'"*) die "values must not contain single quotes: $s" ;; esac
done
for s in "${SOURCES[@]}"; do
  [ -d "$s" ] || die "source $s is not a directory"
  as_user test -r "$s" -a -x "$s" || die "$RUN_USER can't read $s"
done

say "sources: ${SOURCES[*]}"
say "series tree: $SERIES, movie tree: $MOVIES, user: $RUN_USER, depth: $DEPTH, default codec: $PREFER${EXCLUDE:+, exclude: $EXCLUDE}"

# --- files -----------------------------------------------------------------------------------------------
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" 2>/dev/null && pwd || true)"
fetch() { # fetch NAME DEST
  if [ -n "$HERE" ] && [ -f "$HERE/$1" ]; then
    install -m "$3" "$HERE/$1" "$2"
  else
    command -v curl >/dev/null || die "curl is required to download $1"
    curl -fsSL "$REPO_RAW/$1" -o "$2.tmp" && install -m "$3" "$2.tmp" "$2" && rm -f "$2.tmp"
  fi
}
fetch aniliberty-tree.py "$BIN" 755

[ -f "$CONF" ] && cp -p "$CONF" "$CONF.bak"
# Single-quoted values: understood both by systemd's EnvironmentFile and by `. file` in a shell.
{
  echo "# aniliberty-tree configuration (written by install.sh; see aniliberty-tree.conf in the repository)"
  echo "ANILIBERTY_SOURCES='$(IFS=:; echo "${SOURCES[*]}")'"
  echo "ANILIBERTY_SERIES='$SERIES'"
  echo "ANILIBERTY_MOVIES='$MOVIES'"
  echo "ANILIBERTY_MAX_DEPTH='$DEPTH'"
  echo "ANILIBERTY_EXCLUDE='$EXCLUDE'"
  echo "ANILIBERTY_PREFER='$PREFER'"
} >"$CONF"
chmod 644 "$CONF"
say "config: $CONF"

for d in "$SERIES" "$MOVIES"; do
  mkdir -p "$d"
  chown "$RUN_USER": "$d"
done

# --- first build -------------------------------------------------------------------------------------------
# Run the script as the Jellyfin user with the config loaded (values may contain spaces).
run_tree() { as_user bash -c 'set -a; . "$0"; set +a; exec python3 "$1" "${@:2}"' "$CONF" "$BIN" "$@"; }
if [ "$RUN" -eq 1 ]; then
  say "dry run:"
  run_tree --dry-run --list 6
  if [ "$YES" -eq 0 ] && [ "$TTY" -eq 1 ]; then
    read -r -p "Build the trees now? [Y/n]: " go </dev/tty || true
    case "${go:-y}" in [nN]*) RUN=0 ;; esac
  fi
  if [ "$RUN" -eq 1 ]; then
    say "building…"
    run_tree
  fi
fi

# --- timer -------------------------------------------------------------------------------------------------
if [ "$HAVE_SYSTEMD" -eq 1 ]; then
  fetch aniliberty-tree.service "$UNIT_DIR/aniliberty-tree.service" 644
  fetch aniliberty-tree.timer "$UNIT_DIR/aniliberty-tree.timer" 644
  mkdir -p "$DROPIN"
  {
    echo "[Unit]"
    for s in "${SOURCES[@]}"; do echo "RequiresMountsFor=$s"; done
    echo "[Service]"
    echo "User=$RUN_USER"
  } >"$DROPIN/local.conf"
  systemctl daemon-reload
  if [ "$TIMER" -eq 1 ]; then
    systemctl enable --now aniliberty-tree.timer >/dev/null
    say "timer enabled: $(systemctl list-timers aniliberty-tree.timer --no-legend | awk '{print "next run " $1 " " $2 " " $3}')"
  fi
else
  say "no systemd: add to $RUN_USER's crontab (crontab -u $RUN_USER -e):"
  echo "  17 * * * * set -a; . $CONF; set +a; python3 $BIN"
fi

cat <<EOF

Done. In Jellyfin create the libraries (or change their folders):
  Shows  → $SERIES
  Movies → $MOVIES
If Jellyfin runs in Docker, mount the archive and both trees into the container at the SAME paths.
Rebuild now:  systemctl start aniliberty-tree.service
Dry run:      runuser -u $RUN_USER -- bash -c 'set -a; . $CONF; set +a; python3 $BIN --dry-run --list 10'
EOF
