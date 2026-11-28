#!/usr/bin/env bash
#
# Scans the NuGet packages of a project or of a solution for known vulnerabilities (NFR-SEC-10),
# and writes what it found to standard output, as Markdown. See docs/dependencies.md.
#
#   bash .github/scripts/dependency-scan.sh Ecnanib.slnx
#
# It ends with
#   0  when no package has a known vulnerability, or only one that is suppressed,
#   1  when a package has one,
#   2  when the scan did not happen. A scan that did not happen is never taken for a clean one.
#
# The first line of what it writes says which of the three it was, and for 1 it carries the
# findings themselves, so that two scans can be told apart by that line alone.
#
# It needs the .NET SDK and jq, and nothing of GitHub, so it runs on any machine.

set -euo pipefail

export DOTNET_NOLOGO=1

target=${1:?"Usage: dependency-scan.sh <project or solution>"}

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Says that the scan did not happen, with why, and what a tool said if it said something.
did_not_happen() {
  printf '<!-- dependency-scan: failed -->\n'
  printf '## Dependency scan: the scan did not happen\n\n%s\n' "$1"
  if [ -n "${2-}" ]; then
    printf '\n```\n%s\n```\n' "$2"
  fi
  exit 2
}

command -v jq >/dev/null 2>&1 || did_not_happen "\`jq\` is not installed."
command -v dotnet >/dev/null 2>&1 || did_not_happen "The .NET SDK is not installed."
[ -e "$target" ] || did_not_happen "There is no \`$target\`."

# The packages are resolved without the audit of the restore. With it, a package with a known
# vulnerability would stop the restore, as Directory.Build.props says it does, and nothing would
# be left to report on.
if ! dotnet restore "$target" -p:NuGetAudit=false >"$work/restore.txt" 2>&1; then
  did_not_happen "The packages of \`$target\` could not be restored." "$(tail -n 20 "$work/restore.txt")"
fi

# Asks the SDK for a list of the packages, into a file of the name given. What comes after the
# name goes to the SDK. The SDK ends with 0 whatever it found, so nothing is read from that but
# that it ran: an answer that is no JSON, or that names a problem, is a scan that did not happen.
list() {
  local name=$1
  shift

  if ! dotnet package list --project "$target" --include-transitive --no-restore --format json "$@" \
      >"$work/$name.json" 2>"$work/$name.txt"; then
    did_not_happen "The SDK could not list the packages of \`$target\`." "$(cat "$work/$name.json" "$work/$name.txt")"
  fi

  if ! jq -e 'type == "object" and (.projects | type == "array")' "$work/$name.json" >/dev/null 2>&1; then
    did_not_happen "The SDK did not answer with a list of projects." "$(head -n 20 "$work/$name.json")"
  fi

  local problems
  problems=$(jq -r '.problems // [] | .[] | "\(.level): \(.text)"' "$work/$name.json")
  [ -z "$problems" ] || did_not_happen "The SDK named a problem." "$problems"
}

list packages
list vulnerable --vulnerable

