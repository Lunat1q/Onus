# 0002 — Suno auth through an embedded WebView2, not a Chrome extension

**Status:** accepted (2026-11-22)

## Context
Suno's studio API takes a Clerk session JWT that expires after ~60 s. SunoSync needed a companion Chrome extension
that pushed a fresh token to a local HTTP server (port 38945) every minute; a manually pasted token died mid-download.

## Decision
Onus signs in once in a WebView2 window (suno.com's own login page; profile persisted in
`%LocalAppData%\Onus\WebView`). A hidden WebView2 controller stays on suno.com and Onus asks it for a token when the
API needs one: `Clerk.session.getToken()` through the DevTools protocol (`Runtime.evaluate` with `awaitPromise`,
because `ExecuteScriptAsync` can't await), falling back to Clerk's `__session` cookie. Tokens are cached in memory
until 15 s before `exp`; a 401 forces one refresh.

Fallbacks for when embedded sign-in is refused: import the `__client` cookie value, or paste a bearer token.

## Alternatives
- **Keep the extension + token server**: works, but needs Chrome, a second install and a local open port.
- **Call Clerk's Frontend API directly with the `__client` cookie**: no browser, but depends on Clerk's private
  endpoints and Suno's Clerk domain, both of which have moved before.

## Consequences
- Needs the Edge WebView2 runtime (ships with Windows 11).
- A hidden browser tab costs some memory.
- If Google ever blocks sign-in inside embedded browsers, users fall back to the cookie import.
- Signed-out suno.com doesn't load Clerk at all, so "no Clerk" is reported as "Not signed in".
