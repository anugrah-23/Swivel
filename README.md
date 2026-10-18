# Swivel

**Convert, edit and move files without opening anything.** Drag a file, tap a key, drop it on a wheel.

Swivel is a free, open-source Windows tray app. Start dragging any file, tap **Alt** while you're
still holding it, and a wheel opens under your cursor. Let go on a wedge: *Boom. It's a WebP.*

- **Everything runs on your PC.** No uploads, no account, no telemetry.
- **Originals are never touched.** New files land next to them as `name.ext`, then `name (1).ext`.
- **Every job can be undone.** Undo sends the output to the Recycle Bin, or moves a moved file back.

## What's on the wheel

Tap the trigger again to cycle **Convert → Tools → Actions**. Wedges are ranked by how often you use them.

| | |
| --- | --- |
| **Images** | JPG, PNG, WebP, AVIF, HEIC, GIF, TIFF, BMP, ICO, SVG, PDF · compress, resize, smart crop, rotate, strip metadata, combine into one PDF |
| **Audio and video** | MP4, WebM, MOV, MKV, AVI, GIF · MP3, M4A, FLAC, WAV, Opus, OGG · compress, trim, mute, pull frames, extract audio |
| **Documents** | DOCX, Markdown, HTML, EPUB, TXT · DOCX, PPTX and XLSX to PDF |
| **PDF** | merge, split, compress, pages to PNG or JPG |
| **Archives** | ZIP, 7Z, TAR, GZIP · extract ZIP, 7z, TAR, GZ and RAR |
| **Actions** | copy path, copy file, move to, copy to, share, float, terminal here, reveal |

No drag needed: press **Ctrl+Alt+W** for the files selected in Explorer, **Ctrl+Alt+V** for whatever
you copied, or right-click files and pick **Spin with Swivel**.

## Install

Download `Swivel-win-Setup.exe` from the [latest release](https://github.com/anugrah-23/Swivel/releases/latest)
and run it. It installs per user, needs no admin rights, and updates itself. Windows 10 (2004 or
later) and Windows 11, x64.

Preview builds aren't code-signed yet, so Windows SmartScreen says "Windows protected your PC".
Choose **More info → Run anyway**. Until signed builds arrive, "Spin with Swivel" on Windows 11 sits
under **Show more options**.

HEIC photos use Windows' free HEIF and HEVC extensions from the Microsoft Store. Document conversion
(Pandoc, 40 MB) and Office-to-PDF without Microsoft Office (LibreOffice, 373 MB) download the first
time you need them, after asking.

## Build from source

Requirements: the .NET 10 SDK, and Visual Studio 2019 or later Build Tools with the C++ workload
(only for the NativeAOT Explorer menu DLL).

```powershell
powershell -File build/fetch-engines.ps1     # FFmpeg, Pandoc, 7zr, verified against build/engines.lock.json
dotnet build Swivel.sln
dotnet test Swivel.sln --filter "Category!=Perf"
dotnet run --project src/Swivel.App
```

- `build/pack.ps1` builds the installer into `artifacts/releases` (about 154 MB, fully offline).
- `build/register-shell-ext.ps1` registers the Windows 11 top-level menu for a dev build (needs Developer Mode).
- `Swivel.exe --snapshot <folder> <file>` renders the wheel, panels and windows to PNGs for design review.
- `SWIVEL_DATA_DIR=<folder>` runs an isolated copy with its own settings and history, as the UI tests do.

## How it's built

One C# / .NET 10 WPF process. A low-level keyboard hook catches the trigger tap mid-drag; a
transparent, never-focused overlay is the drop target; a job queue drives the engines and writes
each output under a temporary name, renaming it into place only when it's complete.

```
src/Swivel.App        tray, wheel, panels, toasts, flyout, settings, onboarding
src/Swivel.Core       catalog, file sniffing, job queue, history and undo, settings
src/Swivel.Engines    libvips, FFmpeg, Pandoc, Office/LibreOffice, PDFsharp/PDFium, SharpCompress/7zr
src/Swivel.Native     keyboard hook, hotkeys, Explorer selection, shell share, virtual-file drops
src/Swivel.ShellExt   NativeAOT IExplorerCommand for Windows 11's menu, plus its sparse package manifest
tests/                Core and engine golden-file tests, FlaUI UI tests
```

## Licence

Swivel is MIT-licensed. The engines it uses keep their own licences; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Pandoc (GPL) and FFmpeg (LGPL) run as separate
programs and are never linked into Swivel's code.
