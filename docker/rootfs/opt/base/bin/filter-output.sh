#!/bin/bash

# Description:
#   Watch for file changes.
#
# Usage:
#   ./filter-output.sh /path/to/file

# Check for at least one argument
if [ "$#" -lt 1 ]; then
  echo "Usage: $0 'regex_pattern' [n]"
  exit 1
fi

# Assign arguments to variables, set default line count to 1
regex_pattern="$1"
lines_to_skip="$2"

# Use grep -v if n is less than 2 for efficiency, otherwise use awk
if [ "$lines_to_skip" -lt 2 ]; then
  grep -v "$regex_pattern"
else
  awk -v n="$lines_to_skip" "/$regex_pattern/ {skip=n} skip {skip--; next} 1"
fi
