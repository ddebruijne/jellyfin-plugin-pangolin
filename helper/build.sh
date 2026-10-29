#!/bin/sh
# Builds pangolin-userspace for linux/amd64 and linux/arm64 into the given
# directory (default: dist). olm and newt are fetched at the pinned versions
# and patched (see patches/) so they can run without root.
#
# Without a local Go toolchain:
#   docker run --rm -v "$PWD/helper:/src" -w /src golang:1.26 ./build.sh dist
set -eu

OLM_VERSION=v1.10.2
NEWT_VERSION=v1.19.1
VERSION=${VERSION:-dev}

cd "$(dirname "$0")"
here=$(pwd)
mkdir -p "${1:-dist}"
out=$(cd "${1:-dist}" && pwd)

work=$(mktemp -d)
trap 'chmod -R u+w "$work"; rm -rf "$work"' EXIT

go mod download "github.com/fosrl/olm@$OLM_VERSION" "github.com/fosrl/newt@$NEWT_VERSION"
modcache=$(go env GOMODCACHE)
cp -R "$modcache/github.com/fosrl/olm@$OLM_VERSION" "$work/olm"
cp -R "$modcache/github.com/fosrl/newt@$NEWT_VERSION" "$work/newt"
chmod -R u+w "$work/olm" "$work/newt"
(cd "$work/olm" && git apply -p1 "$here/patches/olm-userspace.patch")
(cd "$work/newt" && git apply -p1 "$here/patches/newt-userspace.patch")

cp go.mod go.sum "$work/"
go mod edit -modfile="$work/go.mod" \
	-replace "github.com/fosrl/olm=$work/olm" \
	-replace "github.com/fosrl/newt=$work/newt"

for arch in amd64 arm64; do
	CGO_ENABLED=0 GOOS=linux GOARCH=$arch go build -modfile="$work/go.mod" -trimpath \
		-ldflags "-s -w -X main.version=$VERSION" \
		-o "$out/pangolin-userspace_linux_$arch" .
	echo "built $out/pangolin-userspace_linux_$arch"
done
