# ParaWASD

<a href="https://ibb.co/SwDrBpcP">
  <img src="https://i.ibb.co/4Rwdpx27/output-image.png" alt="ParaWASD title image" width="100%">
</a>

ParaWASD is a BepInEx plugin for Paralives that adds an eye-level camera and direct WASD movement for the selected Para.

Version: 0.96b

## Features

- Toggle ParaWASD during Live Mode with `F6`.
- Move the selected Para with `W/A/S/D`.
- Look around with the mouse from an eye-level camera.
- Hold `Left Shift` to sprint.
- Press `C` to cancel the current queued action.
- Use cursor mode to interact with world objects and menus.
- Navigate interaction menus and submenus from the keyboard.
- Automatically opens nearby doors while moving.
- Restores the game camera, cursor, visibility, and selected-Para autonomy setting when disabled.

## Requirements

- Paralives
- [BepInEx 6 Unity Mono for Windows x64](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.Mono-win-x64-6.0.0-be.755+3fab71a.zip)
- Mouse and keyboard

ParaWASD does not include BepInEx. Install BepInEx first, then install the plugin.

## Installation

1. Install [BepInEx-Unity.Mono-win-x64-6.0.0-be.755+3fab71a](https://builds.bepinex.dev/projects/bepinex_be/755/BepInEx-Unity.Mono-win-x64-6.0.0-be.755+3fab71a.zip).
   - ParaWASD is built for BepInEx 6 Unity Mono. Do not use the IL2CPP build.
   - Extract BepInEx into the Paralives game folder, the same folder that contains the game executable.
   - Start the game once so BepInEx can create its folders, then close the game.
2. Download [ParaWASD-0.96b.zip](https://github.com/nolanlane/ParaWASD/releases/download/0.96b/ParaWASD-0.96b.zip).
3. Extract the ParaWASD zip into the Paralives game folder.
4. Start the game.

After extraction, the plugin should be at:

```text
BepInEx/plugins/ParaWASD.dll
```

## Controls

| Key | Action |
| --- | --- |
| `F6` | Toggle ParaWASD |
| `Mouse` | Look around |
| `W/A/S/D` | Move |
| `Left Shift` | Sprint |
| `C` | Cancel the current queued action |
| `Left Alt` | Toggle cursor mode |
| `Escape` | Exit ParaWASD when no interaction menu is open |

## Interaction Menus

| Key | Action |
| --- | --- |
| `W/S` or `Up/Down` | Move selection |
| `D` or `Right` | Open submenu |
| `A` or `Left` | Return to parent menu |
| `Enter`, `Keypad Enter`, or `E` | Open submenu or choose action |

## Known Issues

- Stairs are not supported yet.
- Movement can be uneven around tight spaces, unusual lots, or complex NavMesh layouts.
- ParaWASD is intended for mouse and keyboard play.

## Troubleshooting

- If ParaWASD does not load, confirm BepInEx starts correctly and `ParaWASD.dll` is in `BepInEx/plugins`.
- If the camera does not toggle, make sure a Para is selected or available in the active household.
- Check the BepInEx log for messages prefixed with `[Info   :ParaWASD]` or `[ParaWASD]`.

## Support

Optional tips are welcome, but they do not include support, feature requests, or priority fixes.

- [Leave a tip on Venmo](https://venmo.com/code?user_id=2620855699898368041&created=1780019478)

## Building From Source

Place the project in `Mods/ParaWASD` inside a Paralives install. The project expects game assemblies in `../../Paralives_Data/Managed` and BepInEx assemblies in `../../BepInEx/core`.

```powershell
dotnet build ParaWASD.csproj
cp bin/Debug/netstandard2.1/ParaWASD.dll "../../BepInEx/plugins/"
```

## License

MIT
