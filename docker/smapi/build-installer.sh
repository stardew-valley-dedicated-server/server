#!/usr/bin/env bash
# Builds the Linux SMAPI installer package from a (patched) SMAPI source tree, mirroring the
# Linux part of upstream's build/scripts/prepare-install-package.ps1, and writes the reinstall
# key the startup scripts compare against.
#
# usage: build-installer.sh <smapi-src> <game-dir> <patches-dir> <smapi-version> <out-dir>
#
# Output layout under <out-dir>:
#   internal/linux/SMAPI.Installer  self-contained installer (run with --no-prompt --install --game-path)
#   internal/linux/install.dat      the SMAPI bundle the installer lays out into the game folder
#   BUILD_ID                        <smapi-version>-<12 hex of sha256 over the patch series and this script>
#   LICENSE.txt                     upstream's LGPL notice (SMAPI is LGPL v3)
#   SOURCE-OFFER.txt                LGPL v3 corresponding-source pointer for the modified build
set -euo pipefail

src="$1"
game="$2"
patches="$3"
version="$4"
out="$5"

assets="${src}/src/SMAPI.Installer/assets"
publish_args=(
    --configuration Release
    -v minimal
    --runtime linux-x64
    --framework net6.0
    -p:TargetFrameworks=net6.0
    -p:GamePath="${game}"
    -p:CopyToGameFolder=false
    # net6.0 is end-of-life for the SDK; the game's bundled runtime is what SMAPI actually runs on
    -p:CheckEolTargetFramework=false
)

echo "Compiling SMAPI ${version}..."
dotnet publish "${src}/src/SMAPI" "${publish_args[@]}" --self-contained true
dotnet publish "${src}/src/SMAPI.Installer" "${publish_args[@]}" --self-contained true
for mod in ConsoleCommands SaveBackup; do
    dotnet publish "${src}/src/SMAPI.Mods.${mod}" "${publish_args[@]}" --self-contained false
done

echo "Preparing install package..."
smapi_bin="${src}/src/SMAPI/bin/Release/linux-x64/publish"
internal="${out}/internal/linux"
bundle="${internal}/bundle"
mkdir -p "${bundle}/smapi-internal"

# installer
cp -r "${src}/src/SMAPI.Installer/bin/Release/linux-x64/publish/." "${internal}"
rm -rf "${internal}/assets"

# bundle root: SMAPI itself runs on the game's bundled .NET runtime, hence the shipped
# runtimeconfig instead of the build-generated one
cp "${assets}/runtimeconfig.json" "${bundle}/StardewModdingAPI.runtimeconfig.json"
cp "${assets}/unix-launcher.sh" "${bundle}"
for name in StardewModdingAPI StardewModdingAPI.dll StardewModdingAPI.xml steam_appid.txt; do
    cp "${smapi_bin}/${name}" "${bundle}"
done

# bundle smapi-internal
cp -r "${smapi_bin}/i18n" "${bundle}/smapi-internal"
for name in 0Harmony.dll 0Harmony.xml Markdig.dll Mono.Cecil.dll Mono.Cecil.Mdb.dll Mono.Cecil.Pdb.dll \
    MonoMod.Common.dll Newtonsoft.Json.dll Pathoschild.Http.Client.dll Pintail.dll TMXTile.dll \
    SMAPI.Toolkit.dll SMAPI.Toolkit.xml SMAPI.Toolkit.CoreInterfaces.dll SMAPI.Toolkit.CoreInterfaces.xml \
    System.Net.Http.Formatting.dll; do
    cp "${smapi_bin}/${name}" "${bundle}/smapi-internal"
done
cp "${smapi_bin}/SMAPI.blacklist.json" "${bundle}/smapi-internal/blacklist.json"
cp "${smapi_bin}/SMAPI.config.json" "${bundle}/smapi-internal/config.json"
cp "${smapi_bin}/SMAPI.metadata.json" "${bundle}/smapi-internal/metadata.json"

# bundled mods
for mod in ConsoleCommands SaveBackup; do
    from="${src}/src/SMAPI.Mods.${mod}/bin/Release/linux-x64/publish"
    mkdir -p "${bundle}/Mods/${mod}"
    cp "${from}/${mod}.dll" "${from}/manifest.json" "${bundle}/Mods/${mod}"
    if [ -d "${from}/i18n" ]; then
        cp -r "${from}/i18n" "${bundle}/Mods/${mod}"
    fi
done

chmod 755 "${bundle}/unix-launcher.sh" "${bundle}/StardewModdingAPI" "${internal}/SMAPI.Installer"

# install.dat: zip (not a build task) keeps the Unix permission bits
(cd "${bundle}" && zip -q -r "../install.dat" .)
rm -rf "${bundle}"

# reinstall key + license: keyed on the SMAPI version, patch series, and this packaging script —
# the repo-controlled inputs that reshape the artifact — so changing any of them reinstalls over an
# existing volume instead of keeping a stale build. Upstream/toolchain drift is deliberately out of
# scope: the ${SMAPI_VERSION} tag, game reference DLLs, and SDK image are pinned or build an
# equivalent artifact, and byte-hashing the package would churn the key every build (compiled DLLs
# and the zip carry timestamps) and force a needless reinstall on every upgrade.
patch_hash="$(cat "${patches}"/*.patch "$0" | sha256sum | cut -c1-12)"
printf '%s\n' "${version}-${patch_hash}" > "${out}/BUILD_ID"
cp "${src}/LICENSE.txt" "${out}/LICENSE.txt"

# LGPL v3 source offer: image recipients get LICENSE.txt but not the repo — point them at the
# corresponding source for this modified build.
cat > "${out}/SOURCE-OFFER.txt" <<EOF
This directory ships a MODIFIED build of SMAPI (Stardew Modding API), licensed under
LGPL v3 -- see LICENSE.txt in this directory for the full license text.

Upstream source:      https://github.com/Pathoschild/SMAPI (git tag ${version})
Modifications:        https://github.com/stardew-valley-dedicated-server/server
                      -> patches/smapi/ (applied at build time)
Corresponding source: the upstream tag above plus that patch series. This exact build is
                      identified by BUILD_ID ($(cat "${out}/BUILD_ID")) and by the image's
                      SDVD_GIT_SHA environment variable.
EOF

echo "SMAPI $(cat "${out}/BUILD_ID") packaged in ${out}"
