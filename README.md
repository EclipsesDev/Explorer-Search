<h1 align="center"> Explorer Search </h1>

<p align="center">
  <img src="https://img.shields.io/github/stars/EclipsesDev/Explorer-Search?style=for-the-badge&color=yellow" />
  <img src="https://img.shields.io/github/forks/EclipsesDev/Explorer-Search?style=for-the-badge&color=blue" />
  <img src="https://img.shields.io/github/watchers/EclipsesDev/Explorer-Search?style=for-the-badge&color=green" />
  <img src="https://img.shields.io/github/issues/EclipsesDev/Explorer-Search?style=for-the-badge&color=red" />
</p>

<p align="center">
  <img src="https://img.shields.io/github/last-commit/EclipsesDev/Explorer-Search?style=for-the-badge&color=purple" />
  <img src="https://img.shields.io/github/license/EclipsesDev/Explorer-Search?style=for-the-badge&color=orange" />
  <img src="https://img.shields.io/github/repo-size/EclipsesDev/Explorer-Search?style=for-the-badge&color=lightgrey" />
</p>

---

`Explorer Search` is a lightweight Windows file search app built with `C#`, `.NET 8`, and `WinUI 3`.

It is designed for fast file name lookup using a local database index, so repeated searches are quicker than Windows 11 File Explorer search on large directories.

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
