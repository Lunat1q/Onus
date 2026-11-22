# 0001 — WPF on .NET 10 with the built-in Fluent theme

**Status:** accepted (2026-11-22)

## Context
SunoSync is Python + CustomTkinter. The goal is a standalone Windows app with a good-looking UI that is easy to
build and ship as one exe.

## Decision
WPF on .NET 10, `Application.ThemeMode="Dark"` (WPF's own Fluent theme) for control templates, plus our own palette
and a few custom styles (pills, chips, nav, song rows) in `Themes/Styles.xaml`. Fluent's accent keys are overridden in
`App.xaml` so sliders/checkboxes use the app purple. MVVM via CommunityToolkit.Mvvm source generators. No DI container.

## Alternatives
- **WinUI 3 / Windows App SDK**: nicer controls, but packaging/deployment friction for an unpackaged single exe.
- **WPF-UI / MahApps**: extra dependency for what the built-in Fluent theme now covers.
- **Avalonia**: cross-platform not needed.

## Consequences
- `ThemeMode` is flagged experimental (WPF0001, suppressed). Fluent resource keys may change between .NET releases;
  the overrides in `App.xaml` are the only coupling.
- Field-based `[ObservableProperty]` raises MVVMTK0045 under a Windows TFM; suppressed because we don't AOT-compile.
