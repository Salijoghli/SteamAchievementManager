---
name: build-latest
description: Build the current source into a single SAM.exe and put it in the repository root, ready to run. Use after making changes to SAM, or when the user asks to build, rebuild or "get the latest version" locally.
---

# Build the latest SAM.exe into the repo root

Produces `<repo>\SAM.exe`: the same single-file, self-contained exe the GitHub release ships
(`build.ps1` Release output), copied to the repository root so the user always runs the newest
build from one stable place. `upload\` is wiped by every build, so don't point anything at it.

Run every step from the repository root (the folder that holds `SAM.sln` and `build.ps1`).
Use PowerShell. Stop at the first failing step and report it with the error output; never copy a
failed or partial build into place.

## 1. Check the source builds and tests pass

```powershell
dotnet build SAM.sln -c Debug -p:Platform=x64 -v:quiet -nologo
dotnet test SAM.App.Tests -c Debug -p:Platform=x64 --no-build
```

Expect `0 Error(s)` and `Failed: 0`. If anything fails, stop and report; don't "fix" tests to get
a build out.

## 2. Make sure the root SAM.exe isn't running

```powershell
Get-Process SAM -ErrorAction SilentlyContinue | Select-Object Id, Path
```

If a SAM process runs from `<repo>\SAM.exe` (or `<repo>\upload\SAM.exe`, which the build deletes),
**do not kill it**: it is the user's app and may be running Autopilot. Ask the user to close it
(tray icon → Exit) and wait for them. SAM processes running from anywhere else (e.g. a Debug build
under `SAM.App\bin`) don't block this.

## 3. Publish the single-file build

```powershell
.\build.ps1
```

This takes a minute or two and ends with `Built ...\upload\SAM.exe (NN MB)` (about 65 MB).

## 4. Copy it to the root

```powershell
Copy-Item upload\SAM.exe .\SAM.exe -Force
Remove-Item .\SAM.exe.old, .\SAM.exe.download -ErrorAction SilentlyContinue
```

`SAM.exe.old` / `SAM.exe.download` are self-update leftovers; SAM also cleans them up itself.

## 5. Verify and report

```powershell
$exe = Get-Item .\SAM.exe
"{0}  {1:N1} MB  {2}" -f $exe.VersionInfo.FileVersion, ($exe.Length / 1MB), $exe.LastWriteTime
```

Tell the user the version, size and full path of the new `SAM.exe`, and the test result from step
1. Mention that it's a local build: the version number is whatever `SAM.App\SAM.App.csproj` says,
so if it equals the latest GitHub release, the in-app update check won't offer that release.

## Notes

- `SAM.exe` in the root is git-ignored (`/SAM.exe` in `.gitignore`); never commit it.
- On the user's PC the shortcut `..\Steam Achievement Manager.lnk` opens this root `SAM.exe`, so a
  successful build is what the shortcut starts next time.
- Don't bump the version, commit, tag or release as part of this skill. Releasing is a separate
  step the user asks for (bump `<Version>/<AssemblyVersion>/<FileVersion>`, commit, push, push a
  `vX.Y.Z` tag).
- Don't launch the new exe unless the user asks. A running SAM is single-instance, so a second
  launch only forwards to the open window anyway.
