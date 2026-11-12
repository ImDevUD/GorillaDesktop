# GorillaDesktop

A BepInEx mod that puts a TV inside Gorilla Tag. Open a home screen in VR, tap YouTube, Netflix,
Discord and more with your finger, or show your whole PC desktop.

## Features

- **TV home screen:** Live clock, app tiles and your PC screen ("My Screen") in one menu.
- **Apps:** YouTube, Netflix, Disney+, Prime Video, Discord, Hulu, Twitch and Spotify on the home screen.
  Tap **+** to add HBO Max, Kick, TikTok, Crunchyroll, Apple TV, Paramount+, SoundCloud, X,
  Instagram or Reddit.
- **Apps open full screen:** Websites open in Edge app mode with no address bar or tabs.
  Discord and Spotify open their desktop apps if installed. A loading screen shows until the app is ready.
- **Touch like a GT button:** Press the screen with your index fingertip to click, with the GT click sound
  and vibration.
- **SteamVR-style keyboard:** Type with your fingers or point at keys and pull the trigger. Turkish letters,
  symbols and voice typing (Mic). Every key is sent right away.
- **In-game monitor:** Your monitor at full resolution, captured on the GPU (Desktop Duplication).
  Does not slow the game down.
- **Move and resize:** Grab the screen with grip; grab with both hands and pull apart / push together to resize.
- **Lock:** Lock the screen in place. While locked, Y does not hide or move it and grip does not move it.
- **Settings panel:** The gear at the top right of the screen: lock, monitor, FPS, touch, click sound, GTV logo.

PC sound plays from your PC as usual; the mod does not touch audio.

## Install

1. Install **BepInEx 5** for Gorilla Tag.
2. Download the latest zip from [Releases](../../releases).
3. Put `GorillaDesktop.dll` into `Gorilla Tag\BepInEx\plugins`.
4. Start the game and press **Y on the left controller**.

## Controls (VR)

| Input | Action |
|---|---|
| Left **Y** | Show / hide the screen (does nothing while locked) |
| Touch a tile or the screen with your index finger | Open / click |
| **Home** button (under the gear) | Back to the home screen (the app keeps running) |
| **Keyboard** button (under the gear) | Show / hide the keyboard |
| **Trigger** while pointing at the keyboard | Press a key |
| **Grip** (hand next to the screen) | Grab and move; two hands to resize |
| **Joystick** while grabbing | Push the screen away / pull it closer |

WalkSim (desktop): F8 shows/hides the screen (opens straight to your PC screen), hold F6 to move it.

## Netflix, Disney+ and other protected video

Streaming sites protect their video, and Windows shows protected video as black in screen capture.
To avoid this, the mod starts Edge with hardware acceleration turned off (your own profile, so you stay
signed in). Good to know:

- If Edge is already running when you open the first app, the mod closes and restarts it. Your tabs come
  back the next time you open Edge yourself.
- Turn off **Startup boost** in Edge (`edge://settings/system`), or Edge may start in the background with
  acceleration on.
- With acceleration off, these sites usually play at 720p.

## Settings file

After the first launch, `Gorilla Tag\BepInEx\config\com.gorilladesktop.gtag.cfg` is created.
Each setting is described inside the file.

## Building from source

Needs the .NET SDK. `GamePath` in `GorillaDesktop/GorillaDesktop.csproj` must point to your
Gorilla Tag folder (or pass `-p:GamePath="..."`).

```
dotnet build GorillaDesktop -c Release
```

The menu images in `GorillaDesktop/Assets` are embedded into the DLL. The built DLL is copied to the
game's `BepInEx\plugins` folder automatically.
