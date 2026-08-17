# Linux and macOS

Kkindle uses one portable Avalonia UI with separate Windows, Linux and macOS
desktop heads. Windows retains WPD support for MTP-only Kindle devices. Linux
and macOS support Kindles exposed as mounted USB storage; MTP devices are not
currently enumerated on those systems.

## User data and secrets

- Linux data: `$XDG_DATA_HOME/Kkindle`, falling back to
  `~/.local/share/Kkindle`; root configuration uses `$XDG_CONFIG_HOME/Kkindle`
  or `~/.config/Kkindle`.
- macOS data and root configuration:
  `~/Library/Application Support/Kkindle`.
- Linux stores its wrapping key in Secret Service using `secret-tool`.
- macOS stores its wrapping key in the login Keychain.
- Windows continues to use DPAPI and its existing data layout.

## Build and package

```sh
dotnet build src/Kkindle.Desktop.Linux/Kkindle.Desktop.Linux.csproj -c Release
dotnet build src/Kkindle.Desktop.MacOS/Kkindle.Desktop.MacOS.csproj -c Release
bash scripts/build-linux-release.sh 0.5.2 artifacts/linux linux-x64
bash scripts/build-macos-release.sh 0.5.2 artifacts/macos osx-arm64
```

The Linux package is self-contained for .NET but still depends on the native
WebKitGTK, font, Secret Service and desktop libraries declared by the `.deb`.
The reader prefers WPE WebKit when `libWPEWebKit-2.0.so.1` is installed and
automatically falls back to the packaged WebKitGTK 4.1 dependency otherwise.
This makes the same package work on Ubuntu 24.04 and distributions that ship
WPE WebKit 2.0.

The `Development Build` GitHub Actions workflow can be run manually or by
pushing the `develop`/`dev/**` branches. It appends the Actions run number to a
base version such as `0.6.0-dev`, uploads three-platform packages as seven-day
workflow artifacts, and never creates a Git tag or GitHub Release. Development
macOS packages always use ad-hoc signing.

Calibre is not bundled in any Windows, Linux or macOS archive. It is optional and is
discovered from the application directory, standard install locations or
`PATH`, or the user can select `ebook-convert` in Settings. On Debian/Ubuntu,
the `.deb` lists `calibre` only as a suggested package, so installing Kkindle
does not automatically install it.

The Settings page also offers explicit user-initiated installation buttons.
Windows downloads the official signed MSI and launches Windows Installer;
Linux runs calibre's official isolated installer into `~/calibre-bin` without
root; macOS verifies the official DMG and application signature before placing
`calibre.app` in `~/Applications`. KFX Input is downloaded from calibre's
official plugin index, validated as a plugin ZIP and installed with the
detected `calibre-customize`. These downloads never become part of a Kkindle
release artifact.

Local macOS builds and GitHub Releases use ad-hoc signing when Apple
distribution credentials are not configured. The archive includes
`MACOS-README.md` with the Gatekeeper override steps required for that build.

For normal Gatekeeper-approved distribution, configure these repository
secrets:
`APPLE_CERTIFICATE_P12_BASE64`, `APPLE_CERTIFICATE_PASSWORD`,
`APPLE_NOTARY_KEY_BASE64`, `APPLE_NOTARY_KEY_ID`, and `APPLE_NOTARY_ISSUER`.
`APPLE_SIGNING_IDENTITY` and `APPLE_KEYCHAIN_PASSWORD` are optional; the
workflow discovers the Developer ID identity and generates a temporary
keychain password when they are omitted. The release job imports the
certificate, signs with the hardened runtime, notarizes, staples and validates
the app before uploading it. Supplying only part of the required credentials
fails the release instead of silently falling back to ad-hoc signing.
