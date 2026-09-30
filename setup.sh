#!/bin/sh
# Sets up your own deployment. See deploy/README.md for what it does and why.
exec python3 "$(dirname "$0")/deploy/setup.py" "$@"
