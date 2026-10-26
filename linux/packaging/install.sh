#!/bin/sh
# Installs Elite Studio for this user only: no administrator password, nothing outside the home folder.
# Run it from the unpacked folder: ./install.sh
set -e

from=$(dirname "$(readlink -f "$0")")
[ -n "$HOME" ] || { echo "HOME is not set, so there is nowhere to install to." >&2; exit 1; }
[ -f "$from/EliteStudio" ] || { echo "Run install.sh from the unpacked Elite Studio folder." >&2; exit 1; }
share=${XDG_DATA_HOME:-$HOME/.local/share}
program="$share/elite-studio"
bin="$HOME/.local/bin"

# A new version replaces the old one whole, so nothing of the old one is left behind. Studio's own files (settings,
# themes, builds, logs) are in another folder, "$share/Elite Studio", and are kept.
if [ "$from" != "$program" ]; then
    rm -rf "$program"
    mkdir -p "$program"
    cp -R "$from/." "$program/"
fi
chmod +x "$program/EliteStudio" "$program/elite-studio"

mkdir -p "$share/applications" "$share/icons/hicolor/256x256/apps" "$bin"
# The menu entry starts Studio by its full path: ~/.local/bin is not on every desktop's search path.
sed "s|^Exec=.*|Exec=\"$program/elite-studio\"|" "$program/elite-studio.desktop" > "$share/applications/elite-studio.desktop"
cp "$program/elite-studio.png" "$share/icons/hicolor/256x256/apps/elite-studio.png"
ln -sf "$program/elite-studio" "$bin/elite-studio"

echo "Elite Studio is installed:"
echo "  the program        $program"
echo "  the menu entry     $share/applications/elite-studio.desktop"
echo "  the icon           $share/icons/hicolor/256x256/apps/elite-studio.png"
echo "  the command        $bin/elite-studio"
echo "Start it from your desktop's menu (under Games or Utilities), or with the command elite-studio."
case ":$PATH:" in
    *":$bin:"*) ;;
    *) echo "($bin is not on your PATH yet, so for now type $bin/elite-studio.)" ;;
esac

# The window needs WebKitGTK 4.1, libnotify and a few more of the system's libraries, on a Linux no older than
# Ubuntu 24.04 (the window's library, Photino.Native.so, asks for glibc 2.38). ldd says what of that this PC lacks:
# "libname => not found" for a library, "version `GLIBC_2.38' not found" for too old a system. Without the window
# Studio still works, in the web browser.
lacks=$(command -v ldd >/dev/null && ldd "$program/Photino.Native.so" 2>&1 | grep 'not found' || true)
if printf '%s\n' "$lacks" | grep -q 'version'; then
    echo
    echo "Note: this Linux is older than Studio's own window needs, so Studio will open in your web browser instead."
    echo "The window needs Ubuntu 24.04, Mint 22, Debian 13, Fedora 39 or newer; installing more does not change that."
    echo "For Studio's own window on this PC, use the Flatpak version (see README.txt)."
elif [ -n "$lacks" ]; then
    echo
    echo "Note: this PC lacks libraries Studio's own window needs, so Studio will open in your web browser instead."
    echo "Missing: $(printf '%s\n' "$lacks" | sed 's/^[[:space:]]*\([^[:space:]]*\).*/\1/' | sort -u | paste -s -d ' ' -)"
    echo "For its own window, install WebKitGTK 4.1 and libnotify, for example:"
    echo "  Ubuntu, Debian, Mint:  sudo apt install libwebkit2gtk-4.1-0 libnotify4"
    echo "  Fedora:                sudo dnf install webkit2gtk4.1 libnotify"
    echo "  Arch, Manjaro:         sudo pacman -S webkit2gtk-4.1 libnotify"
    echo "On a Steam Deck, keep using the browser: SteamOS does not carry WebKitGTK 4.1 (see README.txt)."
fi
