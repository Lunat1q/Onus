# 0005: Live analysis taps Onus's own audio via WASAPI process loopback

**Status**: accepted (Nov 2026)

## Context

The analyzer panel (levels, loudness, spectrum, pitch/key, stereo) needs the samples of whatever is playing. Audio
comes from two places: `Windows.Media.Playback.MediaPlayer` (no sample access) and suno.com's own player in a
hidden WebView2 (ADR 0004; encrypted stream decoded in the page). Hooking each separately would mean an
`AudioGraph` rewrite of the player plus a Web Audio `AnalyserNode` injected into Suno's page (fragile, and it
reroutes the page's audio).

## Decision

WASAPI **process loopback** (`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`, Windows 10 2004+) with
`INCLUDE_TARGET_PROCESS_TREE`, on two targets: Onus itself, and the WebView2 browser process
(`CoreWebView2.BrowserProcessId`). Measured: "process tree" reaches a target's direct children but **not
grandchildren**, and Chromium renders audio in its audio-service utility process (Onus → msedgewebview2 browser →
audio service), so tapping Onus alone hears nothing of suno.com's player. Captured as 48 kHz float stereo. Uses the `NAudio.Wasapi` package (`WasapiRecorderBuilder.WithProcessLoopback`)
instead of ~150 lines of hand-written COM activation interop. Capture runs while the analyzer panel is open or a
stream-only clip plays (for the analytics cache), and stops when neither holds and no capture is in progress.

## Consequences

- New dependency: NAudio.Wasapi (+ NAudio.Core), used only by `Services/AudioTap.cs`.
- Loopback hears the stream after the player's own volume but before the Windows master volume. The tap divides
  the applied volume back out (Onus's slider for local files; for the web player the `<audio>` element's real
  volume, which Suno sets on its own curve: slider 0.8 → element 0.46), so levels, LUFS and captures describe the
  song itself. Gain is capped at +26 dB.
- Other apps' audio is never analysed; Windows system sounds raised by Onus itself would be.
- `LoudnessMeter` uses the BS.1770 48 kHz K-weighting coefficients; the capture format is pinned to 48 kHz to match.
