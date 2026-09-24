# Asset sources

Beta 0.1.0 source control and the single-file Release include 26 publisher-media files plus the original `endfield-pool-card.svg` listed below. On 2026-09-24 the project owner confirmed redistribution authorization for this release. The publisher images' copyright remains with their respective owners; neither the project's source-code license nor another repository's MIT license grants rights to reuse them. This file records each source rather than treating a URL as a license.

AsterLauncher contains no Starward artwork, icons, or branding. Packaged game media has an explicit source and may be replaced by users through Game Settings.

The v0.7 theme presets are original color definitions in XAML/C#. Their names do not add any game artwork, logos, or other third-party media. The wallpaper reuse change only caches the already documented local assets in memory for the running process.

The 2026-09-24 Endfield record-response investigation used no media or artwork and introduced no new packaged assets. Publisher media rights remain as described below.

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

Petit Planet retains the original AsterLauncher gradient placeholder because no stable production launcher asset and executable contract were verified. Custom user artwork always takes priority over packaged media.

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

- Each expanded six-star row currently uses an original XAML portrait placeholder. The user-supplied Starward reference screenshot is not packaged. If portraits are added later, record the exact source and verify redistribution rights before packaging them; publisher character artwork is not licensed by another repository's source-code license.
