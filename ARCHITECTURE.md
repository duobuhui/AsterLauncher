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

UIGF records and Endfield records use separate archive services. Import merges and deduplicates records. Star Rail incremental capture uses existing account-UID/warp-type/record-ID checkpoints, finishes the entire boundary page, and stops older pages only for that pool. Full capture bypasses checkpoints; accounts and pools without history are scanned normally. Authorization URLs, authkeys and tokens remain transient and are excluded from logs and archives.

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
