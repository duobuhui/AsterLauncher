# Milestones

## Beta 0.1.0 - first public source and release candidate

- Use one version source for the assembly and display, with `v0.1.0-beta` as the first release tag.
- Add the project homepage and GitHub Releases update check to Settings. Full-package application and exact-base delta handling are implemented; a real remote update requires an uploaded release for validation.
- Build a one-EXE ZIP for public distribution. Include the attributed game art after the project owner confirmed redistribution authorization; preserve project-owned fallback styling and local custom-art support.
- Document the full-support checklist and an evidence-based capability matrix for all seven built-in games. Mark Endfield's tested installation as Bilibili B-channel without assuming the same executable proves channel.

## M1 - Buildable shell and contracts (this MVP)

- WinUI 3 shell, original Home and Game Library.
- Custom executable adapter and Endfield skeleton with manual selection.
- Observable, typed scan reporting with no guessed discovery rules.
- JSON persistence, file/in-app logging, process detection, and play history.
- Launch profiles, companion steps, MAA CLI templates, orchestration, and unit tests.

## M2 - Configuration depth

- Full launch-step editor with reorder, validation, and per-game defaults.
- Evidence-backed discovery rules gathered from real installations.
- Recovery UX for moved executables and richer session history.

## v0.3 - Navigation, clarity, and Endfield evidence (completed)

- Per-monitor DPI manifest and rasterization-aware initial window size.
- Persistent vertical game tabs, flyout search/filter, per-game secondary navigation, and in-place add-game form.
- Optional persisted custom icon glyph and artwork path.
- Endfield lookup through saved path, a running `Endfield.exe`, or an exact official-launcher uninstall entry plus a real-sample-verified relative path.
- Typed lookup diagnostics and deterministic locator coverage while preserving the original eight orchestration tests.

## M3 - Proven extension surface

- Add a second evidence-backed first-party game adapter.
- Validate optional `IGachaProvider` implementation outside the launch core.
- Reassess whether dynamic adapter loading is justified.

## v0.4 - Built-in catalog, media, tools, and UIGF (completed)

- Added compile-time adapters for Genshin Impact, Honkai Impact 3rd, Honkai: Star Rail, Zenless Zone Zero, Petit Planet, and Arknights without game-specific launch branches in the main page.
- Added packaged official-source hero media, verified local Hypergryph launcher icons, and gradient fallback behavior.
- Added official download entry points while deliberately deferring full publisher package/update clients.
- Added per-game tool presets for MAA/maa-cli, MaaEnd, March7thAssistant, and BetterGI.
- Separated Game Settings from Launcher Settings; added System/Dark/Light theme persistence and WinUI navigation transitions.
- Added UIGF v4.2 local archive, merge import, export, game-cache capture, privacy-safe diagnostics, and deterministic tests.

## v0.5 - Full-window hero and Endfield archive (completed)

- Extended the selected-game hero behind the title bar, game rail, and content without root scaling or a right-side mask.
- Replaced the heavy lower panels with bounded Acrylic time and command islands, plus a bottom-right split launch action.
- Added explicit 220 ms overview/feature/add-page transitions that follow the Windows animation preference.
- Persisted completed play sessions for last-session, current-week, and total summaries.
- Added Endfield local JSON merge/export and CSV export, plus a verified cache reader restricted to official record hosts and paths.
- Added an upstream/license ledger and a separate game-media redistribution review checklist.

## v0.6 - First-run discovery and window behavior

- Added a dedicated first-run guide for existing-install scans and the game directory, with a separate launcher background.
- Added bounded directory search for adapter-approved EXE names, including `StarRail.exe`, while preserving existing install locators and manual selection.
- Added saved game directory and X-button behavior settings, plus tray restore/exit through an isolated notification icon host.
- The saved directory is prepared for future in-app downloads; current publisher links still open the publisher installer and cannot force its destination.

## v0.7 - Theme and profile feedback

- Reused decoded local wallpaper images and layers during each running session; switching back to a game no longer reopens an unchanged image.
- Added direct profile creation, rename, and deletion, plus a Chinese execution-order projection that refreshes immediately after step changes.
- Added original color-only Typhon Purple, Elysia Pink, and Paimon White presets across the launch action, glass, selection accents, prominent controls, and page titles.
- Grouped launcher settings into app information, appearance, and window/path tabs; the project-homepage slot remains a placeholder until its URL is known.
- Made Endfield record sync retain valid groups if another official group is rejected, with partial-result wording and token-free diagnostics. A controlled official response showed that the first page must omit `seq_id`; the cache reader now also reuses the newest session token across discovered pools. An isolated WinUI sync on 2026-09-24 saved 420 character records and no weapon records; authorization fields were absent from the archive.

## v0.8 - Endfield six-star analysis

- Added a local analysis projection over saved Endfield character draws, excluding gift events and keeping pool counts separate.
- Replaced the flat six-star table with a compact, collapsible card per pool. The card header names its recorded six-stars; expanded rows have relative interval bars, portrait slots, UP markers for source-verified pools, and acquisition times. It refreshes after sync or import.
- Separately counts free and paid records, and labels the 80-draw six-star and single-pool first-120-draw UP guarantees as rules without claiming an exact live pity count.
- Shows the first recorded paid UP position within a source-verified pool; a free UP is reported without consuming the paid count.
- Kept derived statistics out of persisted files and documented that archive-based counts are not game pity values.

## v0.9 - Dense pool overview

- Moved the local archive summary into the title row and replaced the large statistics panel with three compact pity indicators.
- Filled wide layouts with two pool cards per row, with one reusable project-original card texture; kept Standard at the end and allowed narrow layouts to use one column.
- Expanded the verified pool catalog to announced Chartered pools since launch, the first Re-Factor series, and the independent celebration event. Unknown pools remain visible without guessed UP rules.
- Separated Standard, Chartered, Re-Factor, and event six-star counters; excluded free draws from guarantee counts while showing their six-star outcomes on each card.
- Preserved the compact non-card archive view for miHoYo games. Pity indicators are estimates from locally available records, not live game state.

## v0.10 - Counted pulls and pool images

- Corrected six-star row counts to exclude each pool's free draws; the existing Winter Hunt record at raw position 84 now reads 74 counted draws.
- Gave each announced pool its own official announcement banner and retained the original texture for Standard or unidentified pools. The project owner later confirmed redistribution authorization for the Beta 0.1.0 release.
- Made row bars show a visible 80-draw reference track and paid-draw progress; values above 65 use dark red.

## v0.11 - Game library management

- Replaced repeated rail status text with compact icons and tooltips.
- Added persistent visible/hidden game ordering, reversible removal of built-in games, and confirmed removal of custom games and their profiles.
- Added EXE-name recognition before custom game creation; recognized EXEs use the existing built-in adapter validation and restore that game's card.

## v0.12 - Full-width game library

- Expanded Game Library across the available content width, with two columns on wide windows and stacked sections on narrow ones.
- Added game icons in both lists and drag-based ordering in place of the row of arrow buttons.
- Added confirmed one-click cleanup for uninstalled visible games: hide built-ins, remove custom configuration and profiles, retain game files.
- Enforced a 960×540 DIP minimum client area at the window's DPI while preserving 16:9 interactive resize behavior.
