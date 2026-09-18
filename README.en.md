# HashTool — MD5 / SHA-256 desktop tool for Windows

**Author: wangxiaobao** · A single **~130 KB** portable `.exe`. Drag files or folders in, get MD5 + SHA-256.

[![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6?logo=windows&logoColor=white)](#)
[![license](https://img.shields.io/badge/license-MIT-green)](LICENSE)

<img src="docs/icon.png" width="80" align="right" alt="HashTool icon">

**[中文说明在这里](README.md)**

## What it is

A tiny Windows GUI that computes **MD5 and SHA-256** for files and folder trees, with JSON export,
right-click copy, incremental refresh and a case toggle.

It is built **only from things that ship with Windows**:

| Layer | What is used |
| --- | --- |
| Compiler | `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (ships with Windows) |
| GUI | `System.Windows.Forms` (.NET Framework 4.x, built into Windows) |
| Hashing | `System.Security.Cryptography` — **the same API `Get-FileHash` and `certutil -hashfile` use** |

No Python, no Electron, no .NET SDK, no runtime to install, no third-party libraries, no NuGet packages.
One file, ~130 KB, starts instantly, writes nothing outside the folder you point it at.

![screenshot](docs/screenshot.png)

## Features

- Drag & drop files **or folders** anywhere on the window (recursive, skips symlinks/junctions)
- Computes MD5 **and** SHA-256 in one pass, 1 MiB chunks, multi-threaded (thread count selectable)
- Optionally compute only one algorithm (`MD5 only` — roughly 2x faster)
- **F5 incremental refresh**: only files whose size or last-write-time changed get re-hashed
- Export the whole table as JSON (UTF-8, includes size, mtime, state, duplicate flags)
- Rich copying: context menu, plus a configurable `Ctrl+C` mode (MD5 by default, or both hashes)
- Uppercase / lowercase display toggle — purely cosmetic, never triggers a re-hash
- Duplicate detection by hash (highlighted in green), sorting, filtering, progress, cancel
- Tolerant of locked files (`FileShare.ReadWrite | Delete`) — a locked file is reported, others continue

## Download

Grab `HashTool.exe` from [Releases](../../releases) and double-click it.
A `.sha256` file is attached to every release if you want to verify the download.

## Build from source

Nothing to install — Windows 10/11 already ships the C# compiler.

```bat
build.bat
```

The output is `dist\HashTool.exe`. The whole program is one source file (`src/HashTool.cs`).

Source code intentionally stays **C# 5 compatible** so the in-box `csc.exe` can compile it.

## Self-tests

```bat
dist\HashTool.exe --selftest                  :: known vectors + GUI construction + recursive scan
dist\HashTool.exe --uitest .\ui_out           :: full GUI run, 13 assertions, cleans up after itself
dist\HashTool.exe --screenshot docs\shot.png  :: renders the screenshot used in this README
```

CI runs all of the above on every push, and additionally cross-checks every digest against the
built-in `Get-FileHash`.

## License

[MIT](LICENSE) © 2026 wangxiaobao
