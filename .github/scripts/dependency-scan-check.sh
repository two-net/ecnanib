#!/usr/bin/env bash
#
# Holds the scan of the dependencies, and the rule of the build beside it, to a planted package:
# one that has had a known vulnerability for years (NFR-SEC-10). A scan that finds nothing says
# something only while it finds this. See "Testing" in docs/dependencies.md.
#
#   bash .github/scripts/dependency-scan-check.sh
#
# It makes its projects in a temporary folder, with a copy of the repository's
# Directory.Build.props beside them, so the repository itself never references the package.

set -euo pipefail

export DOTNET_NOLOGO=1

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/../.." && pwd)
scan="$here/dependency-scan.sh"

# Not a package that the framework brings, so the SDK does not take it out of the graph.
package=Newtonsoft.Json
version=12.0.3
advisory=https://github.com/advisories/GHSA-5crp-9r3c-p9vr
code=NU1903

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

# Makes a project that references the package, in the folder given. What comes after the folder
# is written among its items.
plant() {
  mkdir -p "$1"
  cp "$root/Directory.Build.props" "$1/"
  cat >"$1/Planted.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$package" Version="$version" />
    ${2-}
  </ItemGroup>
</Project>
EOF
}

# Says what did not hold, shows what a tool wrote, and ends.
broken() {
  printf 'The check of the dependency scan failed: %s\n' "$1" >&2
  if [ -n "${2-}" ] && [ -e "$2" ]; then
    printf '%s\n' '---' >&2
    cat "$2" >&2
  fi
  exit 1
}

# Runs the scan on a project, writes what it said to a file, and says what it ended with.
scanned() {
  local status=0
  bash "$scan" "$1" >"$2" 2>&1 || status=$?
  printf '%s' "$status"
}

# 1. The build refuses the package: the restore fails, and says why.
plant "$work/planted"
if dotnet restore "$work/planted/Planted.csproj" >"$work/restore.txt" 2>&1; then
  broken "the restore of a project with $package $version passed." "$work/restore.txt"
fi
grep -q "$code" "$work/restore.txt" || broken "the restore failed, but not with $code." "$work/restore.txt"
grep -q "$advisory" "$work/restore.txt" || broken "the restore did not name the advisory." "$work/restore.txt"
echo "The build refuses $package $version, with $code."

# 2. The scan finds it, although the restore of the build fails.
status=$(scanned "$work/planted/Planted.csproj" "$work/found.md")
[ "$status" = 1 ] || broken "the scan of the planted package ended with $status, and not with 1." "$work/found.md"
grep -q "^<!-- dependency-scan: findings " "$work/found.md" || broken "the first line does not say that something was found." "$work/found.md"
grep -q "| High | \`$package\` | $version | Planted | Direct | .*($advisory) |" "$work/found.md" \
  || broken "the scan did not name the package, its version, its severity, and the advisory." "$work/found.md"
echo "The scan finds $package $version."

# 3. An advisory that the project suppresses passes the build, and the scan lists it apart.
plant "$work/suppressed" "<NuGetAuditSuppress Include=\"$advisory\" />"
dotnet restore "$work/suppressed/Planted.csproj" >"$work/restore.txt" 2>&1 \
  || broken "the restore of a project that suppresses the advisory failed." "$work/restore.txt"
status=$(scanned "$work/suppressed/Planted.csproj" "$work/suppressed.md")
[ "$status" = 0 ] || broken "the scan of a suppressed advisory ended with $status, and not with 0." "$work/suppressed.md"
grep -q "^<!-- dependency-scan: clean -->" "$work/suppressed.md" || broken "the first line does not say that the scan is clean." "$work/suppressed.md"
grep -q "^### Suppressed" "$work/suppressed.md" && grep -q "($advisory)" "$work/suppressed.md" \
  || broken "the scan did not list the suppressed advisory." "$work/suppressed.md"
echo "A suppressed advisory passes the build, and the scan lists it apart."

# 4. A scan that did not happen is not a clean one.
status=$(scanned "$work/nothing/Nothing.csproj" "$work/nothing.md")
[ "$status" = 2 ] || broken "the scan of a project that is not there ended with $status, and not with 2." "$work/nothing.md"

plant "$work/unreachable"
cat >"$work/unreachable/nuget.config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nowhere" value="https://localhost:9/v3/index.json" />
  </packageSources>
</configuration>
EOF
status=$(scanned "$work/unreachable/Planted.csproj" "$work/unreachable.md")
[ "$status" = 2 ] || broken "the scan with a source that cannot be reached ended with $status, and not with 2." "$work/unreachable.md"
grep -q "^<!-- dependency-scan: failed -->" "$work/unreachable.md" || broken "the first line does not say that the scan did not happen." "$work/unreachable.md"
echo "A scan that did not happen ends with 2."
