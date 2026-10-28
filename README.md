# KotH

A first-person king-of-the-hill game: whoever holds the summit wins. Built on [Stride](https://www.stride3d.net/) 4.4
(Bepu physics, Vulkan) and .NET 11 with NativeAOT for Linux x64. Mushroom figures with bamboo canes fight on
**Mount Destiny**, a 123 m block mountain on an island, against each other and against bots.

**Rules.** The capture zone sits on the summit. Stand in it alone for 3 s to take it, then score a point per second
while you stay inside, even when others join. 100 points or the most after 20 minutes win. Every hit raises the
target's damage percent and pushes it further (12 m at 100 %). Whoever falls below the island respawns at the foot of
the mountain; percent and points stay. Multiplayer waits for 4 players (bots count), then counts down 60 s.

| Key | Action |
|---|---|
| W A S D / arrows | move |
| Mouse | look |
| Space | jump (hold = higher), tap twice in the air = double jump |
| Left Shift | sprint (a tap is enough) |
| E | crouch; tap while sprinting or land holding it = slide |
| Left click | swing |
| Mouse wheel | third person / back to first person |
| F3 / F11 | performance and network overlay / fullscreen or window |
| Escape | menu: resume, settings, to desktop, main menu (the game runs on) |

## Build and run

Needs the .NET SDK 11.0.100-rc.2 (`global.json`), a Vulkan driver and, for NativeAOT, a distrobox `aot` with `clang`,
`lld`, `zlib-devel` and `libva`. Scenes and prefabs are edited in Stride Game Studio 4.4.

```bash
Tools/publish_aot.sh                          # client, server and probes -> Bin/{Linux,Server,Probes}/aot
Bin/Linux/aot/KotH                            # main menu; --offline plays alone at once
Bin/Linux/aot/KotH --host --name Luca         # host and play (UDP 7777, up to 12 players, same build for all)
Bin/Linux/aot/KotH --join 192.168.0.5         # join, also ADDRESS:PORT
Bin/Server/aot/KotH.Server --host --bot       # the same without a window
```

Project layout: `KotH.Shared` (the whole simulation, no graphics), `KotH.Game` (client, scene, shaders), `KotH.Linux`
(executable), `KotH.Server` (headless host), `KotH.Probes` (measurements against the frozen Unity reference in
`Reference/`), `Tools/` (map generators, regression tools), `ThirdParty/Stride.Voxels` (voxel GI). Developer notes,
test aids and the probe list are in [`CLAUDE.md`](CLAUDE.md); regressions in [`Reference/REGRESSION.md`](Reference/REGRESSION.md).

## License

KotH is free software under the **GNU Lesser General Public License, version 3 or later** ([`COPYING.LESSER`](COPYING.LESSER),
supplementing the GPL v3 in [`COPYING`](COPYING)). Copyright © 2026 Luca Rohde. Bundled third-party parts keep their licenses:

- `KotH.Game/Resources/Fonts/FiraCode-Regular.ttf`: Fira Code 6.2, SIL Open Font License 1.1 (`FiraCode-OFL.txt` next to it).
- `KotH.Game/Resources/Music/*.mp3`: from [Pixabay](https://pixabay.com/service/license-summary/) (Pixabay Content License),
  normalised to −15.5 LUFS; the file names name the artist and the track's Pixabay ID.
