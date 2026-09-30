# Milestones

## Beta 0.1.2 - publisher wallpaper refresh and bundled portraits

- Check the supported publishers' current wallpaper metadata once per startup. Download only changed JPEGs to the configured local data directory, keep the last valid cache for offline use, and leave user-selected art in priority. Switching games performs no wallpaper request.
- Keep game identity icons and Endfield pool images bundled. Bundle 88 official Star Rail character portraits and 33 official Endfield operator portraits, with an exact official source manifest and glyph fallback for unmatched records.
- Carry the unreleased Re-Factor phase grouping and Star Rail history display into this beta after build, tests, packaged-app launch, and screenshot verification. Live Star Rail sync remains dependent on the game's current authorization cache and official endpoint response.

## Beta 0.1.1 - portable data and launch correction

- Resolve persistent data from the installed EXE directory, migrate the previous single-file temporary data copy on first use, and allow relocation to a selected empty directory.
- Query all six mainland China Star Rail gacha types through the working getGachaLog endpoint. Verify with a local fake-server regression test and an authorized, return-code-only official API probe.
- Track launch sessions per game so a running game does not block another game or mark it running after selection.
- Package a full release and an optional exact-base delta from 0.1.0-beta for OTA testing. A remote OTA remains unverified until its GitHub Release assets are uploaded and the installed 0.1.0-beta client exercises it.

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

## Beta 0.1.1 follow-up (included in Beta 0.1.2)

- Pace HoYo history requests and retry temporary `-110` responses for ordinary pools. Star Rail categories 21/22 may still return `-110`; report their unavailability and retain records from successful categories.
- Default Endfield history sync to a local-record checkpoint, with a separate full-sync button for older gaps. Record identity includes pool and sequence so one pool does not stop another.

## Re-Factor pool follow-up (included in Beta 0.1.2)

- Key Endfield Re-Factor archive records and phase projections by `poolId` plus `poolVersion`, so later same-name phases retain separate lists even if the raw pool ID repeats. Display one named-series card with the newest phase visible and older phases behind an inline expander.
- Carry the shared 80 six-star count across all Re-Factor phases; carry the first 120 paid UP count only across a same-name series and show both in the compact top strip. Exclude free ten results. Unknown future Re-Factor IDs use generic rules without guessing their featured operator or banner.
- The 2026-09-28 local archive contained only `绚丽异彩` phase 1. Multi-phase grouping and history expansion were checked with synthetic records; an actual later-phase sync remains unverified.

## Star Rail history display follow-up (included in Beta 0.1.2)

- Added a read-only UIGF hkrpg analysis and dense Star Rail page with separate per-UID character, light-cone, Standard, departure, and special-category groups. Banner cards group exact gacha_id; five-star intervals and local pity waterlines carry across IDs within the same ordinary category.
- Replaced large banner cards with visible five-star progress rows, compact rarity and average statistics, and a secondary exact-banner-ID expander. Character history fills two row columns when room allows; the other categories stack beside it. Rows reserve portrait space and fill a 90/80 reference bar; high counts turn dark red.
- A missing banner-name/featured-item catalog means UP win/loss and next-featured guarantees remain unclassified. The first locally observed interval is a lower bound. The page does not change Star Rail capture behavior; the user's real-time sync failure needs separate validation.
- Tested analysis with independent accounts/categories and an across-banner five-star interval. Previewed the WinUI page from an isolated copy of an existing 1,118-record UIGF archive; actual in-game sync and multi-account UI remain unverified.

## 0.1.3-beta gate: Endfield two-channel maintenance

Development work has connected channel-specific state and UI, publisher manifest parsing, per-file resume/repair, guarded file commits, and an NTFS hard-link candidate engine. E-drive fixture tests and WinUI screenshot checks cover the implemented paths. A 0.1.3-beta release is not ready: the official multipart installation format and formal patch application are unfinished, the app HttpClient could not complete a new publisher TLS request, while an independent Python TLS probe confirmed both current manifests and one 528-byte verified file per channel on 2026-09-29; neither a full game install nor launch was exercised. The requested multi-file app distribution also needs an OTA migration path from the currently released single-EXE updater. A 2026-09-29 Release publish with PublishSingleFile=false launched successfully in an isolated E-drive data directory, but its publish root contained 381 files and 100 directories. The current ApplyUpdate.ps1 rejects any full ZIP entry except AsterLauncher.exe, so merely uploading that publish directory would fail existing-client updates and violate the requested tidy EXE directory. Do not tag, publish, or claim game-download support until these gates pass.

### Multi-file migration checkpoint (2026-09-29)

A standalone migrator, tidy root apphost, file-hashed multi-file package builder and E-drive fixture regression script now exist. The real 0.1.2 ZIP was migrated in an isolated directory; the old EXE hash was retained in `MigrationBackup`, the new root entry launched the WinUI app, and a custom data directory remained active. The migration script covers bad outer/inner hashes, ZIP traversal, backup and recovery. This removes the local layout and one-time manual-migration prototype gap. It does not make the existing 0.1.2 single-file updater understand a multi-file package, and the Endfield install/patch/TLS and real game acceptance gates above still prevent a 0.1.3-beta release.
## 0.1.3-beta 发布验收

- 代码：终末地一个页面承载两渠道；官方逐文件安装/更新/修复、内容续传缓存、预下载及正式更新缓存复用、真实 NTFS 共享/扫描/解除共享、安全文件提交和未完成任务恢复。维护区接入启动按钮展开位置并沿用原样式。
- 交付：多文件 ZIP、独立迁移工具、文件差量更新和数据位置兼容。旧单 EXE 更新器需一次迁移，不发布不兼容的旧版 OTA 清单。版本标签由 CI 构建并上传核验过的 beta 附件。
- 自动化：硬链接卷/File ID 与单侧替换/删除、修复同大小损坏、下载 Range/签名/缓存、旧配置、预下载转正式更新、分卷/加密 ZIP、失败恢复、迁移和多文件完整/差量更新均在隔离目录覆盖。
- 实际运行：WinUI 打开维护展开区、两服切换标识/路径/实时版本并截图检查；启动器实际传输服务取得两服官方清单及小文件，校验 MD5。
- 仍需实机：两服完整下载后的游戏启动、真实预下载窗口、正式 HDiff/VFS 增量样本、游戏自身及官方启动器对 .chk 的写入行为。当前增量输出回退为目标文件同步；未把模拟验收记作真实游戏启动。
