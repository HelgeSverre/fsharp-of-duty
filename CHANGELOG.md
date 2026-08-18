# Changelog

## v0.0.2 — 2026-08-18

Server hardening for long-running public hosts, plus UI and packaging polish.

### Fixed

- Spawn selection no longer crashes the server after ~14 months of continuous uptime (32-bit tick overflow produced a negative spawn index).
- A simulation fault in one match no longer stops the other match or shuts down the host process.
- Dead peer connections free their player slot within ~30 seconds via server-side WebSocket keepalive, instead of holding it until TCP gave up (~15+ minutes); connections that never send a hello are dropped after 10 seconds.
- An emptied match returns to the Waiting phase (scores and grenades cleared) once every disconnected slot expires, so the two-player ready gate applies to each new group, not just the first after boot.
- Menu rendering cleanup: space-tick artifact, column layout, scrolling, and alignment.
- Angle-bracket glyphs added; HUD solid texel moved out of the space cell.

### Added

- Mouse hover and click support in the loadout picker and settings screen.
- Per-weapon-class damage falloff and a generated, sectioned arsenal page.

### Changed

- Tiny layout DSL now drives menu and HUD chrome.
- Arsenal callout copy toned down; docs gained a full controls table and measured system requirements.

## v0.0.1 — 2026-08-18

First tagged release of IRONSIGHT: a from-scratch F# WWII arena shooter with a procedurally generated client, an authoritative multiplayer server (team deathmatch and free-for-all), a Half-Life-style server browser backed by a community `servers.json` directory, platform installers (pkg/inno/deb), and the marketing website with a live server browser and leaderboard.
