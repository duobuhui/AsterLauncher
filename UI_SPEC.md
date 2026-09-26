# AsterLauncher UI specification — liquid glass pass

## Wallpaper and window proportion

The wallpaper is measured from its source file, never inferred from a screenshot. The bundled Endfield image is 1600×900 (16:9); Genshin Impact, Honkai Impact 3rd, Honkai: Star Rail, and Zenless Zone Zero are each 2560×1440 (16:9). Arknights is 1277×749 (1.705:1). Custom artwork is a user-selected local image and may have any ratio.

The window uses a 16:9 client ratio while resizing and a minimum client area of 960×540 DIPs, scaled by the current window DPI. Arknights and custom artwork can have different ratios; both wallpaper image layers use `Stretch="Uniform"`, with a quiet dark fill around them. If artwork is unavailable, the game's adapter gradient is shown. UI measurements use DIPs, while initial physical window bounds use `XamlRoot.RasterizationScale`.

## Spatial structure

The selected wallpaper spans the title bar, game rail, navigation, and content. A light top/bottom gradient helps chrome contrast without covering the subject. On game change, the previous wallpaper stays visible while the next image loads, then the two layers crossfade for 240 ms. No full-window Acrylic or root scaling is used.

Each local artwork file is decoded once per launcher session while its file metadata is unchanged. Wallpaper layers stay available for return visits to a game, so switching back does not reopen the same source. No network wallpaper request occurs on a game switch.

The game rail starts expanded at 248 DIPs (284 on wide windows) and is only contracted by the user. Contracted width is 76 DIPs. Search, filter, add, settings, and logs remain available in both states. The compact rail suppresses the visible scrollbar but remains wheel- and keyboard-scrollable. Real game icons are clipped to a 10-DIP rounded rectangle without an outer tile frame; a slim left accent marks the selected row. Selected and unselected icon padding compensate for that accent so the icon centers align with compact system buttons. The rail width transition explicitly enables WinUI's dependent size animation after text visibility changes, avoiding squeezed text.

Expanded game rows keep the title and play time, while small status icons replace repeated running/install labels. Tooltips retain the full status. The rail's add action opens Game Library management across the full right content width. Wide windows place the visible list next to the hidden built-ins and EXE form; narrow windows stack them at full width. Both lists show the game's real icon or glyph and reorder by dragging a row. Built-ins can be hidden and restored; custom removal requires confirmation. The clear-uninstalled action confirms a count, hides visible uninstalled built-ins, and removes visible uninstalled custom configurations without deleting game files. The EXE form identifies supported games from their verified executable names and restores their built-in cards. Order and visibility survive restart.

The selected game's large title, publisher, and description are absent from Overview. Its artwork and selected rail item provide identity; the rail item also has a tooltip and automation name. The icon-only second navigation has tooltips and automation names.

## Overview command surface

The bottom command surface is the main interaction point. Collapsed, it shows only the 184+50-DIP primary launch button and arrow, without a surrounding frame. Expanded, the same bottom-right-anchored surface reveals the path, scan, manual path, more actions, and profile/client options. The four redundant status chips have been removed. A compact time capsule is always visible and opens last session, week, and total detail.

The arrow grows the same surface from 234×60 DIPs to at most 560 DIPs wide and roughly 264–302 DIPs high over 250 ms. Width and height opt in to dependent animations; glass tint and content opacity follow the same timeline. The launch button's screen position stays fixed during both directions of travel. No independent Flyout window is involved. With Windows animations disabled, the region changes immediately.

## Secondary pages

Secondary pages occupy the same content plane over a restrained translucent scrim. The page host enters with one 220 ms opacity/translation transition; individual rows are not animated. Returning to Overview reveals the wallpaper and dock in their original positions.

- Gacha uses a compact title row for the current game, local archive totals, and sync action. Endfield adds three small local-record counters for Standard draws since six-star, limited-pool draws since six-star, and remaining paid draws toward the recorded UP; the tooltip explains incomplete archives. Its pool cards fill one or two columns according to available width. Each announced pool uses its own official announcement image as a restrained header background; Standard and unknown pools use the original project texture. Newest pools lead and Standard stays last. Each collapsed card names its recorded six-stars and shows total, paid, and free counts, including six-stars from free draws. Expanding shows portrait placeholders, times, and verified UP markers. Row counts exclude free draws, and a free six-star does not reset the paid count. Each bar has a visible track and fills against an 80-draw reference; counts above 65 use dark red. The displayed 80/120 rules follow each verified pool category; the celebration event's 120-draw reward is a selection voucher. MiHoYo games use the same compact heading and archive controls without Endfield cards. Import/export actions follow below.
- Launcher Settings has Home, Appearance, and Window & Paths tabs. Home uses the available width for app/version information next to the GitHub homepage and update controls. Appearance includes System, Dark, Light, Typhon Purple, Elysia Pink, and Paimon White. Color presets tint the launch action, glass overlay, selected navigation/rail state, selected controls, text backing, and page titles.
- First launch shows a scrollable guide for scanning existing games and setting the game directory over a dedicated AsterLauncher gradient, without game artwork. Launcher Settings also exposes the game directory and X-button behavior.
- Tools uses information-dense rows with name, category, selected executable, and primary action.
- Launch Profiles keeps the selector, new/delete/rename controls, change-game-EXE action, and four generic stage-add controls in one rounded surface. The stage labels are Chinese. The live list and summary show execution order, including the game executable or a takeover step, for example `MAA → 游戏本体`. Add/remove updates immediately. MAA remains available through the tool/EXE path without a dedicated quick button. Changing the game EXE uses the existing adapter validation and persistence path.
- Game Settings keeps executable and artwork controls separate from launcher theme settings, and starts at the same left content inset as other game pages.
- Logs uses compact column-aligned event rows and a small path heading.

The surfaces share tint, edge highlight, and readable text tokens while preserving each page's own information hierarchy. Content scrolls when viewport size or system scaling reduces available space.

## Validation targets

Inspect the actual running window at 1280×800 and narrower equivalent viewports, including high DPI. Confirm wallpaper edges, game-rail selection, complete launch controls, expanded panel width, and secondary-page scrolling. Verify build, existing unit tests, and app startup separately.

## Beta 0.1.1 settings and runtime state

- "窗口与路径" shows the current configuration file, a data-folder picker and a "迁移并重启" action. The target must be an existing empty directory. The operation copies configuration, logs, gacha archives and artwork, keeps the source as a backup, records the selected path beside the EXE and restarts.
- A clean single-file release writes app data beside the installed EXE. The .NET runtime may extract native dependencies elsewhere; that location is not shown as the app data root.
- Home and Game Library launch controls reflect the selected game only. Switching to another game while a launch session remains active restores that game's independent button and running indicator; each game can have its own active session.