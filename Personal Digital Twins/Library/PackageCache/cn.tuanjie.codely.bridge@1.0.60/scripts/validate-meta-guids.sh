#!/usr/bin/env sh
# Same validation as validate_package_format in .gitlab-ci.yml (GUID lines in *.meta).
#
# Run without committing:
#   sh scripts/validate-meta-guids.sh
#   ./scripts/validate-meta-guids.sh
# Windows: scripts\validate-meta-guids.bat
set -eu
cd "$(dirname "$0")/.." || exit 1

find . -name "*.meta" -exec awk '/^guid:/ {
  gsub(/\r/, "", $2)
  if (length($2) != 32 || $2 !~ /^[a-zA-Z0-9]+$/) {
    print FILENAME ": Invalid GUID -> " $2;
    err = 1
  }
}
END {
  if (err) exit 1
}' {} + \
  && echo "All .meta GUID checks passed."
