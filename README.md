# Binding-of-Isaac-VR

Play **The Binding of Isaac: Repentance+** as a 2.5D miniature in VR.

Consider paying me a coffee!! This will motivate me to fix even more stuff and bring more details to this mod.
>[![Ko-fi](https://img.shields.io/badge/Ko--fi-Donate%20a%20Coffee-F16061?style=for-the-badge&logo=ko-fi&logoColor=white)](https://ko-fi.com/techslogi)

The game keeps running as normal on your desktop. A companion mod streams the game to a small OpenXR app in StereoKit that rebuilds it in front of you
as a miniature version.

Built for a **Quest 3 over Virtual Desktop**, but there is nothing Quest-specific in it; any
OpenXR headset should work (I also tested this on my PSVR2 through a PC adapter).

> **Beta.** Playable start to finish, including major bosses. Expect a few
> visual oddities. Bug reports very welcome — see [Reporting a bug](#reporting-a-bug).

---

## What it looks like

<!-- TODO: a GIF here does more than any paragraph. Grab one of: entering a room, a boss
     fight, the passthrough toggle, and the life-size room. -->

- The room as a **miniature**: sprites stand up on a floor you view from the front.
- **Grab it.** Press a grip button to move the board, both grips to scale it.
- **Passthrough or a virtual room.** Either see your real room around the board, or have the
  app rebuild Isaac's current room at life size around you.
  >***For passthrough on Virtual Desktop, use Red 0, Green 0, Blue 180, Similarity and Smoothness both to 10%.***
- **Floors, walls, water, reflections, curses, shadows and level overlays** all come from the
  game's own art, including realtime effects as they happen.
- **A real HUD** rebuilt from the game's own sprite sheets, plus optional **stats** and
  **EID** readouts.
- **Hold Map/Select** to see the game's own screen on a flat panel, for menus, the map,
  cutscenes and whenever you consider information missing from the app.

---

## Requirements

| | |
|---|---|
| Game | The Binding of Isaac: **Repentance+** Latest Public Version Available |
| Script extender | **[REPENTOGON](https://repentogon.com/)** — required |
| Headset | Any OpenXR headset. Developed on Quest 3 + Virtual Desktop |
| OS | Windows |

---

## Install

### 1. Install REPENTOGON
Follow the instructions at [repentogon.com](https://repentogon.com/). The app reads live
per-layer sprite state that the vanilla API does not expose, so this is **not optional**.

Configure Isaac to launch through REPENTOGON via Steam, adding the complete path to REPENTOGON like so:

**right-click Isaac → Properties → Launch Options**

""C:/.../Full REPENTOGON Installation Path\REPENTOGONLauncher.exe"

### 2. Extract the game's resources
Isaac ships its art inside archives. Run the **Resource Extractor** that comes with the game:

```
<Isaac folder>\tools\ResourceExtractor\ResourceExtractor.exe
```

This creates `<Isaac folder>\extracted_resources\resources\gfx\...`. The app reads sprites
straight from there, it never ships game art.

> Your Isaac folder is usually
> `...\Steam\steamapps\common\The Binding of Isaac Rebirth`.

### 3. Install the exporter mod
Copy the `isaac-diorama-exporter` folder into:

```
<Isaac folder>\mods\
```

Enable it in the game's Mods menu.

### 4. Launch Isaac with `--luadebug`
The mod sends data over a local UDP socket, which Isaac's Lua sandbox only allows with this
flag. In Steam: **right-click Isaac → Properties → Launch Options**:

```
--luadebug
```

> `--luadebug` lets any enabled mod run unsandboxed Lua. Only enable mods you trust.

Your complete string under Isaac's properties in Steam should be something like:

"C:/.../Full REPENTOGON Installation Path\REPENTOGONLauncher.exe" --luadebug  --isaac=%command%

### 5. Run the app
Start your headset's OpenXR runtime (in Virtual Desktop, connect and start VR), then run
`IsaacDiorama.exe`. It launches Isaac through Steam if the game is not already running, and
closes itself a few seconds after Isaac exits.

**You should not need to configure a path.** The app finds Isaac by reading Steam's own
library index. If it cannot, it draws a setup panel in the headset telling you what is
missing — and you can point it manually by putting the Isaac folder path in a
`resources_root.txt` file next to the exe:

```
D:\SteamLibrary\steamapps\common\The Binding of Isaac Rebirth
```

---

## Using it

### In VR

| Action | Control |
|---|---|
| Click a menu button | Point with a controller, pull the **trigger** |
| Move the board | Hold a **grip** and move your hand |
| Scale the board | Hold **both grips**, move hands apart / together |
| Reset position and scale | **Y** |
| Show the game's own screen | Hold **Map/Select** (the game's map button) |
| Show the hidden Debug page | **A / X** on either controller, then Options → Debug |

### The Options menu

A floating **Options** button sits to the left of the board.

- **Passthrough On/Off** — On leaves a flat key colour for Virtual Desktop to chroma-key, so
  you see your real room. Off paints black and rebuilds Isaac's room at life size around you.
- **Floor − / +** — the life-size room's floor height. Set this once, standing up.
- **Stats** — Isaac's stats: speed, fire rate, damage, range, shot speed, luck, planetarium
  chance and the angel-room modifier.
- **EID** — External Item Descriptions for whatever you are standing next to.
- **Exit** — closes the app *and* the game, with a confirmation.

Settings are saved automatically and restored next launch.

### Desktop keys

`F1` debug overlay · `F2` screenshot of the headset view · `F3` flat game screen ·
`Backspace` map · `B` swap floor red/blue · `V` live/tiled floor · `C` live/tiled walls ·
`Y` reset the board · `Tab` / `Z` / `X` / `Enter` slider tuning

---

## Troubleshooting

**A setup panel appears in the headset.** The extracted resources were not found. Re-check
step 2, or set `resources_root.txt`.

**"Waiting for Isaac..."** The app is running but no data is arriving. Check, in order: the
mod is enabled in the Mods menu; Isaac was launched with `--luadebug`; REPENTOGON is
installed.

**Nothing at all in the headset.** The OpenXR runtime is not up. In Virtual Desktop, connect
and enter VR *before* starting the app.

**Passthrough shows blue instead of your room.** Virtual Desktop's chroma-key is not enabled
or is keyed to a different colour. Turn passthrough off in Options for the virtual room
instead.

**Settings are not saved.** If the app lives under `Program Files`, settings go to
`%APPDATA%\IsaacDiorama\` instead. The startup log names the exact file it chose.

---

## Reporting a bug

Please include:

1. **The version.** In VR: press **A/X**, then Options → **Debug** — it is the top line.
2. What you were doing, and which floor/room if you know it.
3. A screenshot (`F2` saves one next to the exe).
4. The log, if you can — StereoKit writes it beside the exe; the mod logs to Isaac's
   `log.txt` prefixed `isaac-diorama-exporter:`.

Entity-specific oddities are much easier to fix with an **entity ID**. Isaac's debug console
(`debug 5`) draws them over everything, in `type.variant.subtype` form.

---

## How it works

```
Isaac + REPENTOGON
  └─ isaac-diorama-exporter (Lua)
       │  UDP 127.0.0.1:47800 — entities, layer state, floor/wall images,
       │  HUD, lasers, water, room state, player stats, EID text
       ▼
  IsaacDiorama.exe (C# / .NET 8 / StereoKit / OpenXR)
       └─ rebuilds the room from the game's own sprite sheets
```

The mod sends **data, never pixels** for entities: type, variant, position, animation name
and frame, per-layer overrides. The app resolves each entity's `.anm2` itself and composites
the exact frame the game is showing. Floors and walls are the exception — those stream as
images, so blood, scorch marks and liquids appear as they happen.

---

## Credits & licence

- **The Binding of Isaac: Repentance+** is by Edmund McMillen and Nicalis. This project is an
  unofficial fan tool and ships **no game assets** — it reads the copy you already own.
- **[REPENTOGON](https://repentogon.com/)** for the extended Lua API this depends on.
- **[EID](https://github.com/wofsauge/External-Item-Descriptions)** for item descriptions.
- **[StereoKit](https://stereokit.net/)** for the OpenXR rendering.
- **My incredible wife who consistently listens to all my ramblings about things like this and supports me always**

This mod is dedicated to Dante, our lovely cat who sadly passed away suddenly during the development of this mod.

![Photo of a lovely mixed breed cat, looking to its left against a window with a blue sky and clouds at the back](https://raw.githubusercontent.com/techslogi/Binding-of-Isaac-VR/refs/heads/main/docs/dante.png)
