# NVIDIA DLSS SDK

DLSS Super Resolution for the client (`KotH.Game/Rendering/Dlss`), SDK **v310.9.16** from
https://github.com/NVIDIA/DLSS. Not in Git (59 MB): `Tools/dlss/fetch.sh` downloads it into
`.work/dlss/` (pinned, SHA-256 in `Tools/dlss/SHA256SUMS`); `publish_aot.sh linux|windows` calls it.

- `libnvsdk_ngx.a`: NGX loader, linked into the AOT binary (`KotH.Linux.csproj`, DirectPInvoke).
- `libnvidia-ngx-dlss.so.310.9.16`: the DLSS model, shipped beside the game.
- Windows: `nvsdk_ngx_s.lib` (x64, static CRT) linked into `KotH.exe` (`KotH.Windows.csproj`), `nvngx_dlss.dll` beside it.

License: NVIDIA RTX SDKs License (`LICENSE.txt` in the SDK). Redistribution only inside the game.
Before a commercial release: notify NVIDIA (developer.nvidia.com/sw-notification) and show the
NVIDIA DLSS marks as the license asks (credits/about).
