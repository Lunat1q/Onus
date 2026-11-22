# 0003 — Windows.Media.Playback for audio, not VLC

**Status:** accepted (2026-11-22)

## Context
SunoSync's player used python-vlc, which needs a separately installed VLC of matching bitness. When it was missing
or mismatched, `play_file` silently returned False: the "player is broken" bug.

## Decision
Use the WinRT `Windows.Media.Playback.MediaPlayer` (TFM `net10.0-windows10.0.19041.0`). It plays MP3/WAV/FLAC/M4A
from `file://` and streams `https://` Suno CDN URLs directly, and its built-in System Media Transport Controls give
hardware media keys, the Windows media flyout and lock-screen controls for free. Playback failures surface as a toast.

## Alternatives
- **NAudio**: extra dependency, no SMTC, and streaming needs more code.
- **WPF `MediaElement`/`MediaPlayer`**: no SMTC integration, weaker error reporting.
- **LibVLCSharp**: brings back the large native dependency.

## Consequences
- Minimum Windows 10 2004 (19041).
- Events arrive on worker threads; `PlayerViewModel` marshals them to the dispatcher.
