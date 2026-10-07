# Asset sources

Beta 0.1.0 source control and the single-file Release include 26 publisher-media files plus the original `endfield-pool-card.svg` listed below. On 2026-09-24 the project owner confirmed redistribution authorization for this release. The publisher images' copyright remains with their respective owners; neither the project's source-code license nor another repository's MIT license grants rights to reuse them. This file records each source rather than treating a URL as a license.

AsterLauncher contains no Starward artwork, icons, or branding. Packaged game media has an explicit source and may be replaced by users through Game Settings.

The v0.7 theme presets are original color definitions in XAML/C#. Their names do not add any game artwork, logos, or other third-party media.

## Beta 0.1.2 publisher media update (2026-09-28)

- The launcher checks wallpaper metadata once after startup. HoYoPlay's [official game list](https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn) supplies the current background URLs for 原神、崩坏3、星穹铁道、绝区零、星布谷地. [终末地官网](https://endfield.hypergryph.com/) supplies the newest version-update cover; [明日方舟官网](https://ak.hypergryph.com/) supplies its current preloaded homepage hero. The returned publisher CDN images are converted to JPEG by the publishers' image endpoints and cached under `Data/artwork/cloud/`. These runtime cache files are not part of the Git release. An unchanged source URL reuses the cached file; a failed check keeps the previous image. User-selected artwork and user data artwork take priority, followed by the cloud cache and then the attributed packaged fallback below. This is a public-media request, with no account authorization data.
- Sidebar/game-library icons stay in the package. The existing 13 Endfield pool banners and original fallback texture also stay in the package; neither is fetched at runtime.
- The release now bundles **88 Star Rail character portraits** from the [official character page](https://sr.mihoyo.com/character) and its [publisher content service](https://act-api-takumi-static.mihoyo.com/content_v2_user/app/1963de8dc19e461c/getContentList?iChanId=253&iPageSize=999&iPage=1&sLangKey=zh-cn), plus **33 Endfield operator portraits** from the [official operator page](https://endfield.hypergryph.com/operator). Each downloaded file's exact official CDN URL, character name, and source page are recorded in [`portrait-index.json`](src/AsterLauncher.App/Assets/Games/Gacha/Portraits/portrait-index.json). `eng/update-character-portraits.ps1` refreshes this index from those publisher sources. These images are publisher artwork, with copyright retained by miHoYo/HoYoverse or Hypergryph. Packaging follows the project owner's confirmed redistribution authorization; the AsterLauncher or Starward source license does not grant image rights. Unknown characters and light cones use the existing glyph fallback.

The 2026-09-24 Endfield record-response investigation used no media or artwork and introduced no new packaged assets. Publisher media rights remain as described below.

The 2026-09-28 Re-Factor phase and pity update studied the official [rules](https://endfield.hypergryph.com/news/4776) and [绚丽异彩 notice](https://endfield.hypergryph.com/news/2651). It introduced no new images or packaged assets. The existing `refactor-vivid.png` remains attributed below; its publisher rights are unchanged. Unknown future Re-Factor cards use the original `endfield-pool-card.svg` until a verified banner and rights record are added.

The six-star analysis table uses only local record text and the project's existing color resources. No GitHub artwork, operator portraits, or third-party UI assets were imported.

The full-width game library reuses the existing packaged game icons through `GameCardViewModel`; it introduces no new media. Its drag grip and clear icon use text and Windows system glyphs. Existing publisher-icon rights below are unchanged.

## Hypergryph

- `endfield.ico` and `arknights.ico`: copied from the locally installed official 鹰角启动器 resource directory `D:\Games\Arknights Endfield bilibili\1.6.0\res\icons` on 2026-09-21. They are used only as 42-DIP game identity icons.
- `endfield-hero.jpg`: official Endfield CDN image, 1600x900: `https://web.hycdn.cn/upload/image/20260824/8added9b9b315ede92db1c92804667fa.jpg`.
- `arknights-hero.jpg`: official Arknights CDN image, 1277x749: `https://web.hycdn.cn/upload/image/20260828/e59e8bf7c4da71eba98930cb5a2d4eb4.jpg`.

## HoYoPlay launcher backgrounds

The four 2560x1440 PNG files are lossless local conversions of pure-background WebP captures from the UIGF-org `HoYoPlay-Launcher-Background` archive, which records the images delivered by HoYoPlay. PNG is packaged because the tested WinUI image decoder did not display WebP on this machine. The archive repository is MIT-licensed, but that does not relicense the game-publisher artwork. Repository: `https://github.com/UIGF-org/HoYoPlay-Launcher-Background`.

- `genshin-impact.png`: `output/hoyoplay_cn_pure/hk4e_cn/2026/07/22/a36b618a05cc80cb87c2955351abd67b_2358889847008472507.webp`
- `honkai-impact-3rd.png`: `output/hoyoplay_cn_pure/bh3_cn/2026/07/16/ffdd42e098046b7bea134907a2ea04b2_352028468613440724.webp`
- `honkai-star-rail.png`: `output/hoyoplay_cn_pure/hkrpg_cn/2026/08/17/b3811afc4a6a4e7e3879c830329f8ae2_4041979032811000021.webp`
- `zenless-zone-zero.png`: `output/hoyoplay_cn_pure/nap_cn/2026/08/26/86fa3b52c96b8835f8b7f1f967efac28_8820261911238438183.webp`

Petit Planet retains the original AsterLauncher gradient as its packaged offline fallback because no stable production executable contract was verified. The current HoYoPlay image can be cached at runtime. Custom user artwork always takes priority over downloaded and packaged media.

## Official website identity icons (retrieved 2026-09-23)

The following sidebar icons were downloaded from the games' official websites and converted losslessly to PNG for WinUI decoding. Website favicon/touch-icon art is publisher artwork, not covered by this project's code license. The project owner's 2026-09-24 redistribution confirmation applies to this release.

- `genshin-impact-icon.png` (256x256): official Genshin Impact touch icon, `https://ys.mihoyo.com/main/icon.png` (linked from `https://ys.mihoyo.com/`). The icon is time-specific anniversary artwork and may be replaced upstream.
- `honkai-impact-3rd-icon.png` (119x119): official Honkai Impact 3rd favicon, `https://bh3.mihoyo.com/favicon.ico` (linked from `https://bh3.mihoyo.com/`).
- `honkai-star-rail-icon.png` (120x120): official Honkai: Star Rail favicon, `https://sr.mihoyo.com/favicon-mi.ico` (linked from `https://sr.mihoyo.com/`).
- `zenless-zone-zero-icon.png` (64x64): official Zenless Zone Zero favicon, `https://juequling.mihoyo.com/favicon-mi.ico` (linked from `https://zzz.mihoyo.com/`).
- `petit-planet-icon.png` (400x400): official Petit Planet website favicon, `https://planet.hoyoverse.com/favicon.png` (linked from `https://planet.hoyoverse.com/zh-cn/home`).

For later versions or additional assets, review the scope of permission again. See `THIRD_PARTY_NOTICES.md`.

## Endfield gacha cards (2026-09-24)

- `endfield-pool-card.svg`: original geometric texture drawn in this project, now used by Standard and unrecognized pools. No game image, publisher artwork, third-party code, or Starward asset was copied into it. It may be distributed as project-owned artwork.
- The 13 named-pool banners below are unmodified 1650×300 images from the corresponding official Endfield announcements, retrieved 2026-09-24. These are Hypergryph publisher artwork. The URLs establish provenance, while redistribution for this release is based on the project owner's confirmation above. No repository source-code license applies to them.

| Local file under `Gacha/` | Pool | Official announcement | Original image |
| --- | --- | --- | --- |
| `molten.png` | 熔火灼痕 | [1188](https://endfield.hypergryph.com/news/1188) | [CDN](https://web.hycdn.cn/upload/image/20260119/767a655579335002f33c747a59c5e2b8.png) |
| `messenger.png` | 轻飘飘的信使 | [8561](https://endfield.hypergryph.com/news/8561) | [CDN](https://web.hycdn.cn/upload/image/20260205/784a936dcc82723ff36b2e43c2958418.png) |
| `vivid.png` | 热烈色彩 | [7226](https://endfield.hypergryph.com/news/7226) | [CDN](https://web.hycdn.cn/upload/image/20260212/05a55504cfcd59f0a7e3b305b7d9ab42.png) |
| `river.jpg` | 河流的女儿 | [5992](https://endfield.hypergryph.com/news/5992) | [CDN](https://web.hycdn.cn/upload/image/20260310/d58aa5adacc4ba33b8067c32628dee2f.jpg) |
| `wolf.jpg` | 狼珀 | [7224](https://endfield.hypergryph.com/news/7224) | [CDN](https://web.hycdn.cn/upload/image/20260325/df25ea5ecb561c995601a3cb0ffca8c0.jpg) |
| `spring-thunder.jpg` | 春雷动，万物生 | [5999](https://endfield.hypergryph.com/news/5999) | [CDN](https://web.hycdn.cn/upload/image/20260414/428db5f6061715095ca55fe0288d3731.jpg) |
| `unregretted.jpg` | 拳出无悔 | [2661](https://endfield.hypergryph.com/news/2661) | [CDN](https://web.hycdn.cn/upload/image/20260602/a89f8aadf0346a3659e09e444572e94f.jpg) |
| `sinner.jpg` | 逐罪者 | [3804](https://endfield.hypergryph.com/news/3804) | [CDN](https://web.hycdn.cn/upload/image/20260623/246a0421c5b1a71f13c98f1d4dfbaacc.jpg) |
| `north.jpg` | 临渊望北 | [8545](https://endfield.hypergryph.com/news/8545) | [CDN](https://web.hycdn.cn/upload/image/20260707/343291d361b775d7e607b79c49838fc4.jpg) |
| `morning-star.jpg` | 晨星于此闪耀 | [1165](https://endfield.hypergryph.com/news/1165) | [CDN](https://web.hycdn.cn/upload/image/20260730/11bb13df378201f8b0b5580ec3c02d37.jpg) |
| `winter.jpg` | 冬猎 | [6097](https://endfield.hypergryph.com/news/6097) | [CDN](https://web.hycdn.cn/upload/image/20260824/daa199c31f50c311e52da65204f9c373.jpg) |
| `refactor-vivid.png` | 绚丽异彩重构寻访 | [2651](https://endfield.hypergryph.com/news/2651) | [CDN](https://web.hycdn.cn/upload/image/20260921/394fc7fba6fed31434e0c65f69ecdecd.png) |
| `celebration.jpg` | 辉光庆典 | [9342](https://endfield.hypergryph.com/news/9342) | [CDN](https://web.hycdn.cn/upload/image/20260429/1f7ef0a09bd76ca66e1438efc7cde069.jpg) |

- Expanded six-star rows use the bundled official operator portraits when the recorded name matches the catalog above; unmatched names retain the original XAML glyph. The user-supplied Starward reference screenshot is not packaged.

## Star Rail history preview (2026-09-28)

- Five-star character rows use the bundled official portraits listed above; light cones now use the bundled catalog icons described below; unmatched names retain the project-authored XAML glyph. The user-supplied Starward screenshot is a density reference only and is not bundled. Cards use existing theme brushes. UIGF record data and official rule notices are factual references, not artwork sources.

## Endfield channel controls in the development tree

The official/Bilibili channel indicator is an original text badge drawn over the existing, previously attributed Endfield icon. The maintenance panel and launch-button glyphs use existing project theme resources and Windows system glyphs. No new bitmap, publisher image, third-party icon, or downloaded game resource is bundled by this work. Runtime game package files and signed CDN URLs are user-cache/install data, not AsterLauncher release assets.
## 0.1.3 channel markers

The Endfield server selector and icon markers are original AsterLauncher text overlays (“官” and “B”) on the already attributed packaged game icon. The overview, sidebar and library consume observable channel state. No separate publisher/bilibili logo or other launcher asset was added. Node.js and 7-Zip are executable dependencies, not media; their notices and corresponding source distribution are recorded in THIRD_PARTY_NOTICES.md.

## Bundled UIGF character and equipment icons (2026-09-30)

Original images are obtained from the public catalogs hosted by miHoYo/miHoYo community Wiki. The catalogs include editor uploads; hosting and a source link are provenance, not an open-source image license. Publisher artwork remains the respective publisher's property. These additional bundled images are included at the project owner's explicit request in this maintenance session. No other launcher's assets or repository image license is used.

| Game | Bundled catalog images used by the index | Catalog |
| --- | --- | --- |
| Genshin Impact | 129 character icons, 252 weapon icons | [public catalog](https://api-static.mihoyo.com/common/blackboard/ys_obc/v1/home/content/list?app_sn=ys_obc&channel_id=189) |
| Honkai: Star Rail | 9 additional character icons, 170 light-cone icons; existing official-site character portraits take precedence | [public catalog](https://api-static.mihoyo.com/common/blackboard/sr_wiki/v1/home/content/list?app_sn=sr_wiki&channel_id=17) |
| Zenless Zone Zero | 62 agent icons, 100 W-engine icons, 41 Bangboo icons | [public catalog](https://api-static.mihoyo.com/common/blackboard/zzz_wiki/v1/home/content/list?app_sn=zzz_wiki&channel_id=2) |

The original PNG/JPEG bytes are kept under `Assets/Games/Gacha/Portraits/` in game-specific folders. `portrait-index.json` records the original catalog title, exact source page, CDN URL, catalog endpoint, SHA-256 and size for every new mapping. Source-listed aliases and whitespace variants of Chinese names map to the same file; unmatched names use the project glyph. No image request is made by the record view, so these images remain available offline.

`eng/update-uigf-artwork.cjs` independently collects public catalog entries, restricts HTTPS hosts, checks image signatures, validates cached hashes and updates the index after successful collection. Duplicate unused catalog images are excluded. Existing first-party portrait collection preserves these catalog mappings.
## Independent game-data feed
`resources/catalog.json` publishes the existing, attributed Endfield pool schedule and one publisher portrait at `resources/images/endfield/`. The portrait is byte-identical to its bundled file and retains the original publisher URL in the catalog and `portrait-index.json`; no third-party launcher asset is used. Client updates validate the declared size, SHA-256 and raster format before activation. Publisher image rights remain with Hypergryph / GRYPHLINE; the source-code license does not license these images. Feed revisions may add public display metadata independently of application releases. Offline clients retain cached and bundled images.
## Endfield Danqing Du resource update

Official [version preview](https://www.taptap.cn/moment/856621354630254004) and [version guide](https://endfield.hypergryph.com/version_briefing/latest) provide the new pool names and weapon associations. Version boundaries use published calendar dates from the [development communication](https://endfield.hypergryph.com/news/3805), without assigning an unpublished maintenance time.

New resource-feed images retain their original publisher bytes: Si and Minghe thumbnail images and the Si recommendation JPEG. The Minghe portrait and pool banner reference the same original file. Exact CDN URLs, SHA-256 and byte lengths are recorded in resources/catalog.json. Images remain Hypergryph / GRYPHLINE property and are used only for game information; no third-party launcher artwork is used.
