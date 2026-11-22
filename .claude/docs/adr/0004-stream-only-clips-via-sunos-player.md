# 0004: Stream-only clips play through suno.com's own player

**Status**: accepted (Nov 2026)

## Context

Accounts without unlocked downloads get `audio_url = .../api/forbidden`. The only other audio Suno lists
(`media_urls`, "m4a-opus", `encoding: 1.0.0`) is encrypted; suno.com decrypts it in its own JavaScript.

## Decision

Onus does not decrypt it. Such clips play in a second hidden WebView2 (same profile as sign-in) that opens
`suno.com/song/{id}` and presses the page's Play button. An injected script only drives the page's `<audio>`
element (play/pause/seek/volume) and posts position/ended/error back. The view is shown at 1×1 px in the window corner:
Chromium never loads media in a page that was never shown (a hidden controller stalls at `readyState` 0, not even
fetching the stream). The shared WebView2 environment runs with `--autoplay-policy=no-user-gesture-required`,
without native window-occlusion tracking (a minimized Onus would count as hidden again) and without Chromium's
media-key/SMTC hookup, so Onus's player keeps
the media keys and Windows flyout (driven manually via `SystemMediaTransportControls` while the page plays).

## Consequences

- Stream only: these clips still can't be downloaded.
- Depends on suno.com's song page markup (`button[aria-label="Play"]`); a redesign shows a "Suno's player: …"
  error until `SunoWebPlayer.HookScript` is updated.
- Changing the environment's browser arguments needs every Onus instance closed (WebView2 refuses a shared user
  data folder with different arguments).
