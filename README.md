# Explorer Search

`Explorer Search` is a lightweight Windows file search app built with `C#`, `.NET 8`, and `WinUI 3`.

It is designed for fast filename lookup by using a local database index, so repeated searches are much quicker than scanning folders every time.

## What it does

- Search files by partial file name
- Choose a root folder to search from
- Include or exclude subfolders
- Limit maximum result count
- Cancel long-running searches
- Open the selected file location in Windows Explorer

## Apps

- **Language:** `C#`
- **Runtime/Framework:** `.NET 8`
- **Desktop UI:** `WinUI 3` (`Windows App SDK`)
- **Data storage:** `SQLite` (local file index)

## Requirements

- Windows 11 (recommended)
- `.NET 8 SDK`
- Visual Studio 2022/2026 with these workloads:
  - `.NET desktop development`
  - `Windows application development`
  - `WinUI 3 development`

## Getting started

1. Open `WindowFileExplorerSearch.sln` in Visual Studio.
2. Restore NuGet packages (if prompted).
3. Set `ExplorerSearch.App` as the startup project.
4. Select `Debug | x64`.
5. Press `F5` to run.

## Index Database File Path

The search index is stored here:

- `%LOCALAPPDATA%\ExplorerSearch\index.db`

You can delete this file to force a fresh index rebuild.

## Troubleshooting

- **App runs from solution but not from `.csproj` directly**
  - Open `WindowFileExplorerSearch.sln`
  - Use `Debug | x64`
  - Clear `.vs`, `bin`, and `obj` folders if needed