#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: build-linux-release.sh VERSION [OUTPUT_DIR] [RID]}"
output_root="${2:-artifacts/linux}"
rid="${3:-linux-x64}"

case "$rid" in
  linux-x64) deb_arch="amd64" ;;
  linux-arm64) deb_arch="arm64" ;;
  *) echo "Unsupported Linux RID: $rid" >&2; exit 2 ;;
esac

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$repo_root/scripts/dotnet-sdk.sh"
require_repo_dotnet_sdk "$repo_root"
output_root="$(mkdir -p "$output_root" && cd "$output_root" && pwd)"
work_root="$(mktemp -d)"
trap 'rm -rf "$work_root"' EXIT
publish_root="$work_root/publish"
package_root="$work_root/deb"

cd "$repo_root"
dotnet publish "src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:Version="$version" -p:PublishSingleFile=false \
  -o "$publish_root"

cp "$repo_root/LICENSE" "$publish_root/LICENSE"
cp "$repo_root/docs/cross-platform.md" "$publish_root/LINUX-README.md"
tar -C "$publish_root" -czf "$output_root/Kkindle-$version-$rid.tar.gz" .

install -d "$package_root/DEBIAN" "$package_root/opt/kkindle" \
  "$package_root/usr/bin" "$package_root/usr/share/applications" \
  "$package_root/usr/share/icons/hicolor/256x256/apps" "$package_root/usr/share/pixmaps"
cp -a "$publish_root/." "$package_root/opt/kkindle/"
install -m 0644 "$repo_root/src/Kkindle.App/Assets/Icons/kkindle.png" \
  "$package_root/usr/share/icons/hicolor/256x256/apps/kkindle.png"
install -m 0644 "$repo_root/src/Kkindle.App/Assets/Icons/kkindle.png" \
  "$package_root/usr/share/pixmaps/kkindle.png"

cat > "$package_root/DEBIAN/control" <<EOF
Package: kkindle
Version: $version
Section: utils
Priority: optional
Architecture: $deb_arch
Maintainer: kingstacker
Depends: libfontconfig1, libice6, libsm6, libsecret-tools, libwebkit2gtk-4.1-0 | libwpewebkit-2.0-1
Recommends: udisks2
Suggests: calibre, libwpewebkit-2.0-1
Description: Personal ebook library, reader and Kindle manager
 Kkindle combines a local ebook library, reading, conversion and
 mounted USB Kindle management in an Avalonia desktop application.
EOF

cat > "$package_root/usr/bin/kkindle" <<'EOF'
#!/usr/bin/env sh
export GDK_BACKEND="${GDK_BACKEND:-x11}"
exec /opt/kkindle/Kkindle "$@"
EOF
chmod 0755 "$package_root/usr/bin/kkindle" "$package_root/opt/kkindle/Kkindle"

cat > "$package_root/usr/share/applications/kkindle.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Kkindle
Comment=Ebook library, reader and Kindle manager
Exec=kkindle
Icon=kkindle
Terminal=false
Categories=Office;Viewer;
StartupWMClass=kkindle
EOF

dpkg-deb --build --root-owner-group "$package_root" \
  "$output_root/kkindle_${version}_${deb_arch}.deb"
