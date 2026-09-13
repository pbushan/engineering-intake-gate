#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
cd "$repository_root"

expected_version="2026.9.2"
retired_version="0.1.""0"
retired_tag="v${retired_version}"
retired_notes="RELEASE_NOTES_${retired_tag}.md"
invalid_padded_version="2026.0""9.0"
version=$(tr -d '\r\n' < VERSION)

[ "$version" = "$expected_version" ] || {
    echo "VERSION must contain the current public release $expected_version; found '$version'." >&2
    exit 1
}
printf '%s\n' "$version" | grep -E '^[0-9]{4}\.(1[0-2]|[1-9])\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$' >/dev/null || {
    echo "VERSION is not SemVer-compatible YYYY.M.PATCH without zero-padded numeric fields." >&2
    exit 1
}

grep -F '<ProductVersion>$([System.IO.File]::ReadAllText('\''$(MSBuildThisFileDirectory)VERSION'\'').Trim())</ProductVersion>' Directory.Build.props >/dev/null
grep -F '<Version>$(ProductVersion)</Version>' Directory.Build.props >/dev/null
grep -F '<InformationalVersion>$(ProductVersion)</InformationalVersion>' Directory.Build.props >/dev/null
grep -F 'COPY VERSION Directory.Build.props' Dockerfile >/dev/null
if rg -n '<(Version|InformationalVersion)>' src tests --glob '*.csproj'; then
    echo "A project-local .NET product version bypasses the repository authority." >&2
    exit 1
fi

package_version=$(sed -n 's/^[[:space:]]*"version": "\([^"]*\)",/\1/p' src/IntakeGate.Web/package.json | head -1)
[ "$package_version" = "$version" ] || {
    echo "Frontend package version '$package_version' does not match VERSION '$version'." >&2
    exit 1
}
lock_versions=$(sed -n 's/^[[:space:]]*"version": "\([^"]*\)",/\1/p' src/IntakeGate.Web/package-lock.json | head -2)
[ "$lock_versions" = "$version
$version" ] || {
    echo "Frontend lockfile root versions do not match VERSION '$version'." >&2
    exit 1
}

release_notes="docs/RELEASE_NOTES_${version}.md"
[ -f "$release_notes" ] || { echo "Missing current release notes: $release_notes" >&2; exit 1; }
grep -F "# Engineering Intake Gate $version" "$release_notes" >/dev/null
grep -F "Current release:** \`$version\`" README.md >/dev/null
grep -F "Git tag is \`v$version\`" README.md >/dev/null
grep -F "version: '$version'" src/IntakeGate.Web/src/test/mockBackend.ts >/dev/null

if find docs -maxdepth 1 -type f -name "$retired_notes" | grep . >/dev/null; then
    echo "The retired release-notes filename still exists." >&2
    exit 1
fi
if rg -n -F "$retired_version" VERSION Directory.Build.props README.md CONTRIBUTING.md docs \
    src/IntakeGate.Host src/IntakeGate.Web/src tests test-harness profiles; then
    echo "Stale product-release metadata remains outside third-party dependency manifests." >&2
    exit 1
fi
if rg -n -F -e "$retired_tag" -e "$retired_notes" -e "$invalid_padded_version" \
    . --glob '!**/.git/**' --glob '!**/node_modules/**' --glob '!docs/VERSIONING.md'; then
    echo "A retired or invalid public release identity remains." >&2
    exit 1
fi

echo "Version alignment passed: $version"
