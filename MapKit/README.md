# KotH map kit

Build a KotH map in **Stride Game Studio 4.4**, export it as a map package, play it and share it on the Steam Workshop.
Light, sky and fog are always the game's; you choose per object whether its shadow is baked or live.

## Setup
- Stride 4.4.1 Game Studio and the .NET 11 SDK. Open `Stride/KotHMapKit.slnx`.
- The scene `MyMap` is the sample: ground, a stepped hill, the zone, four spawns, crates, a pillar, a bush and a pond.
  Duplicate it or edit it; the exported scene is the one set as default in Game Settings.

## Building a map
Add components from the **KotH map** category:

| Component | On | What it does |
|---|---|---|
| Map info | any entity, once | folder name, title, author, description, workshop visibility, change note |
| Map zone | the zone box, once | position = centre, scale = size in metres; capture rules, respawn height |
| Map spawn | a figure | a player's feet; face the zone or the entity's forward (−Z); up to 12 |
| Map object | an object or a group | shadow Baked / Live / Off, collision Mesh / None / InvisibleWall, template |
| Map bots | any entity, optional | bot tuning by name (see the component's description) |
| Map ignore | helpers | never exported (the scale figures and the red 256 m bounds) |

- **Templates:** Surface (colour and texture by UVs), Terrain (texture projected from the sides, `TextureScale`
  metres per repeat), Leaves (alpha cut, both sides), Water (the game's waves). Colour, texture, glossiness and
  metalness come from the Stride material; normal maps are not drawn yet.
- **Shadows:** Baked is cheapest (lightmap, static sun shadow). Live costs time each frame; use it sparingly.
- **Collision** is the drawn mesh itself. Decoration you walk through: None. Invisible walls: InvisibleWall.
- **Textures:** compression is fine; untick **Stream** in each texture asset, else the export keeps only the colour.
- **Scale:** a player is 1.8 m tall and 0.8 m wide, jumps 1.6 m (double jump +1.1 m). Bots find their own way.
- **Limits:** static geometry only (no scripts, nothing that moves), at most 256 m on every axis.

## Export and test
- **Windows:** Run the game from Game Studio. **Linux:** `Stride/export.sh` (`--show` keeps the window open).
- The kit exports when the scene shows (the camera's view becomes the preview) and lists errors and warnings;
  fly the camera (right mouse, W A S D) and press F5 to export again.
- The package lands in the game's maps folder (`~/.local/share/KotH/maps/NAME`, Windows `%LOCALAPPDATA%\KotH\maps`;
  under Proton the Linux folder). Test it: `KotH --offline --map-file <folder>`.

## Upload
- **SteamCMD:** the export writes `NAME.workshop.vdf` beside the folder.
  `steamcmd +login <user> +workshop_build_item <path to NAME.workshop.vdf> +quit`; SteamCMD writes the new item id
  back into the vdf and later exports keep it.
