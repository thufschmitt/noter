#!/usr/bin/env bash
# Assembles the files that GitHub Pages serves for in-place updates: one .msix and one .appinstaller per
# architecture, plus the signing certificate.
#
#   make-update-site.sh <dist> <out> <site-url> <version>
#
# <dist> holds the downloaded build artifacts, <out> is created, and <site-url> is where <out> will be served
# (no trailing slash). The package name and publisher come from Package.appxmanifest, so they can't drift.
set -euo pipefail

dist=$1 out=$2 site=${3%/} version=$4
manifest="$(dirname "$0")/../Longhand/Package.appxmanifest"

identity=$(grep -oP '<Identity [^>]*' "$manifest")
name=$(grep -oP 'Name="\K[^"]+' <<<"$identity")
publisher=$(grep -oP 'Publisher="\K[^"]+' <<<"$identity")

mkdir -p "$out"
cp "$(find "$dist" -name Longhand.cer | head -1)" "$out/Longhand.cer"

# Servers only accept https redirect addresses, so a sign-in to a connected app ends here, and this page passes the answer on to the app.
cat > "$out/callback.html" <<'HTML'
<!doctype html>
<meta charset="utf-8">
<title>Longhand</title>
<body style="font-family: sans-serif; margin: 3em">
<p id="m">Returning to Longhand...</p>
<script>
  var target = "longhand://oauth" + location.search;
  location.replace(target);
  setTimeout(function () {
    var link = document.createElement("a");
    link.href = target;
    link.textContent = "open it";
    var m = document.getElementById("m");
    m.textContent = "If Longhand didn't open, ";
    m.appendChild(link);
    m.appendChild(document.createTextNode(". You can close this tab."));
  }, 1500);
</script>
HTML

for arch in x64 arm64; do
  msix=$(find "$dist" -iname "*_${arch}.msix" | head -1)
  [ -n "$msix" ] || { echo "No $arch .msix under $dist" >&2; exit 1; }
  # The version in the file name keeps a cached copy of an older package from being served.
  package="Longhand_${version}_${arch}.msix"
  cp "$msix" "$out/$package"

  # Checks for an update at every launch and every 8 hours in the background. The update installs the next
  # time the app is closed, so it never interrupts a page being written.
  cat > "$out/Longhand-$arch.appinstaller" <<XML
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$version" Uri="$site/Longhand-$arch.appinstaller">
  <MainPackage Name="$name" Publisher="$publisher" Version="$version" ProcessorArchitecture="$arch" Uri="$site/$package" />
  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="0" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
XML
done
