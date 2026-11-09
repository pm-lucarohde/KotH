# Concentus (Opus in C#)

NuGet `Concentus` **2.2.2** (`KotH.Game.csproj`), a managed port of the Opus codec by Logan Stromberg, for the voice
chat (`KotH.Game/Audio/VoiceChat.cs`): VOIP mode, 48 kHz mono, 20 ms frames at 24 kbit/s, loss concealment on gaps.
Only the managed code runs (`OpusCodecFactory.AttemptToUseNativeLibrary = false`), so NativeAOT needs no libopus.

License: Xiph.Org's BSD-style Opus license (`LICENSE` in the package); ship its notice with the game.
Check: `dotnet run .work/voice/encode.cs` (a tone through encoder and decoder, bytes per frame, level kept).
