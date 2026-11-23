# Domain

## Flows

- **Sign in**: SignInWindow shows suno.com in WebView2 (shared profile in `%LocalAppData%\Onus\WebView`). It
  polls for a Clerk session and closes itself. The hidden controller then answers every token request
  (`SunoAuth.GetTokenAsync`), cached until 15 s before the JWT's `exp`. Fallbacks: import the `__client` cookie
  value (Settings → Trouble signing in), or a manual bearer token (expires in ~60 s, last resort).
- **Fetch**: scan pages of the selected source, list every clip that passes the filter; rows say *New* or *On disk*.
  Any row can be streamed; on-disk rows play the local file.
- **Search**: source *Search all of Suno* → term + sort, Fetch walks 5 pages (100 songs) from Start page. Local
  filter box hidden (the server matched the term); Sync disabled. Others' clips are usually *Stream only*.
- **Sync new**: same scan with Smart Resume, and new clips go straight into a download channel while scanning
  continues (N parallel workers, rate limited).
- **Download**: refetch clip details if the list omitted the prompt → resolve audio (WAV: known url or Suno's
  convert + poll ≤ 2 min, else MP3) → `.part` → size check (≥ 1 KB, = Content-Length) → move under a free name →
  tags (title, artist, style, year, comment, lyrics, `TXXX:prompt`, `TXXX:SUNO_UUID`, cover) → `.txt` + `.jpg` +
  `cover.jpg`.
- **Dedupe**: a clip is "on disk" when some file in the library folder carries its id in `TXXX:SUNO_UUID`
  (same frame SunoSync wrote, so existing SunoSync folders are recognised).
