#!/bin/sh
# Prints the version of a develop build: the next patch after the highest release tag, plus
# "-develop.<build>", e.g. 0.1.1-develop.12 after v0.1.0 or v0.1.0-beta.
# Usage: git tag --list 'v*' | sh develop-version.sh <build number>
# Tag names come one per line on stdin. Lines that are not v plus a SemVer version, and develop
# tags (anything containing -develop), are ignored. Only the MAJOR.MINOR.PATCH core counts.
set -eu

build="${1-}"
case "$build" in
  '' | 0* | *[!0-9]*)
    echo "build number '$build' is not a positive whole number" >&2
    exit 1
    ;;
esac

semver='^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
major=0
minor=0
patch=0

while IFS= read -r tag || [ -n "$tag" ]; do
  case "$tag" in
    *-develop*) continue ;;
  esac
  if ! printf '%s\n' "$tag" | grep -Eq "$semver"; then
    continue
  fi

  core="${tag#v}"
  core="${core%%-*}"
  ma="${core%%.*}"
  rest="${core#*.}"
  mi="${rest%%.*}"
  pa="${rest#*.}"

  if [ "$ma" -gt "$major" ] ||
    { [ "$ma" -eq "$major" ] && [ "$mi" -gt "$minor" ]; } ||
    { [ "$ma" -eq "$major" ] && [ "$mi" -eq "$minor" ] && [ "$pa" -gt "$patch" ]; }; then
    major="$ma"
    minor="$mi"
    patch="$pa"
  fi
done

echo "$major.$minor.$((patch + 1))-develop.$build"
