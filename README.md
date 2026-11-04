# PAX Media Manager

A desktop app that looks after your downloads and your media library. It moves new torrents and finished downloads where they belong, turns your folders into a poster library with ratings and details, and plays videos and pictures in its own viewer.

## Features

### Downloads
- **Torrent watcher.** Watches your Downloads folder and copies new `.torrent` files to a folder or network share your torrent client watches. It waits for the browser to finish writing the file without opening it, because opening it breaks Firefox downloads.
- **Categories and auto-move.** Finished downloads are moved to the right folder for their category (Movies, TV, Games…), with a progress overlay and an optional "it's arrived" toast.
- **Dashboard stats**, optionally merged across several PCs through a shared folder. Each PC writes only its own file, so they never conflict.
- **Shared settings** between machines (Windows and Linux), with the local copy kept for when the share is offline.

### Library
- **Poster grid or carousel** for each category, with search, sort, filters (shelf, genre, decade, resolution, rating), "Surprise me", stats and a duplicate finder.
- **Artwork and details** from your own folder images, or from online sources when you add your own keys:
  - Movies and TV: [TMDB](https://www.themoviedb.org/) (plus ratings from OMDb, MDBList and TVmaze)
  - Anime scores: AniList
  - Games: Steam
  - Audiobooks: Audible
  - Subtitles: OpenSubtitles
- **Details window**: backdrop, cast you can click through, trailer, ratings, media specs read from the file itself, an episode list with missing and upcoming episodes, and resume / next up.
- **Fix match** when a title is recognised wrong. Everything looked up is cached on disk, so it's only fetched once.
- **Discover tab**: recommendations based on what you own and watch ("Because you watched…"), plus trending titles, games and audiobooks by authors and series you have.

### Viewer
- **Video player** (LibVLC): seek, volume, speed, audio and subtitle tracks, auto-next, and it remembers where you stopped.
- **Picture viewer**: zoom, pan, rotate, fit, and a slideshow with speed, loop and shuffle.
- Opens from the library or the tray ("Open media…"), one window per item.

## Build

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:DebugType=none -o publish
```

Publish it as a folder, not a single file: the video player's native libraries have to be real files.

**Installer:** `installer\build-setup.ps1` builds a single `pax-media-manager-setup.exe`. It installs for the current user without admin rights, and `--quiet` installs silently.

**Linux:** needs VLC installed (`sudo apt install vlc`). Not fully tested yet.

### API keys

Online artwork and ratings are optional. Paste your own keys in Settings (TMDB, OMDb, MDBList, OpenSubtitles). They're stored only on your PC and are never written to the shared settings file. Steam, Audible, AniList and TVmaze need no key.

## Code

| Folder | What's there |
| --- | --- |
| `Services/` | Torrent watcher, auto-mover, library scanner, metadata clients, caches, settings, watch history |
| `Views/` | Main window, library, details, Discover, dialogs |
| `Viewer/` | The video and picture viewer windows |
| `installer/` | The setup program and its build script |

Built with [Avalonia](https://avaloniaui.net/) 12 and [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp) on .NET 10.

This product uses the TMDB API but is not endorsed or certified by TMDB.
