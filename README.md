# Steam Achievement Manager

Steam Achievement Manager (SAM) is a lightweight, portable application used to manage achievements and statistics in the popular PC gaming platform Steam. This application requires the [Steam client](https://store.steampowered.com/about/), a Steam account and network access. Steam must be running and the user must be logged in.

This is the code for SAM. The closed-source version originally released in 2008, last major release in 2011, and last updated in 2013 (a hotfix).

The code is being made available so that those interested can do as they like with it.

There are some changes to the code since the last closed-source release:
- General code maintenance to bring it into a more modern state.
- Icons have been replaced with ones from the Fugue Icons set.
- Version has been bumped to 7.0.x.x to indicate the open-source release.

[Download latest release](https://github.com/gibbed/SteamAchievementManager/releases/latest).

[![Build status](https://ci.appveyor.com/api/projects/status/00vic6jliar6j0ol/branch/master?svg=true)](https://ci.appveyor.com/project/gibbed/steamachievementmanager/branch/master)

## About this fork

This is a modified fork of [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager). It is not the original software. Changes from upstream:
- SAM.Picker and SAM.Game now find each other next to their own executables, so launching them from a shortcut or another working directory works.
- The game picker no longer crashes when downloading the game list is cancelled, and shows a readable error when it fails.
- Game schemas with an unknown stat type load instead of failing.
- `build.ps1` build script and a GitHub Actions build.

## Building

Requires Windows with the Visual Studio 2022 Build Tools (MSBuild) and the .NET SDK. Run:

```powershell
.\build.ps1
```

Release builds are written to `upload\`. Every push to `master` is also built by GitHub Actions, and the executables can be downloaded from the run's artifacts.

## Attribution

Most (if not all) icons are from the [Fugue Icons](https://p.yusukekamiyamane.com/) set.
