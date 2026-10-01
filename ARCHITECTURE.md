# AsterLauncher architecture

AsterLauncher is an original .NET 10 / WinUI 3 application. Game-specific behavior belongs in adapters and independent services; views consume observable state.

## Project boundaries

- `AsterLauncher.Core`: domain models, adapter contracts, channel/installation identity and launch orchestration.
- `AsterLauncher.Infrastructure`: persistence, discovery, process management, gacha archives, resource providers and file maintenance.
- `AsterLauncher.App`: WinUI views, view models, navigation and dependency injection.
- `AsterLauncher.Tray`: notification-area integration.
- `AsterLauncher.Migrator`: package validation, migration, update commit and recovery.
- `AsterLauncher.Core.Tests`: deterministic tests of domain and infrastructure behavior.

`BuiltInGameCatalog` registers the built-in adapters. Adding a game should extend these contracts rather than add game-specific launch branches to a view.

## Launch and data

`GameLaunchOrchestrator` owns the sequence:

`BeforeGame -> Game -> AfterGame -> wait for game exit -> OnGameExit -> close owned companions`

External processes use structured argument lists. Cleanup targets only process instances started by the launcher; existing processes are never acquired for termination.

Configuration, logs, records and artwork live in the configured data root. `LauncherDataPaths` resolves the installation-root location file, the development override, or installation-root `Data`. Game-library ordering, hidden games and launch profiles are persisted through `JsonConfigurationStore`.

UIGF records and Endfield records use separate archive services. Import merges and deduplicates records. Genshin Impact, Star Rail and Zenless Zone Zero incremental capture use game-scoped account-UID/pool-type/record-ID checkpoints, finishes the entire boundary page, and stops older pages only for that pool. Full capture bypasses checkpoints; accounts and pools without history are scanned normally. Genshin raw types 301/400 share a checkpoint; other types stay independent. String and integer UIGF UIDs identify the same account. Authorization URLs, authkeys and tokens remain transient and are excluded from logs and archives.

`UigfGachaAnalyzer` produces per-game, per-account sections for the shared analysis page. Genshin/Star Rail ranks 5/4/3 and Zenless ranks 4/3/2 use different labels. First high-rarity intervals are local lower bounds; averages use only complete intervals. Banner breakdowns use actual gacha_id values, not guessed periods. Unrecognized and unverified special pool types remain visible without a fabricated pity maximum. The legacy Star Rail analysis API delegates to this engine.

`PortraitImageConverter` resolves a game/name key through the packaged index only. It uses original local images, supports source-listed aliases, and retains a glyph for unmatched names. Asset collection runs during development through `eng/update-uigf-artwork.cjs`, never while opening a record page.

## Artwork and tools

`WallpaperUpdateService` checks public publisher metadata at startup, caches changed artwork and preserves offline fallbacks. User-selected artwork takes priority. Switching games reuses cached images.

Companion-tool presets reference user-selected executables. They do not download or bundle those tools and follow the same process-ownership boundary.

## Endfield maintenance

`EndfieldDistribution.cs` defines channel, installation, manifest, predownload and progress models. Each channel owns its directory, version, task and launch profile. Legacy paths remain unclassified until trusted installation information or an explicit user choice identifies the channel.

`EndfieldDistributionProvider` fetches public metadata separately for update checks and complete manifests. It verifies encrypted manifest MD5 before decoding and rejects unsafe, duplicate or empty paths. `EndfieldNodeHttpHandler` uses a portable HTTPS process restricted to distribution hosts; resource URLs pass through stdin and remain transient.

`EndfieldDownloadService` keys content caches by expected MD5 and size. `EndfieldMaintenanceService` locks physical installation roots, guards running games, plans sharing before download, synchronizes target files and records the version only after final verification. Its journal stores installation/channel identity and content hashes rather than signed URLs.

`VerifiedFileCommit` generates an independent temporary file on the destination volume, verifies and flushes it, then replaces only the current directory entry. Updates, repairs and unsharing must use this layer and must never write through an existing hard link.

`EndfieldSharingService` is independent of the channel protocol. It restricts sharing to eligible VFS `.chk` resources, compares both target manifests, hashes local content and checks NTFS file identity. Source handles deny writes during conversion. Scanning and unsharing use actual link counts rather than trusting an index. External in-place writes cannot be controlled by the launcher; external maintenance requires unsharing.

Predownload only populates content caches. A formal update fetches fresh patch metadata before reuse. `EndfieldArchiveService` uses portable 7-Zip, validates archive paths and streams output into private staging before target-manifest verification. Split ZIPs are read as one archive from the first volume. Native HDiff/VFS patching currently falls back to target-manifest file synchronization.

`EndfieldMaintenanceViewModel` owns observable channel state. The primary button displays launch/update/download/progress/pause actions, and its existing expansion hosts maintenance controls. Async results update the originating channel; selecting another channel does not move a task.

## Launcher distribution and updates

