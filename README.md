# ParaWASD

<a href="https://ibb.co/SwDrBpcP">
  <img src="https://i.ibb.co/4Rwdpx27/output-image.png" alt="ParaWASD title image" width="100%">
</a>

ParaWASD is a BepInEx plugin for Paralives that adds an eye-level camera and direct WASD movement for the selected Para.

Version: 0.97.0

## Features

- Toggle ParaWASD during Live Mode with `F6`.
- Move the selected Para with `W/A/S/D`.
- Look around with the mouse from an eye-level camera.
- Hold `Left Shift` to sprint.
- Press `C` to cancel the current queued action.
- Look at something and press `E` to open its interactions right from the crosshair.
- Walk up to another Para and press `E` to start a conversation, with the together cards driven from the keyboard.
- Swap between household Paras without leaving first person using `[` and `]`.
- Navigate interaction menus and the conversation from the keyboard, or tap `Left Alt` to bring the mouse back when you want it.
- Automatically opens nearby doors and handles short stairs and steps while moving.
- Walls, floors, fog, and your own Para's shadow are tuned to look right from inside a room instead of from the overhead camera.
- Tune mouse look, camera, and movement settings through the BepInEx config file.
- Cleanly restores the game camera, cursor, and visibility when disabled, and never changes your saved autonomy setting.

## Requirements

- Paralives
- [BepInEx 5.4.23.5 for Windows x64](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)
- Mouse and keyboard

ParaWASD does not include BepInEx. Install BepInEx first, then install the plugin.

## Installation

1. Install [BepInEx 5.4.23.5 for Windows x64](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip).
   - ParaWASD is built for BepInEx 5 (Windows x64).
   - Extract BepInEx into the Paralives game folder, the same folder that contains the game executable.
   - Start the game once so BepInEx can create its folders, then close the game.
2. Download [ParaWASD-0.97.0.zip](https://github.com/nolanlane/ParaWASD/releases/download/0.97.0/ParaWASD-0.97.0.zip).
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
| `E` | Interact with whatever the crosshair is on, or talk to a nearby Para |
| `C` | Cancel the current queued action |
| `[` / `]` | Switch to the previous / next household Para |
| `Left Alt` | Toggle cursor mode |

## Configuration

After ParaWASD runs once, BepInEx creates the config file at:

```text
BepInEx/config/com.parawasd.plugin.cfg
```

You can adjust mouse sensitivity, invert mouse Y, pitch limits, field of view, optional camera smoothing, camera offsets, movement speed, and sprint speed.

The default settings are the tested values. Changing them can make the camera or movement unstable and may break or crash the game. If that happens, restore the defaults or delete the config file so BepInEx can recreate it.

## Interaction Menus

| Key | Action |
| --- | --- |
| `W/S` or `Up/Down` | Move selection |
| `D` or `Right` | Open submenu |
| `A` or `Left` | Return to parent menu |
| `Enter`, `Keypad Enter`, or `E` | Open submenu or choose action |
| `Q` | Close the menu |

The menu opens in keyboard mode so you can pick an action without ever touching the mouse. If you'd rather click, tap `Left Alt` to bring the cursor back.

## Conversations

Look at another Para and press `E` to walk over and start talking. The conversation runs entirely from the keyboard:

| Key | Action |
| --- | --- |
| `E` | Start talking, then choose the highlighted together card |
| `Left/Right` or `A/D` | Browse the together cards |
| `R` | Switch a card's variant when one is available |
| `Q` | End the conversation (or back out of choosing who to involve) |

## Known Issues

- Stairs and steps mostly work now, but unusual layouts can still trip up the pathing.
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

Place the project in `Mods/ParaWASD` inside a Paralives install. The project expects game assemblies in `../../Paralives_Data/Managed` and BepInEx 5 assemblies in `../../BepInEx/core`.

```powershell
dotnet build ParaWASD.csproj -c Release
cp bin/Release/netstandard2.1/ParaWASD.dll "../../BepInEx/plugins/"
```

## License

MIT
