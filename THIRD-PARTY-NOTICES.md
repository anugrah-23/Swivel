# Third-party notices

Swivel is MIT-licensed. It ships or downloads the components below, each under its own licence.
Engines that run as separate programs (FFmpeg, Pandoc, 7-Zip, LibreOffice) are not linked into
Swivel's code; Swivel starts them as child processes.

## Shipped with Swivel

| Component | Use | Licence | Source |
| --- | --- | --- | --- |
| libvips 8.18 (via NetVips.Native) | Image conversion | LGPL-2.1-or-later | https://github.com/libvips/libvips |
| NetVips | .NET binding for libvips | MIT | https://github.com/kleisauke/net-vips |
| FFmpeg 8.1, LGPL shared build by BtbN | Audio and video | LGPL-2.1-or-later | https://ffmpeg.org, https://github.com/BtbN/FFmpeg-Builds |
| 7-Zip 7zr.exe | Creating .7z archives | LGPL-2.1 with unRAR restriction | https://www.7-zip.org |
| PDFsharp 6.2.4 | PDF merge, split, compress, images to PDF | MIT | https://github.com/empira/PDFsharp |
| PDFium (bblanchon builds) | Rendering PDF pages | Apache-2.0 / BSD-3-Clause | https://github.com/bblanchon/pdfium-binaries |
| SharpCompress | ZIP, TAR, GZIP, RAR and 7z reading | MIT | https://github.com/adamhathcock/sharpcompress |
| Microsoft.Data.Sqlite / SQLite | Job history | MIT / public domain | https://www.sqlite.org |
| H.NotifyIcon | Tray icon | MIT | https://github.com/HavenDV/H.NotifyIcon |
| Serilog | Logging | Apache-2.0 | https://serilog.net |
| Velopack | Installer and updates | MIT | https://velopack.io |
| Archivo, Space Grotesk, Space Mono | Fonts | SIL Open Font License 1.1 | Google Fonts |
| .NET runtime and WPF | Runtime | MIT | https://github.com/dotnet |

## Downloaded on first use (optional packs)

| Component | Use | Licence | Source |
| --- | --- | --- | --- |
| Pandoc 3.12.1 | Document conversion | GPL-2.0-or-later (separate executable) | https://pandoc.org |
| LibreOffice 26.2.6 | Office to PDF when Microsoft Office isn't installed | MPL-2.0 | https://www.libreoffice.org |

## Used from Windows, not shipped

HEIC photos are decoded by Windows' own HEIF and HEVC extensions. Swivel ships no HEVC decoder.
H.264 video is encoded by Windows' Media Foundation encoder where available.

Licence texts for the fonts are in the `licenses` folder next to Swivel.exe; FFmpeg's and 7-Zip's
are in their folders under `engines`.
