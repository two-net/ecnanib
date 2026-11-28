#!/usr/bin/env bash
#
# Says what a scan of the dependencies found where the team works: in one issue of the
# repository, which this script opens, keeps to what the latest scan found, and closes when a
# scan finds nothing (NFR-SEC-10). See "Where the findings are reported" in docs/dependencies.md.
#
#   bash .github/scripts/dependency-scan-issue.sh <report> <address of the run> <commit>
#
# The report is what dependency-scan.sh wrote. The workflow runs this for a scan of main alone,
# with GH_TOKEN and GH_REPO set for gh.

set -euo pipefail

report=${1:?"Usage: dependency-scan-issue.sh <report> <address of the run> <commit>"}
run=${2:?"The address of the run is missing."}
commit=${3:?"The commit is missing."}

label=dependency-scan
title="Dependencies with known vulnerabilities"

# The first line of a report says what the scan was, and for findings it carries them.
said=$(head -n 1 "$report")
case "$said" in
  "<!-- dependency-scan: clean -->") found=no ;;
  "<!-- dependency-scan: findings "*) found=yes ;;
  *)
    # A scan that did not happen says nothing about the packages, so the issue stays as it is.
    echo "The scan did not happen. The issue is left as it is."
    exit 0
    ;;
esac

# The issue is found by a label of its own, and not by a search of the titles, which does not
# know an issue in the minute after it was opened. Creating a label that is there already fails,
# and that is fine.
gh label create "$label" --color B60205 --description "Opened, kept, and closed by the dependency scan" >/dev/null 2>&1 || true

open=$(gh issue list --label "$label" --state open --limit 1 --json number --jq '.[0].number // empty')

if [ "$found" = no ]; then
  if [ -n "$open" ]; then
    gh issue close "$open" --reason completed \
      --comment "The scan of ${commit} found no package with a known vulnerability: ${run}"
    echo "Closed issue #${open}."
  else
    echo "Nothing was found, and no issue is open."
  fi
  exit 0
fi

body=$(mktemp)
trap 'rm -f "$body"' EXIT

{
  cat "$report"
  printf '\n---\n\n'
  printf 'Found by the scan of %s: %s\n\n' "$commit" "$run"
  printf 'What to do is under "When the scan finds something" in [docs/dependencies.md](https://github.com/%s/blob/main/docs/dependencies.md#when-the-scan-finds-something).\n\n' "${GH_REPO:?"GH_REPO is not set."}"
  printf 'The scan keeps this issue: each scan of `main` writes the text again, and a scan that finds nothing closes the issue. Closing it by hand does not make an exception. A suppression does.\n'
} >"$body"

if [ -z "$open" ]; then
  gh issue create --title "$title" --label "$label" --body-file "$body"
  exit 0
fi

# What the issue said so far, to tell a new finding from the same one on another day. gh takes
# the first line itself: cutting its answer off in a pipe would end it with an error.
before=$(gh issue view "$open" --json body --jq '.body | split("\n")[0] | rtrimstr("\r")')

gh issue edit "$open" --body-file "$body" >/dev/null

if [ "$before" = "$said" ]; then
  echo "Issue #${open} says what the scan found, as before."
else
  gh issue comment "$open" \
    --body "What the scan finds has changed, with ${commit}: ${run}. The text of the issue is the list as it is now."
  echo "Issue #${open} says what the scan found now, and has a comment that it changed."
fi