The package root contains `AsterLauncher.exe`, `release-files.json`, `App/` and `MigrationTools/`. User data stays outside update packages.

0.1.2 requires a one-time standalone migration. Later multi-file updates use the format-2 manifest, full packages or exact-version file deltas. The migrator checks the outer ZIP hash, safe paths and per-file hashes, stages on the installation volume, commits with a recovery journal and preserves the previous files in `MigrationBackup`. Old and new update-manifest names remain separate to prevent incompatible packages reaching the old updater.

The migrator commits and recovers the root file manifest together with the application. Packages also carry App/install-manifest.json; the new entry point verifies its target files and restores the root manifest when upgrading through the 0.1.3 migrator, which omitted that file. Legacy recovery receipts remain supported.

## Activity, statistics and cleanup

`PlayActivity` divides finished sessions at local calendar-day boundaries, including daylight-saving transitions. It keeps cumulative legacy time without dated records separate instead of inventing activity. The launcher retains dated sessions for the year selector.

`GachaStatisticsAnalyzer` computes independent game/account/pool intervals. First recorded drops are lower bounds, excluded from means. Featured intervals accumulate confirmed non-featured drops; unknown results break the interval. Name/date rules account for selectable non-featured rosters. `GachaClassificationStore` keeps user corrections outside the original archive, keyed by game, UID, pool type and record ID. Unknown/special rules have no fabricated cap. Endfield statistics use paid/free and phase-aware summaries without scatter plots.

`EndfieldDownloadService` reports cumulative object bytes, including a resumed prefix and validated cache hits. `DownloadTaskCache` persists content references per installation and install/preload task; no signed URL is persisted. Pause keeps references and partials. Cancel waits for workers to stop, removes only unreferenced cache entries, and keeps recovery state if applying files already began. Successfully verified updates release consumed preload references. Formal patch downloads also report bytes before extraction.

`LocalStorageGate` excludes cleanup from active downloads, maintenance and OTA. `LocalStorageService` enumerates only named launcher-owned locations, excludes current state and referenced user paths, rejects reparse points, and protects indexed or legacy unfinished tasks. Deletion rescans the allowlist and checks volume/file identity, size and timestamp. A Windows delete handle denies writes/renames through unlink, without changing hard-link content or attributes. Estimated released space excludes links still owned elsewhere. Legacy configuration/archive backups are explicitly labeled and require the user's selected-file confirmation.
Completed game maintenance releases its content references and deletes unreferenced cache entries; pending preload and other installations retain theirs. Cache directories are created lazily. Storage scans include empty directories, and deletion prunes empty descendants within the selected owner boundary without traversing reparse points or removing configured user paths. Committed launcher updates remove their own GUID staging directory; failure and rollback material remain available. Successful integration probes remove their isolated runtime copies.

## HoYoPlay resource maintenance

`IHoYoDistributionProvider` and `HoYoDistributionProvider` resolve national official PC branches for every miHoYo game in the built-in catalog. `getGameBranches` and `getGameConfigs` establish the current version and entry; a missing public branch is an unavailable download, not a successful install. Old `getGamePackages` versions are not used when the current Sophon branch is newer. Branch credentials and signed resource URLs are transient and are not written into configuration, task journals or logs.

`HoYoManifestReader` independently reads bounded protobuf wire fields. Manifest decompression size and MD5, safe case-insensitive paths, unique file names, chunk continuity, sizes and compressed/uncompressed hashes are validated. `HoYoContentCodec` uses the already bundled Node runtime's Zstandard decoder. No new dependency or upstream launcher implementation is bundled.

`HoYoMaintenanceService` plans target-file synchronization, downloading only defective files' content-addressed chunks with four concurrent workers. It uses the existing resumable downloader in the separate `hoyo` cache bucket and the persistent task reference store. Download, cache verification, output assembly, safe replacement and final verification precede version persistence. Every output is private staging; `VerifiedFileCommit` replaces directory entries without mutating linked peer content. A physical root lock is shared with Endfield maintenance. The unfinished journal blocks launch and survives failures; staged outputs are removed after interruption, while verified chunks remain for resume. Successful tasks release caches; cancellation releases only the stopped task's references. Storage scanning protects both games' active and preload caches.

Updates deliberately use the current target chunk manifest rather than executing unverified HDiff / LDiff payloads. Repair refuses to silently upgrade or downgrade a different installed version. Preload caches missing future chunks and validates decoded content; it leaves game files and the installed version untouched. A formal update retrieves the current manifest again and reuses only matching hashes, so withdrawn or replaced packages cannot be applied as cached instructions.

`GameUserState.HoYoInstallation` adds installation/task identity, path, local version, audio selection and preload identity without rewriting existing paths, play history, profiles or gacha archives. The initial maintenance scope is national official servers; foreign channel installs are not converted. `HoYoMaintenanceViewModel` keeps per-game asynchronous state and routes to the existing primary action and launch-options surface. Network and maintenance operations stay outside MainWindow and the launch orchestrator.