- **Live analysis** (chart button / `A`): panel slides up above the player bar; capture starts on open, stops on
  close. Levels (RMS + 1.5 s peak hold), BS.1770 loudness (momentary, short-term, integrated per song, max peak,
  PLR, offset to the −14 LUFS streaming target, clip count), 72-band spectrum with harmonic markers of the detected
  fundamental + spectrogram, brightness (spectral centroid), pitch (HPS, ±cents), H1–H8, chroma, key estimate from
  ~8 s of chroma, goniometer + L/R correlation + width. Chords · mood: current major/minor triad (~0.5 s chroma, held
  0.3 s to count), share of chord time in major vs. minor, top 4 chords, chord changes/min, energy (integrated LUFS +
  average centroid) and after 15 s of chords a mood (major share × energy: happy/warm/bittersweet/dark/sad). Rough
  guide: triads only, and rich minor chords can read major (root's 5th partial = major third). Tallies run only while
  the panel is open. Song change resets integrated/peak/clips, the key and the chord tally.
- **Analytics cache**: every stream-only clip played via Suno's player is recorded from 0:00, analyzer open or not
  (REC % in the spectrum header when it is). A seek / Previous, leaving early or a lost buffer drops it; pauses are fine. Complete
  (within 0.5 s of duration) → AAC 192 kbps `{download folder}/analytics/{clip id}.m4a`, tagged like a download
  (title, artist, style, year, comment, prompt, lyrics, `SUNO_UUID`, cover; details refetched if the list lacked them). The Suno row then says
  *Analytics cache* and plays that file through the Windows player (no WebView). Library scan and Sync ignore the
  folder; a real download still wins (*On disk*).
- **Marks**: keep / star / trash, keyed by Suno id (path for non-Suno files), same `tags.json` shape as SunoSync.
  Trash-marked songs are hidden from "All" and from Home stats.

## Suno API notes

- `GET /api/feed/?page=N` is **0-indexed** (page 0 = newest). `/api/feed/v2?is_public=true` likewise.
- `GET /api/project/{id}?page=N` is 1-indexed; a 404 there usually means the id is a playlist.
- `GET /api/playlist/{id}/` returns everything in one response (no paging used).
- Search: `POST /api/search/` with `{"search_queries":[{"name":"public_song","search_type":"public_song","term":"…",
  "from_index":N,"rank_by":"…"}]}` → `{result:{public_song:{total_hits,result:[clips]}}}`, 20 per call, 0-based
  `from_index`. Empty term = all public songs. `rank_by`: `most_relevant`, `most_recent`, `play_count`,
  `upvote_count` (`created_at` → 422). Bursts get 429 (retried like a 5xx). suno.com's own search page uses
  `suno.com/a/api/unified/feed` instead (not used: the bearer only goes to studio-api).
- Lists: `/api/project/me?page=N` → `{projects}`, `/api/playlist/me?page=N` → `{playlists}`; stop on empty page.
- WAV: `POST /api/gen/{id}/convert_wav/`, then poll `GET /api/gen/{id}/wav_file/` (404 while rendering).
- Liked = `is_liked` or `reaction.reaction_type == "L"` or `vote == "up"`; disliked = `"D"` / `"down"`.
- Clerk JWTs live ~60 s. Never store one; ask for a fresh one.
- Accounts without unlocked downloads (`is_download_unlocked: false`, seen Nov 2026 on a free plan) get
  `audio_url = https://studio-api.prod.suno.com/api/forbidden` on every clip (feed v1 and `suno.com/a/api/feed/v3`
  alike). suno.com then plays `media_urls[].url` (`d2lwuy8qc234o3.cloudfront.net/1/clip/{id}.m4a`, "m4a-opus",
  `encoding: 1.0.0`), which is **encrypted** (no MP4 header) and decrypted in the site's JS. Onus does not decrypt
  it: such clips show as *Stream only*, are skipped by Sync and play through suno.com's own player in a hidden
  WebView2 (ADR 0004). Untested: whether the site's
  "Download" action (`download_song`, enabled in `action_config`) yields a plain file for these accounts.

## Known gaps / ideas (deliberately left out)

- Discord Rich Presence, Sentry, the self-updater (pointed at upstream), "radio"/"mobile bridge" from the old README
  (no code existed for them).
- The SunoSync Chrome extension token server (port 38945): WebView2 sign-in replaces it. Re-add a small listener if
  embedded sign-in turns out to be blocked for many users.
- Playlists over one response page are not paged.
- Library scan runs on demand (startup, after downloads, Rescan button), no file watcher.

## Operational lessons

| Symptom | Cause | Guard |
|---------|-------|-------|
| SunoSync player silent | python-vlc needs a matching VLC install; play errors were swallowed | Windows' own media engine, failures shown as a toast |
| SunoSync scan never ends | page loop had no stop on an empty page when Max pages = 0 | `FeedScan` stops on empty page (test) |
| SunoSync downloads stall after ~1 min | Clerk JWT expires in ~60 s; only the extension refreshed it | token fetched on demand, 401 → refresh once (test) |
| Crash at startup; later 1 of 4 songs listed, "0 songs" | Clear/Add on the source collection inside `DeferRefresh` desyncs the view | `Load` builds a new collection + `ListCollectionView` each time |
| Stream-only song never starts; page audio stuck at `readyState` 0, no stream request | Chromium defers media loading in a never-shown page (hidden WebView2 controller) | web player view shown at 1×1 px, occlusion tracking off (ADR 0004) |
| Crash dialogs when opening the analyzer | a Height animation laid the scopes out at a few px → negative `Rect` | panel slides in (TranslateTransform); `Scope` skips drawing below 16×14 |
| Analyzer flat while a stream-only song plays | process loopback on Onus doesn't reach grandchildren; Chromium's audio service is one | second loopback on `CoreWebView2.BrowserProcessId` (ADR 0005) |
| Stream levels ~20 dB low (peak −22 dBFS) | Suno's page sets `<audio>.volume` on its own curve (0.8 → 0.46) | page reports its real volume; tap divides it out |
| Captures still ~13.5 dB low after the volume fix (−27 LUFS, peaks −15 dBFS); mood Energy never above 50 % | process loopback asked for stereo on a 7.1 device: Windows' 8→2 downmix scales front L/R by ~0.21 | capture in the device's channel count, keep front L/R (`AudioTap.FrontPair`, test); measured equal to the speaker feed |
| Analytics-cache songs far quieter than the stream (−27 LUFS, peaks −13…−16 dBFS) | every cached file was recorded by 1.0 with the 7.1 downmix bug above; the fix only helps new captures | one-time purge of `analytics/*.m4a` per folder (`.level-v2` marker, startup + capture start, test); songs re-record on next listen |
| Analytics capture never saved: "could not create a sink writer" | Media Foundation picks the container from the extension; temp name was `.m4a.part` | encode to `{id}.encoding.m4a`, then move |
| REC died when the analyzer was closed and reopened | capture lived and died with the panel's visibility | capture moved to the app-lifetime `AnalyticsHub`; the panel only draws |
| No REC after clicking another song mid-song | old song page kept reporting its position (0:45) until navigation; recorder only tried to start on the tick the id changed | recorder starts whenever the song is at 0:00 and not yet cached (test) |
| `CallDevToolsProtocolMethodAsync` E_INVALIDARG | called before the hidden page committed its first navigation | wait for first `NavigationCompleted` |
| Sync "never finishes"; rows just say "Failed" | every MP3 url was Suno's `/api/forbidden` placeholder (403); each was retried 3× with no reason shown | 4xx fails at once (test); row shows `Failed · 403`, tooltip + log name the refused host/path (query left out) |
