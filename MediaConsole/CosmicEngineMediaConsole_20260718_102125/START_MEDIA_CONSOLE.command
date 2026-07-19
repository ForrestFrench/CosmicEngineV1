#!/bin/bash
set -euo pipefail

# Compatibility launcher. Keep both filenames working for existing shortcuts.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
exec "$SCRIPT_DIR/start_mediaConsole.command"