# What was looked at: each project, and how many packages it has, direct or not.
jq '[.projects[]
     | { project: (.path | split("/") | last | sub("\\.[A-Za-z]+proj$"; "")),
         packages: ([.frameworks[]? | ((.topLevelPackages // []) + (.transitivePackages // []))[] | .id] | unique | length) }]' \
  "$work/packages.json" >"$work/scanned.json"

[ "$(jq 'length' "$work/scanned.json")" -gt 0 ] || did_not_happen "\`$target\` has no project."

# A project that lists no package was not looked at, as far as this script can tell.
empty=$(jq -r '[.[] | select(.packages == 0) | .project] | join(", ")' "$work/scanned.json")
[ -z "$empty" ] || did_not_happen "No package was listed for: $empty."

# What was found: a line for each advisory of each package of each project.
jq '[.projects[]
     | .path as $path
     | (.path | split("/") | last | sub("\\.[A-Za-z]+proj$"; "")) as $project
     | .frameworks[]?
     | (((.topLevelPackages // []) | map(. + { kind: "Direct" })) + ((.transitivePackages // []) | map(. + { kind: "Transitive" })))[]
     | . as $package
     | (.vulnerabilities // [])[]
     | { path: $path, project: $project, package: $package.id, version: $package.resolvedVersion,
         kind: $package.kind, severity: .severity, advisory: .advisoryurl }]
    | unique' \
  "$work/vulnerable.json" >"$work/found.json"

# The SDK lists an advisory that a project suppresses, although the restore lets it pass. So the
# suppressions of each project that something was found in are read from the project, and what
# they name is listed apart and counts for nothing.
printf '{}' >"$work/suppressed.json"
while IFS= read -r path; do
  [ -n "$path" ] || continue

  if ! dotnet msbuild "$path" -getItem:NuGetAuditSuppress >"$work/items.json" 2>"$work/items.txt"; then
    did_not_happen "The suppressions of \`$path\` could not be read." "$(cat "$work/items.json" "$work/items.txt")"
  fi

  jq --arg path "$path" --slurpfile items "$work/items.json" \
    '. + { ($path): [$items[0].Items.NuGetAuditSuppress[]?.Identity] }' \
    "$work/suppressed.json" >"$work/suppressed.next" || did_not_happen "The suppressions of \`$path\` could not be read."
  mv "$work/suppressed.next" "$work/suppressed.json"
done < <(jq -r '[.[].path] | unique | .[]' "$work/found.json")

jq --slurpfile suppressed "$work/suppressed.json" \
  'def rank: { "Critical": 0, "High": 1, "Moderate": 2, "Low": 3 }[.severity] // 4;
   map(. + { suppressed: (.advisory as $advisory | ($suppressed[0][.path] // []) | index($advisory) != null) })
   | sort_by(rank, .project, .package, .version, .advisory)' \
  "$work/found.json" >"$work/findings.json"

open=$(jq '[.[] | select(.suppressed | not)] | length' "$work/findings.json")
passed=$(jq '[.[] | select(.suppressed)] | length' "$work/findings.json")
packages=$(jq '[.[] | select(.suppressed | not) | "\(.package) \(.version)"] | unique | length' "$work/findings.json")

# A table of findings: those that are suppressed, or those that are not.
table() {
  printf '| Severity | Package | Version | Project | Referenced | Advisory |\n|---|---|---|---|---|---|\n'
  jq -r --argjson suppressed "$1" \
    '.[] | select(.suppressed == $suppressed)
     | "| \(.severity) | `\(.package)` | \(.version) | \(.project) | \(.kind) | [\(.advisory | split("/") | last)](\(.advisory)) |"' \
    "$work/findings.json"
}

if [ "$open" -gt 0 ]; then
  # The findings themselves, so that this line changes when they do and at no other time.
  printf '<!-- dependency-scan: findings %s -->\n' \
    "$(jq -r '[.[] | select(.suppressed | not) | "\(.project) \(.package) \(.version) \(.advisory)"] | join("\n") | @base64' "$work/findings.json")"

  if [ "$packages" -eq 1 ]; then
    printf '## Dependency scan: 1 package with a known vulnerability\n\n'
  else
    printf '## Dependency scan: %s packages with a known vulnerability\n\n' "$packages"
  fi

  table false
else
  printf '<!-- dependency-scan: clean -->\n'
  printf '## Dependency scan: no package with a known vulnerability\n'
fi

if [ "$passed" -gt 0 ]; then
  printf '\n### Suppressed\n\n'
  printf 'Each of these is named by a `NuGetAuditSuppress` item, with the reason beside it. It stops no build and is not counted above.\n\n'
  table true
fi

printf '\n### What was scanned\n\n'
printf '| Project | Packages, direct or not |\n|---|---|\n'
jq -r '.[] | "| \(.project) | \(.packages) |"' "$work/scanned.json"
printf '\nAsked of: %s\n' "$(jq -r '.sources // [] | join(", ")' "$work/vulnerable.json")"

[ "$open" -eq 0 ] || exit 1
