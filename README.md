# PC Store Together Music Sync Fix

Pick a song from **My Music** and the current track keeps playing while the new one loads. Once everyone in the shop is ready, the new track starts from 0:00 for everyone. The mod also prepares the next track in the background when the game offers it for autoplay.

If a new track takes more than 30 seconds to prepare, the mod cancels the switch. You may hear a gap if the shop is quiet or the current track ends before the new one is ready. Players who join after a track has started catch up to its current position.

The mod removes the game's limit of ten different tracks per session. The **My Music** counter shows how many tracks are in your local library.

The mod targets PC Store Together v0.1.6 (Steam build 26947026) on Windows x64. All players should install the same version, including the host.

## Install

1. Close the game. In Steam, right-click **PC Store Together** and choose **Manage → Browse local files**.
2. If you don't have BepInEx, extract `BepInEx_win_x64_5.4.23.5.zip` into that folder. [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5)
3. Download the release asset ending in `-plugin.zip` and extract it next to `PC Store Simulator Together.exe`. This puts `MusicSyncFix.dll` in `BepInEx/plugins`. If you're updating, replace the existing DLL. [Download the mod](https://github.com/shkbb/pc-store-together-music-sync-fix/releases/latest)
4. Launch through Steam, join your shop and play a track from **Music → My Music**. The game accepts MP3, OGG and WAV files up to 8 MiB for sharing.

To remove the mod, close the game and delete `BepInEx/plugins/MusicSyncFix.dll`. If a transfer stalls, check `BepInEx/LogOutput.log` for plugin messages about retries.

## Build from source

With the .NET SDK installed, run the script against a local copy of the game with BepInEx 5 installed. It uses the DLLs from that installation and writes `build/MusicSyncFix.dll`.

```powershell
.\Build.ps1 -GameDirectory 'C:\path\to\PC Store Together'
```
