#!/usr/bin/env bash
# Build the package the way a release would and check it works, without publishing anything.
#
#   scripts/pack.sh              pack as the version in the project file
#   scripts/pack.sh 0.2.0        pack as 0.2.0
#
# The package and its symbols end up in ./artifacts. Set NUGET_API_KEY and run
# scripts/pack.sh 0.2.0 --push to send them to nuget.org (the tag-driven workflow in
# .github/workflows/publish.yml does this for you; see docs/Releasing.md).
set -euo pipefail
cd "$(dirname "$0")/.."

version=""
push=false
for arg in "$@"; do
  case "$arg" in
    --push) push=true ;;
    -*) echo "unknown option $arg" >&2; exit 2 ;;
    *) version="$arg" ;;
  esac
done
version_args=()
[ -n "$version" ] && version_args=("-p:Version=$version")

echo "==> test"
dotnet test --nologo -v q

echo "==> pack"
rm -rf artifacts
dotnet pack src/RebolParse -c Release --nologo -v q -o artifacts "${version_args[@]}"
package=$(ls artifacts/*.nupkg | head -1)
echo "    $package"
ls artifacts

echo "==> contents"
unzip -l "$package" | awk 'NR>3 {print "    " $4}' | grep -E "lib/|README|\.nuspec|\.xml" || true
unzip -p "$package" README.md | grep -c "github.com/kealist/rebol-parse/blob/main/" | sed 's/^/    README links pointing at GitHub: /'
if unzip -p "$package" README.md | grep -E "\]\((docs|samples|LICENSE)" >/dev/null; then
  echo "    README still has relative links" >&2; exit 1
fi

echo "==> use it from a fresh project"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
pkg_version=$(unzip -p "$package" '*.nuspec' | sed -n 's|.*<version>\(.*\)</version>.*|\1|p' | head -1)
mkdir "$work/smoke"
# restore only from ./artifacts, into a private package cache, so a stale copy cannot be picked up
cat > "$work/smoke/smoke.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <RestoreSources>$PWD/artifacts</RestoreSources>
    <RestorePackagesPath>$work/packages</RestorePackagesPath>
  </PropertyGroup>
  <ItemGroup><PackageReference Include="RebolParse" Version="$pkg_version" /></ItemGroup>
</Project>
XML
cat > "$work/smoke/Program.cs" <<'CS'
using RebolParse;
var r = Parser.Parse("width=640", Dialect.Read("copy key some alpha \"=\" copy value some digit").Compiled());
Console.WriteLine($"{r.Matched} {r.Vars["key"]} {r.Vars["value"]}");
CS
out=$(cd "$work/smoke" && dotnet run --nologo -v q 2>&1 | tail -1)
echo "    $out"
[ "$out" = "True width 640" ] || { echo "the packed library did not work in a fresh project" >&2; exit 1; }

if $push; then
  : "${NUGET_API_KEY:?set NUGET_API_KEY to push}"
  echo "==> push to nuget.org"
  dotnet nuget push "artifacts/*.nupkg" --api-key "$NUGET_API_KEY" --source https://api.nuget.org/v3/index.json --skip-duplicate
else
  echo "==> done; nothing was published (add --push to publish)"
fi
