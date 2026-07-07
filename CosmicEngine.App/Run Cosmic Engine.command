#!/bin/bash
# Double-clickable macOS launcher (Finder runs .command files in Terminal.app).
# Just delegates to run-show.sh so there's one script to maintain.
cd "$(dirname "$0")"
./run-show.sh
