# Elite Studio

A companion app for **Elite Dangerous** on Windows. One window, four studios, and overlays you can call up inside the game.

Elite Studio reads the files the game itself writes (the journal, your ship, your cargo, your key bindings). It does not change the game and does not touch its memory.

## What it does

**Studio UI** — a theme editor for the cockpit HUD's colours, with a live preview. It works through the EDHM HUD mod (see Credits), which Studio fetches for you from its makers' own page.

**Studio Market**
- **Trades**: the best hauling trades for the ship you are flying, near you or anywhere, one way or round trip.
- **Find by name**: the nearest places that sell or buy a commodity, sell a module or a ship, or have a service. Or find a station by its own name and see its price list. Studio remembers the price list of every station whose market you open, and shows what a ton makes carried between that station and the one you are docked at.
- **Sniffer**: save commodity searches with a price and an amount. Studio runs each one once an hour, anywhere in the galaxy, and can play a sound when one is met.

**Studio Nav** — plot routes and hand them to the galaxy map one system at a time; find unexplored space.

**Studio Loadouts** — your ship as it is, a ship designer, an engineering planner with a shopping list, and Guardian unlocks.

**Overlays in the game** — F2 hauling, F3 Find by name, F4 route, F5 loadouts. Press the key to show one and work it with the mouse; press it again to hide it. Each has its own size and place.

## Install

1. Download the newest zip from the **Releases** page.
2. Unzip it anywhere.
3. Double-click **Start Elite Studio**.

You need Windows 10 or 11 and Microsoft's free **.NET 10 Desktop Runtime** (x64). If it is missing, Windows says so on the first start and offers the download.

The app is not code-signed yet, so Windows may show "Windows protected your PC" the first time. Click **More info**, then **Run anyway**.

Closing the window only hides Studio. To exit, right-click its icon near the clock and choose **Exit**.

Studio keeps its settings in `%LOCALAPPDATA%\Elite Studio`. Deleting that folder puts everything back to a first start.

## Where the data comes from

Market prices come from [Ardent Insight](https://ardent-insight.com/); stations, modules, ships and routes from [Spansh](https://spansh.co.uk/); exploration coverage from [EDAstro](https://edastro.com/). All three are free services that volunteers run for the whole community, fed by what players' tools report. An answer is as fresh as the last visit by one of those players. Studio asks them only when you search (and once an hour for a search you saved in the Sniffer), keeps answers for a few minutes instead of asking twice, and says who it is with every request.

## Build it yourself

You need the .NET 10 SDK.

```
dotnet test EliteStudio.slnx
dotnet publish src/EliteStudio.csproj -c Release -r win-x64 --self-contained false -o dist
```

The first line runs the checks; the second makes the ready-to-run copy in `dist`.

## Credits

- **EDHM** (Elite Dangerous HUD Mod) by psychicEgg and Fred89210 does the recolouring in the game. It is not part of Elite Studio and is not in this repository or its downloads: its licence does not allow passing it on. Studio downloads it from its makers for each player.
- **Ardent Insight**, **Spansh** and **EDAstro**, for the data services above.
- **EDCD** (coriolis-data and FDevIDs) for the tables of ships, modules, engineering recipes and names. The numbers in them are Frontier's.
- **Coriolis** (MIT licence), whose open-source sums the ship designer's figures follow.

Elite Dangerous is a trademark of Frontier Developments plc. Elite Studio is a fan-made tool and is not affiliated with or endorsed by Frontier Developments.

## Licence

GPL-3.0. See [LICENSE](LICENSE).
