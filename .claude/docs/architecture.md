# Architecture

```
src/Onus/
  App.xaml(.cs)          # composition root, Fluent dark theme + accent overrides, global crash handler, settings auto-save
  MainWindow.xaml(.cs)   # shell: sidebar nav, page host, lyrics/now-playing panel, toast, keyboard shortcuts, mini mode
  Core/                  # pure logic, no WPF (except Track/Settings being ObservableObject)
    ClipInfo.cs          # parse a Suno clip from any response shape; stems; WAV-url deep search
    ClipFilter.cs        # what to keep from the feed (liked, stems, trash, type, style include/exclude, search)
    FeedSource.cs        # library / workspace / playlist / search → page URL (+ search POST body, SearchRank)
    FeedScan.cs          # page loop + stop rules (empty page, max pages, playlist, Smart Resume)
    FileNames.cs         # sanitize, "Song v2.mp3" uniqueness, target folder rules
    TitleText.cs         # display title cleanup, genre/BPM from prompt, m:ss
    QueueNav.cs          # next/previous with shuffle + repeat
    AudioAnalysis.cs     # FFT, levels, log bands, pitch (HPS), harmonics, chroma, key, centroid, L/R correlation
    Loudness.cs          # BS.1770 LoudnessMeter: K-weighting, momentary / short-term / gated integrated LUFS
    Harmony.cs           # triad per chroma frame, per-song major/minor chord time, top chords, changes/min, energy → mood
    SongProfile.cs       # SongProfiler: the analyzer's song-level math over a whole file → SongProfile (mood, key, energy, LUFS)
    AnalyticsCache.cs    # {download folder}/analytics/{clip id}.m4a paths, completeness + seek rules
    Jwt.cs, RateLimiter.cs, JsonStore.cs (temp file + move), Log.cs, AppPaths.cs, Settings.cs, Models.cs
  Services/
    SunoAuth.cs          # hidden WebView2 on suno.com; asks Clerk for a fresh JWT on demand (ADR 0002)
    SunoWebPlayer.cs     # second hidden WebView2: plays "stream only" clips through suno.com's own player (ADR 0004)
    AudioTap.cs          # WASAPI process loopback of Onus + WebView2 browser → per-source rings, volume undo, loudness (ADR 0005)
    AnalyticsRecorder.cs # records an uninterrupted listen of a stream-only clip → .wav.part → AAC .m4a in the cache
    SunoClient.cs        # studio API; bearer on every call; 401 → refresh once; workspace 404 → playlist
    Downloader.cs        # resolve MP3/WAV, stream to .part, verify, move, tag, sidecars (.txt, .jpg, cover.jpg)
    TagIO.cs             # TagLib read/write; SUNO_UUID TXXX frame = the dedupe key; tag copy for Save as
    SongAnalyzer.cs      # decode a library file (Media Foundation → 48 kHz float stereo) through SongProfiler
    Exporter.cs          # Library "Save as": copy or convert to MP3/WAV/M4A via Media Foundation (NAudio), tags carried
    LibraryIndex.cs      # folder scan, cached by path+size+mtime
    Stores.cs            # TagStore (keep/star/trash), PromptStore, SunoSyncImport
  ViewModels/            # Main (nav/toast), Player, Library, Cloud(+CloudClip), PageViewModels (Dashboard, Vault, Settings),
                         # AnalyticsHub (app-lifetime owner of AudioTap + AnalyticsRecorder, 10 Hz feed)
  Views/                 # XAML pages, PlayerBar, AnalyzerPanel (+AnalyzerDraw, Scope), SignInWindow, MetadataDialog, MiniPlayerWindow, Converters
  Themes/Styles.xaml     # palette + pill/icon/chip/nav/row styles
  Assets/Onus.ico        # app icon (exe + windows); regenerate with tools/make_icon.py (Pillow)
tests/Onus.Tests/        # Core logic, SunoClient/Downloader against a stub HttpMessageHandler, TagLib round trips
```

## Request paths

```
Sync:   CloudVM ─▶ FeedScan ─page─▶ SunoClient ─Bearer─▶ studio-api
                      │ new clips                 ▲ token
                      ▼                     SunoAuth ─CDP Runtime.evaluate─▶ hidden WebView2 (suno.com, Clerk)
                  Channel ─▶ N workers ─▶ Downloader ─GET (no auth)─▶ CDN ─▶ .part ─▶ file ─▶ TagIO
                                                               └─▶ Downloaded event ─▶ Library rescan

Play:   Library/Cloud row ─▶ PlayerVM.PlayQueue ─▶ Windows.Media.Playback.MediaPlayer (file:// or https stream)
                                                     └─▶ SMTC (media keys, Windows flyout)
        stream-only clip ─▶ PlayerVM ─▶ SunoWebPlayer ─▶ hidden WebView2 suno.com/song/{id} (Suno's player)
                                          ▲ position/ended via postMessage; SMTC driven by hand meanwhile

Analyze: Windows media engine ◀─loopback(Onus)───────────┐
         suno.com <audio> (audio service) ◀─loopback(WebView2 browser tree)─ AudioTap ─▶ rings + LUFS
         AnalyzerPanel: 30 fps draw (while shown) ─snapshot─▶ AudioAnalysis ─▶ Scope elements
         AnalyticsHub: 10 Hz feed (panel open / stream-only playing / capture running) ─▶ AnalyticsRecorder
                       ─▶ .wav.part ─▶ AAC ─▶ Downloader.TagCaptureAsync ─▶ analytics/{id}.m4a ─▶ Cloud row
```
