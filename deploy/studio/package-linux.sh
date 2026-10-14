#!/usr/bin/env bash
# Linux packages for Bengine Data Studio: .deb (Debian/Ubuntu — menu entry and icon,
# apt installs the dependencies) and .AppImage (any distribution, no install).
# Neither needs a .NET runtime: the app is published self-contained as a single
# file. Used by release-studio.yml and runnable locally:
#   dotnet publish src/Bengine.Studio -c Release -r linux-x64 --self-contained \
#     -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
#   deploy/studio/package-linux.sh out 1.0.0 dist
# The AppImage needs appimagetool (APPIMAGETOOL or on PATH); without it only the .deb is built.
set -euo pipefail

SRC=$(realpath "$1")
VERSION=$2
DIST=$(mkdir -p "$3" && realpath "$3")
HERE=$(cd "$(dirname "$0")" && pwd)
WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# ---- .deb ----
# Avalonia renders through fontconfig/X11; .NET needs ICU and OpenSSL. Their sonames
# differ between releases, so each dependency lists the alternatives.
DEB=$WORK/deb
mkdir -p "$DEB/DEBIAN" "$DEB/opt/bengine-studio" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/icons/hicolor/512x512/apps"
install -m 755 "$SRC/bengine-studio" "$DEB/opt/bengine-studio/bengine-studio"
ln -s /opt/bengine-studio/bengine-studio "$DEB/usr/bin/bengine-studio"
install -m 644 "$HERE/bengine-studio.desktop" "$DEB/usr/share/applications/bengine-studio.desktop"
install -m 644 "$HERE/bengine-studio.png" "$DEB/usr/share/icons/hicolor/512x512/apps/bengine-studio.png"
cat > "$DEB/DEBIAN/control" <<CONTROL
Package: bengine-studio
Version: $VERSION
Section: database
Priority: optional
Architecture: amd64
Maintainer: Bengine <noreply@github.com>
Homepage: https://github.com/Nodirbek-Abdulaxadov/bengine
Installed-Size: $(du -sk "$DEB" | cut -f1)
Depends: libc6, libstdc++6, libfontconfig1, libx11-6, libice6, libsm6,
 libssl3t64 | libssl3,
 libicu78 | libicu77 | libicu76 | libicu75 | libicu74 | libicu72 | libicu71 | libicu70
Description: Bengine Data Studio — database IDE
 A pgAdmin-style desktop explorer for PostgreSQL, MySQL, SQL Server, and
 SQLite: object tree, properties and DDL, data editing, and a query tool.
CONTROL
chmod -R u=rwX,go=rX "$DEB"
dpkg-deb --build --root-owner-group "$DEB" "$DIST/bengine-studio_${VERSION}_amd64.deb" >/dev/null
echo "$DIST/bengine-studio_${VERSION}_amd64.deb"

# ---- AppImage ----
TOOL=${APPIMAGETOOL:-$(command -v appimagetool || true)}
if [[ -z "$TOOL" ]]; then
  echo "appimagetool not found — AppImage skipped" >&2
  exit 0
fi

APP=$WORK/BengineDataStudio.AppDir
mkdir -p "$APP/usr/bin"
install -m 755 "$SRC/bengine-studio" "$APP/usr/bin/bengine-studio"
install -m 644 "$HERE/bengine-studio.png" "$APP/bengine-studio.png"
install -m 644 "$HERE/bengine-studio.desktop" "$APP/bengine-studio.desktop"
cat > "$APP/AppRun" <<'RUN'
#!/bin/sh
HERE=$(dirname "$(readlink -f "$0")")
# Without ICU on the host, .NET runs in invariant-globalization mode instead of failing.
if ! ldconfig -p 2>/dev/null | grep -q libicuuc; then export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1; fi
exec "$HERE/usr/bin/bengine-studio" "$@"
RUN
chmod 755 "$APP/AppRun"
chmod -R u=rwX,go=rX "$APP"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$TOOL" --no-appstream "$APP" "$DIST/BengineDataStudio-${VERSION}-x86_64.AppImage" >/dev/null
echo "$DIST/BengineDataStudio-${VERSION}-x86_64.AppImage"
