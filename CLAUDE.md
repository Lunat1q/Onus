# Onus

Native Windows rework of [SunoSync](../SunoSync) (Python/CustomTkinter): download, browse and **play** your Suno
library. WPF on .NET 10, single window, no server.

@.claude/docs/architecture.md
@.claude/docs/domain.md

## Commands (run from repo root)

| What | Command |
|------|---------|
| Build | `dotnet build Onus.slnx` |
| Test | `dotnet test Onus.slnx` |
| Publish (single exe, self-contained) | `dotnet publish src/Onus -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish` |

Keep the PC usable: build at Idle priority with `-m:2` (see global CLAUDE.md).
Agents build and test; launching the app is only for a quick visual check (screenshot with `PrintWindow`, never
`CopyFromScreen` — it captures whatever window is on top).

## Stack

- WPF, .NET 10, `ThemeMode="Dark"` (built-in Fluent theme) + own palette in `Themes/Styles.xaml`. Icons: Segoe Fluent Icons.
- TFM `net10.0-windows10.0.19041.0` for WinRT `Windows.Media.Playback` (player, media keys) — ADR 0003.
- WebView2 for Suno sign-in and token refresh — ADR 0002.
- CommunityToolkit.Mvvm (source-generated INPC/commands), TagLibSharp (ID3), xUnit + FluentAssertions.

## Principles

- Feature pages = ViewModel + View. Pure logic lives in `Core/` as static, directly testable units.
- ~300 lines per file. No DI container: `App.xaml.cs` is the composition root.
- `// ponytail:` marks deliberate shortcuts with their ceiling.
- Every change ships with tests; every bug fix with a regression test.
- Never log tokens or cookies. The Suno bearer token goes only to `studio-api.prod.suno.com`, never to CDN hosts
  (a test guards this).
- State lives in `%LocalAppData%\Onus` (settings, tags, prompts, library cache, WebView profile, log).

## Docs to update

| Change | Update |
|--------|--------|
| New file / moved responsibility | `.claude/docs/architecture.md` |
| Behaviour, Suno API quirk, incident | `.claude/docs/domain.md` (flows, known gaps, operational lessons) |
| Decision that would hurt to reverse | new `.claude/docs/adr/NNNN-*.md` |

Definition of done: `dotnet build` (warnings are errors) and `dotnet test` green.
