# Steam Achievement Manager

Steam Achievement Manager (SAM) is a lightweight, portable application used to manage achievements and statistics in the popular PC gaming platform Steam. This application requires the [Steam client](https://store.steampowered.com/about/), a Steam account and network access. Steam must be running and the user must be logged in.

This is the code for SAM. The closed-source version originally released in 2008, last major release in 2011, and last updated in 2013 (a hotfix).

The code is being made available so that those interested can do as they like with it.

There are some changes to the code since the last closed-source release:
- General code maintenance to bring it into a more modern state.
- Icons have been replaced with ones from the Fugue Icons set.
- Version has been bumped to 7.0.x.x to indicate the open-source release.

[Download latest release](https://github.com/gibbed/SteamAchievementManager/releases/latest).

## About this fork

This is a modified fork of [gibbed/SteamAchievementManager](https://github.com/gibbed/SteamAchievementManager). It is not the original software. The original WinForms apps (SAM.Picker and SAM.Game) have been replaced by a new 64-bit WinUI 3 / .NET 8 app, `SAM.exe`:
- A game library with art, achievement progress, playtime and last played; search (by name or App ID), sorting, filters and per-account favorites.
- A per-game window with global rarity percentages, unlock dates, filters, Unlock all / Lock all, and a confirmation before anything is committed.
- Statistics show which achievements depend on them.
- Downloads (game list, images, rarity data) are cached on disk and kept per Steam account.

`SAM.API` was updated to work in 64-bit processes.

## Building

Requires Windows 10/11 and the [.NET SDK](https://dotnet.microsoft.com/download) 8 or newer. Run:

```powershell
.\build.ps1
```

or `dotnet build SAM.sln -c Release -p:Platform=x64`. The app is written to `upload\SAM.exe`; running it requires the .NET 8 Desktop Runtime. Every push is also built by GitHub Actions, and the build can be downloaded from the run's artifacts.

## Attribution

Most (if not all) icons are from the [Fugue Icons](https://p.yusukekamiyamane.com/) set.
