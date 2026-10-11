# Art Mixer

A Fallout 4 mod. Install several art mods at once and choose in MCM how much of each one you see: paintings, posters, signs, magazines, props, the Pip-Boy, the world map, loading screens and the main menu. A converter turns each installed art mod into a pack; every pack gets a slider.

Released on Nexus Mods by [kindaginga](https://www.nexusmods.com/profile/kindaginga/mods). One set of files for every version of Fallout 4 from 1.10.163 to current.

## Parts

| Folder | What it is |
|---|---|
| `plugin/` | The F4SE plugin (C++20), built on the vendored CommonLibF4RD in `plugin/external/CommonLibF4RD` |
| `converter/ArtMixerConvert/` | The converter (C#, .NET 10, Windows Forms) that turns installed art mods into packs |
| `mod/` | The ESL, the Papyrus scripts, the MCM files, the vanilla catalog, the Read Me and the installer metadata |
| `tools/` | The ESL builder, the package script and the test launcher |

## Player requirements

- F4SE for the game version
- Mod Configuration Menu 1.39 or later
- Address Library for F4SE Plugins (All In One) or Runtime Database, either one
- .NET 10 Desktop Runtime, x64, for the converter only

## Building

Windows, Visual Studio 2026, CMake and vcpkg with `VCPKG_ROOT` set, and the .NET 10 SDK.

```
cd plugin
cmake --preset vs2026
cmake --build --preset vs2026-release
```

```
cd converter/ArtMixerConvert
dotnet build -c Release
```

```
cd tools/BuildEsl
dotnet run -c Release -- ../../mod/Data/ArtMixer.esl
```

`tools/package.ps1` builds all three parts and writes the release zip to `dist/`.

## License

All rights reserved. See `LICENSE`. Third-party components keep their own licenses.
