# KotH map kit

Build a KotH map in **Stride Game Studio 4.4** or **Unity** (2022.3 LTS or 6), export it as a map package, play it
and share it on the Steam Workshop. Light, sky and fog are always the game's; per object you choose whether its
shadow is baked or live.

## Setup
- **Stride:** Game Studio 4.4.1 and the .NET 11 SDK; open `Stride/KotHMapKit.slnx`.
- **Unity:** open the project `Unity/KotHMapKit` (Built-in pipeline), or add the package `Unity/com.koth.mapkit`
  to your own project (Package Manager → Add package from disk; URP and Built-in materials both work).
- The sample scene `MyMap`: ground, a stepped hill, the zone, four spawns, crates, a pillar, a bush and a pond.
  Unity: **KotH → New map scene** starts an empty one with the parts every map needs.

## Building a map
Add the **KotH map** components:

| Component | On | What it does |
|---|---|---|
| Map info | any object, once | folder name, title, author, description, workshop visibility, change note |
| Map zone | the zone, once | position = centre, scale = size in metres; capture rules, respawn height |
| Map spawn | a figure | a player's feet; face the zone or the object's forward; up to 12 |
| Map object | an object or a group | shadow Baked / Live / Off, collision Mesh / None / InvisibleWall, template |
| Map bots | any object, optional | bot tuning by name (see the component's description) |
| Map ignore | helpers | never exported (scale figures; Unity: also EditorOnly and inactive objects) |

- **Templates:** Surface (colour and texture by UVs), Terrain (texture projected from the sides, `TextureScale`
  metres per repeat), Leaves (alpha cut, both sides), Water (the game's waves). Colour, texture, glossiness and
  metalness come from the material; normal maps are not drawn yet.
- **Shadows:** Baked is cheapest (lightmap, static sun shadow). Live costs time each frame; use it sparingly.
- **Collision** is the drawn mesh itself. Decoration you walk through: None. Invisible walls: InvisibleWall.
- **Unity terrains** export as one mesh with their first layer's texture; place trees as objects.
- **Stride textures:** untick **Stream** in each texture asset, else the export keeps only the colour.
- **Scale:** a player is 1.8 m tall and 0.8 m wide, jumps 1.6 m (double jump +1.1 m). Bots find their own way.
- **Limits:** static geometry only (no scripts, nothing that moves), at most 256 m on every axis
  (Stride: the red bounds; Unity: select Map info, its box turns red beyond).

## Export and test
- **Stride:** run the game from Game Studio (Windows) or `Stride/export.sh` (Linux, `--show` keeps the window open).
  It exports when the scene shows, the camera's view becomes the preview; fly (right mouse, W A S D), F5 again.
- **Unity:** **KotH → Export map** (Ctrl+Shift+E); the main camera's view becomes the preview, errors and warnings
  go to the Console. Command line: `Unity -batchmode -quit -projectPath P -executeMethod KotH.MapKit.Editor.MapKitMenu.Batch`.
- The package lands in the game's maps folder (`~/.local/share/KotH/maps/NAME`, Windows `%LOCALAPPDATA%\KotH\maps`;
  under Proton the Linux folder). Test it in the game: **Host server**, then in the lobby **Esc → Map**
  (or alone with bots: `KotH --offline --map-file FOLDER`).

## Upload
- **SteamCMD:** the export writes `NAME.workshop.vdf` beside the folder (visibility, title, description and change
  note from Map info). `steamcmd +login <user> +workshop_build_item <path to NAME.workshop.vdf> +quit`; SteamCMD
  writes the new item id back into the vdf and later exports keep it.
- Hosts pick the map in the lobby (Esc → Map); guests without it download it from the workshop when they join.
