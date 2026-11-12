#!/usr/bin/env bash
# Assembles the files that GitHub Pages serves for in-place updates: one .appinstaller per architecture, the signing
# certificate and the sign-in callback page. The packages themselves stay on the GitHub Release, so the site is a few
# kilobytes and deploys in seconds (a 95 MB site sometimes took ten minutes to publish, leaving the feed behind).
#
#   make-update-site.sh <dist> <out> <site-url> <version> <package-url>
#
# <dist> holds the downloaded build artifacts, <out> is created, <site-url> is where <out> will be served and
# <package-url> is where the .msix files are published (both without a trailing slash). The package name and
# publisher come from Package.appxmanifest, so they can't drift.
set -euo pipefail

dist=$1 out=$2 site=${3%/} version=$4 packages=${5%/}
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
  package=$(basename "$msix")

  # The framework packages the app needs (the Visual C++ runtime) are installed with it from the Release, where the
  # release step puts them as <name>_<arch>.appx. Their identity is read from their own manifests.
  dependencies=""
  while IFS= read -r dep; do
    [ -n "$dep" ] || continue
    depManifest=$(unzip -p "$dep" AppxManifest.xml)
    depIdentity=$(grep -oP '<Identity [^>]*' <<<"$depManifest")
    depName=$(grep -oP ' Name="\K[^"]+' <<<"$depIdentity")
    depPublisher=$(grep -oP 'Publisher="\K[^"]+' <<<"$depIdentity")
    depVersion=$(grep -oP 'Version="\K[^"]+' <<<"$depIdentity")
    dependencies+="    <Package Name=\"$depName\" Publisher=\"$depPublisher\" Version=\"$depVersion\" ProcessorArchitecture=\"$arch\" Uri=\"$packages/$(basename "${dep%.appx}")_$arch.appx\" />"$'\n'
  done < <(find "$dist" -ipath "*/Dependencies/$arch/*.appx")
  [ -n "$dependencies" ] || echo "warning: no framework packages for $arch under $dist" >&2
  dependencySection=""
  [ -z "$dependencies" ] || dependencySection="  <Dependencies>"$'\n'"$dependencies  </Dependencies>"$'\n'

  # Checks for an update at every launch, and applies it before the app opens (when it is started from the Start menu,
  # a tile or a link; a taskbar or desktop shortcut gets the silent update instead). Every 8 hours a background check
  # installs updates while the app isn't running.
  cat > "$out/Longhand-$arch.appinstaller" <<XML
<?xml version="1.0" encoding="utf-8"?>
<AppInstaller xmlns="http://schemas.microsoft.com/appx/appinstaller/2018" Version="$version" Uri="$site/Longhand-$arch.appinstaller">
  <MainPackage Name="$name" Publisher="$publisher" Version="$version" ProcessorArchitecture="$arch" Uri="$packages/$package" />
$dependencySection  <UpdateSettings>
    <OnLaunch HoursBetweenUpdateChecks="0" ShowPrompt="true" UpdateBlocksActivation="true" />
    <AutomaticBackgroundTask />
  </UpdateSettings>
</AppInstaller>
XML
done
