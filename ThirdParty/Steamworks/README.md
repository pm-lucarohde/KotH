# Steamworks redistributables

`linux64/libsteam_api.so` and `win64/steam_api64.dll` from Valve's Steamworks SDK **1.65** (23 July 2026,
`sdk/redistributable_bin`), unchanged. They are Valve's, shipped with the game under the Steamworks SDK Access
Agreement, not under KotH's licence. `KotH.Linux` and `KotH.Windows` copy them next to the executable; the game binds
the flat C API itself (`KotH.Game/Steam`), so no SDK headers are needed to build.

SHA-256: `eb2dd015b84177cf4f4326fe578aab375fd8931bbbd719c7492420d9777007fe` (so),
`e6d9bafb9a41e42fba7b21553db49f8719027af3f0deb23ff86cfc44d60e776d` (dll).
