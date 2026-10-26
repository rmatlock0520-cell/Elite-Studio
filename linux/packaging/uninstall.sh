#!/bin/sh
# Removes what install.sh put in place. Studio's own files (settings, themes, builds, logs) are kept.
set -e

[ -n "$HOME" ] || { echo "HOME is not set, so there is nothing to find." >&2; exit 1; }
share=${XDG_DATA_HOME:-$HOME/.local/share}
rm -f "$HOME/.local/bin/elite-studio" "$share/applications/elite-studio.desktop" "$share/icons/hicolor/256x256/apps/elite-studio.png"
rm -rf "$share/elite-studio"

echo "Elite Studio is removed."
echo "Its settings, themes, builds and logs are kept in \"$share/Elite Studio\". Delete that folder to remove them too."
echo "The HUD mod Studio put into the game's folder stays there, so Elite keeps your colours."
