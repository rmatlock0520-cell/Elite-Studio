ELITE STUDIO FOR LINUX 1.0
==========================

Elite Studio is a companion app for the game Elite Dangerous. It reads the game's own files while you play and has
four parts: Studio UI (HUD colour themes and the HUD mod), Studio Market (trades, hauling, finding things by name,
the market sniffer), Studio Nav (routes, exploring, the 3D map) and Studio Loadouts (ship builds, engineering).

This is the Linux version of the Windows app. It works with Elite Dangerous played through Steam (Proton).
Home, help and the code: https://github.com/rmatlock0520-cell/Elite-Studio


WHAT YOU NEED
- A 64-bit Linux PC (x86-64), or a Steam Deck in Desktop Mode. Nothing else to install for Studio itself.
- For Studio's own window: Ubuntu 24.04, Mint 22, Debian 13, Fedora 39 or newer (or another Linux as new: one
  with glibc, the system's core library, at 2.38 or newer), with WebKitGTK 4.1 and libnotify. Many desktops have
  both already, and install.sh tells you if yours does not. Without them Studio opens in your web browser instead
  (see "In the web browser"). To install them:
    Ubuntu, Debian, Mint:  sudo apt install libwebkit2gtk-4.1-0 libnotify4
    Fedora:                sudo dnf install webkit2gtk4.1 libnotify
    Arch, Manjaro:         sudo pacman -S webkit2gtk-4.1 libnotify
- On an older Linux (Ubuntu 22.04, Mint 21, Pop!_OS 22.04, Debian 12 and the like) installing those does not give
  the window: the system underneath is too old for it. Studio works there in your web browser, or use the Flatpak
  version for its own window (see "The Flatpak version").


HOW TO INSTALL
1. Unpack elite-studio-linux-x64.tar.gz (double-click it, or: tar -xzf elite-studio-linux-x64.tar.gz).
2. In the elite-studio folder it makes, run:  ./install.sh   (no password needed; it says what it did, and
   whether Studio's own window can start on this PC)
3. Start Elite Studio from your desktop's menu (under Games or Utilities), or with the command elite-studio.

To try it without installing, run ./elite-studio in the unpacked folder.
To remove it, run ~/.local/share/elite-studio/uninstall.sh.


FINDING THE GAME
Studio looks for Elite in Steam's folders by itself: in every Steam library (another drive, an SD card), and in the
Flatpak and Snap versions of Steam too. (The Flatpak version of Studio sees only some places: see "The Flatpak
version".) If it does not find your game (for example Elite from Epic or Frontier, started through Heroic or
Lutris), Studio UI's Setup tab shows the folder Studio reads, and lets you choose another.
Choose the folder that holds Elite's journal files. It looks like this:
  .../pfx/drive_c/users/steamuser/Saved Games/Frontier Developments/Elite Dangerous
Studio uses your choice from its next start.


HUD COLOURS ON LINUX
- After Save to game in Studio UI (or each change, with Auto-save on), press F11 in the game to load the new colours.
  (On Windows Studio presses it for you.)
- Studio does not install the HUD mod by itself on Linux: press "Install the HUD mod now" on Studio UI's Setup tab.
- The HUD mod does not work with Proton 11 yet. In Steam, open Elite's Properties, then Compatibility, and choose
  Proton 10.
- To remove the HUD mod, close Elite and delete its files from the game folder: d3d11.dll, d3dcompiler_47.dll,
  d3dx.ini and EDHM-Uninstall.bat, and the EDHM-ini, ShaderFixes and ShaderCache folders.


STEAM DECK
- Use Studio in Desktop Mode. In Game Mode the Deck shows one app at a time.
- Use the Flatpak version (see "The Flatpak version"): the Deck has Flatpak and Flathub already, and the Flatpak
  brings the library Studio's window needs. SteamOS does not carry WebKitGTK 4.1, so the tar.gz version opens in
  your web browser instead (if you have no browser yet, install Firefox or Chrome from Discover).


IN THE WEB BROWSER
Studio opens in your web browser when its window cannot start (a library it needs is missing, the Linux is older
than it needs, or there is no desktop to show it on), or when you ask for it: start it with  elite-studio --browser
(or set ELITE_STUDIO_BROWSER=1). Everything works the same there, except that choosing a folder uses your desktop's
own folder picker (KDE's kdialog or GNOME's zenity): with neither, the buttons that choose a folder do nothing.
Closing the browser tab leaves Studio running, so starting Elite Studio again opens it in a new tab. It stops when
you log out, or with Ctrl+C in the terminal it was started from.


IF THE WINDOW STAYS BLANK OR BLACK
Some graphics drivers have trouble with WebKitGTK. The elite-studio command already turns its newer way of drawing
off (WEBKIT_DISABLE_DMABUF_RENDERER=1). If the window still stays blank, use the web browser instead:
  elite-studio --browser
As a last resort, start Studio with
  LIBGL_ALWAYS_SOFTWARE=1 elite-studio
which draws without the graphics card. The 3D map in Studio Nav then shows only its flat view.


THE FLATPAK VERSION
elite-studio.flatpak is the same Studio with the newer libraries its window needs (from GNOME, through Flathub),
for the Steam Deck and for a Linux too old for the window. Flatpak and Flathub must be set up first: the Steam Deck
and Mint have both, for others see https://flathub.org/setup. Install it with
  flatpak install --user elite-studio.flatpak
(say yes if it asks to add Flathub or to install GNOME's runtime) and start it from your desktop's menu. To start it
in the web browser instead:  flatpak run io.github.rmatlock0520_cell.EliteStudio --browser
It runs closed off from the rest of the PC and sees only these places: your home folder, /run/media, /media and
/mnt (where other drives and SD cards are), and the folders of the Flatpak version of Steam.
A Steam library anywhere else stays hidden from it, and its "Choose another folder" cannot reach it either. Then
use the tar.gz version, or let the Flatpak see that place:
  flatpak override --user --filesystem=/path/to/the/library io.github.rmatlock0520_cell.EliteStudio
The Flatpak version cannot see whether Elite is running, so Studio UI shows the game as closed.


WHAT THE LINUX VERSION LEAVES OUT
- Studio Flight.
- The boxes shown over the game (Hauling F2, Find F3, Route F4, Loadouts F5), their keys and controller buttons.
- Nav scan, which reads system names from your screenshots.
- The icon near the clock: closing the window ends Studio, and the Sniffer with it.
- Installing the HUD mod by itself and pressing F11 for you (see "HUD colours on Linux").
- Finding the game outside Steam (see "Finding the game").
Everything else is the same as on Windows.


WHERE ITS FILES ARE
- The program:                                ~/.local/share/elite-studio
- Studio's own files (settings, themes,
  builds, the log):                           ~/.local/share/Elite Studio
- The Flatpak version keeps its own files in  ~/.var/app/io.github.rmatlock0520_cell.EliteStudio/data/Elite Studio
When asking for help, send the log (logs/studio.log in Studio's own files), or the file Studio UI's
"Save a diagnostic report" button makes.

Elite Studio is free software under the GPL-3.0 licence, in LICENSE.txt. CREDITS.md names the work Studio builds
on, with the licence notices that must go with it (EDTS's among them). Studio's window is made with Photino
(Photino.NET and Photino.Native, by TryPhotino) under the Apache License 2.0, in Photino-LICENSE.txt. The fonts in
web/fonts are free fonts (Barlow, DINish, Selawik, Cascadia Mono) under the SIL Open Font License; their licences
are next to them.
