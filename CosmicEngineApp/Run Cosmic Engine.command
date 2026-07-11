#!/bin/bash
# Cosmic Engine - Desktop Launcher (double-click friendly)
#
# Double-click this file (or a Finder ALIAS to it - not a copy) to start
# Cosmic Engine and open the Scene Dashboard automatically. Finder runs
# .command files in Terminal.app on its own - no manual Terminal commands
# needed. See README_LAUNCHER.md for full setup/troubleshooting.

resolve_script_dir() {
  local source="${BASH_SOURCE[0]}"
  while [ -h "$source" ]; do
    local dir
    dir="$(cd -P "$(dirname "$source")" >/dev/null 2>&1 && pwd)"
    source="$(readlink "$source")"
    [[ "$source" != /* ]] && source="$dir/$source"
  done
  cd -P "$(dirname "$source")" >/dev/null 2>&1 && pwd
}

SCRIPT_DIR="$(resolve_script_dir)"

if [ ! -f "$SCRIPT_DIR/run-show.sh" ]; then
  echo "=========================================="
  echo "  Cosmic Engine could not start"
  echo "=========================================="
  echo "Could not find run-show.sh next to this launcher in:"
  echo "  $SCRIPT_DIR"
  echo ""
  echo "This launcher must stay inside the CosmicEngineApp folder,"
  echo "alongside run-show.sh. If you copied just this one file to your"
  echo "Desktop, that breaks it - create a Finder ALIAS instead:"
  echo "  right-click 'Run Cosmic Engine.command' > Make Alias,"
  echo "  then drag the alias to your Desktop."
  echo "See README_LAUNCHER.md for step-by-step instructions."
  echo ""
  read -r -p "Press Enter to close this window..." _ 2>/dev/null
  exit 1
fi

cd "$SCRIPT_DIR" || exit 1
exec ./run-show.sh
