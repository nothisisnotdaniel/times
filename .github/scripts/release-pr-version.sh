#!/bin/sh
# Prints the version of a release PR from its title, e.g. 1.2.3 for "release v1.2.3".
# Usage: git tag --list 'v*' | sh release-pr-version.sh "<PR title>"
# The whole title must be "release v" plus a SemVer version (a prerelease part is allowed, build
# metadata is not), with no -develop anywhere and no line break. The tag v<version> must not exist
# yet: tag names come one per line on stdin. The title is only ever compared, never evaluated.
set -eu

title="${1-}"
nl='
'
cr="$(printf '\r')"

refuse() {
  echo "$1" >&2
  exit 1
}

case "$title" in
  *"$nl"* | *"$cr"*)
    refuse "PR title holds a line break; it must be exactly 'release vX.Y.Z'"
    ;;
esac

case "$title" in
  'release v'*) ;;
  *) refuse "PR title is not 'release vX.Y.Z'" ;;
esac

case "$title" in
  *-develop*) refuse "PR title names a -develop version; only develop builds make those" ;;
esac

version="${title#release v}"
semver='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$'
# The title has no line break (checked above), so grep sees exactly one line: a whole-string match.
if ! printf '%s\n' "$version" | grep -Eq "$semver"; then
  refuse "PR title version is not SemVer; the title must be exactly 'release vX.Y.Z'"
fi

while IFS= read -r tag || [ -n "$tag" ]; do
  if [ "$tag" = "v$version" ]; then
    refuse "tag v$version already exists"
  fi
done

echo "$version"
