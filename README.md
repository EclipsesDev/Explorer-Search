# Explorer Search

A simple fast indexed database Windows 11 file search app built with:
- `C#`, `.NET 8`, `WinUI 3`

## Requirements
- Windows 11
- Visual Studio 2022/2026
- Workloads:
  - `.NET desktop development`
  - `Windows application development`
  -  `WinUI 3 development`
- .NET 8 SDK

## Run
1. Open `WindowFileExplorerSearch.sln`.
2. Restore NuGet packages.
3. Set `ExplorerSearch.App` as startup project.
4. Press `F5`.

## Features
- Search by part of a file name
- Choose root folder
- Include/exclude subfolders
- Max results setting
- Cancel search
- Open selected file location in Explorer

## Notes
- The app builds a local index for faster search.
- Index file location:
  - `%LOCALAPPDATA%\ExplorerSearch\index.db`
