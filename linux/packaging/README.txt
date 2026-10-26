ELITE STUDIO FOR LINUX 1.0
==========================

Elite Studio is a companion app for the game Elite Dangerous. It reads the game's own files while you play and has
four parts: Studio UI (HUD colour themes and the HUD mod), Studio Market (trades, hauling, finding things by name,
the market sniffer), Studio Nav (routes, exploring, the 3D map) and Studio Loadouts (ship builds, engineering).

This is the Linux version of the Windows app. It works with Elite Dangerous played through Steam (Proton).
Home, help and the code: https://github.com/rmatlock0520-cell/Elite-Studio


WHAT YOU NEED
- A 64-bit Linux PC (x86-64), or a Steam Deck in Desktop Mode. Nothing else to install for Studio itself.
- For Studio's own window: WebKitGTK 4.1. Many desktops have it already. If yours does not, Studio opens in your
  web browser instead (see "In the web browser"). To install it:
    Ubuntu, Debian, Mint:  sudo apt install libwebkit2gtk-4.1-0
    Fedora:                sudo dnf install webkit2gtk4.1
    Arch, Manjaro:         sudo pacman -S webkit2gtk-4.1


HOW TO INSTALL
1. Unpack elite-studio-linux-x64.tar.gz (double-click it, or: tar -xzf elite-studio-linux-x64.tar.gz).
2. In the elite-studio folder it makes, run:  ./install.sh   (no password needed; it says what it did)
3. Start Elite Studio from your desktop's menu (under Games or Utilities), or with the command elite-studio.

To try it without installing, run ./elite-studio in the unpacked folder.
To remove it, run ~/.local/share/elite-studio/uninstall.sh.


FINDING THE GAME
Studio looks for Elite in Steam's folders by itself: in every Steam library (another drive, an SD card), and in the
Flatpak and Snap versions of Steam too. If it does not find your game (for example Elite from Epic or Frontier,
started through Heroic or Lutris), Studio UI's Setup tab shows the folder Studio reads, and lets you choose another.
Choose the folder that holds Elite's journal files. It looks like this:
  .../pfx/drive_c/users/steamuser/Saved Games/Frontier Developments/Elite Dangerous
Studio uses your choice from its next start.


HUD COLOURS ON LINUX
- After saving a theme, press F11 in the game to load the new colours. (On Windows Studio presses it for you.)
- The HUD mod does not work with Proton 11 yet. In Steam, open Elite's Properties, then Compatibility, and choose
  Proton 10.


STEAM DECK
- Use Studio in Desktop Mode. In Game Mode the Deck shows one app at a time.
- SteamOS does not carry WebKitGTK 4.1, so Studio opens in your web browser. If you have no browser yet, install
  Firefox or Chrome from Discover.


IN THE WEB BROWSER
Studio opens in your web browser when its window cannot start (no WebKitGTK 4.1, or no desktop to show it on), or
when you ask for it: start it with  elite-studio --browser  (or set ELITE_STUDIO_BROWSER=1). Everything works the
same there. Closing the browser tab leaves Studio running, so starting Elite Studio again opens it in a new tab.
It stops when you log out, or with Ctrl+C in the terminal it was started from.


IF THE WINDOW STAYS BLANK OR BLACK
Some graphics drivers have trouble with WebKitGTK. The elite-studio command already turns its newer way of drawing
off (WEBKIT_DISABLE_DMABUF_RENDERER=1). If the window still stays blank, use the web browser instead:
  elite-studio --browser
As a last resort, start Studio with
  LIBGL_ALWAYS_SOFTWARE=1 elite-studio
which draws without the graphics card. The 3D map in Studio Nav then shows only its flat view.


WHAT THE LINUX VERSION LEAVES OUT
- Studio Flight.
- The boxes shown over the game (Hauling F2, Find F3, Route F4, Loadouts F5), their keys and controller buttons.
- Nav scan, which reads system names from your screenshots.
Everything else is the same as on Windows.


WHERE ITS FILES ARE
- The program:                                ~/.local/share/elite-studio
- Studio's own files (settings, themes,
  builds, the log):                           ~/.local/share/Elite Studio
- The Flatpak version keeps its own files in  ~/.var/app/io.github.rmatlock0520_cell.EliteStudio/data/Elite Studio
  (and cannot see whether Elite is running, so Studio UI shows the game as closed).
When asking for help, send the log (logs/studio.log in Studio's own files), or the file Studio UI's
"Save a diagnostic report" button makes.

The fonts in web/fonts are free fonts (Barlow, DINish, Selawik, Cascadia Mono) under the SIL Open Font License;
their licences are next to them.
