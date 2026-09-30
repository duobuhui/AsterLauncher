# 终末地游戏下载、预下载与修复研究

研究日期：2026-09-29。本文是接入方案，不代表 AsterLauncher 已实现相关功能。

## 结论与验证范围

官服和哔哩哔哩服可通过鹰角同一个公开元数据接口区分获取资源。已实时验证两服元数据、加密文件清单的下载与解码、清单哈希，以及各一个小文件的下载与哈希；另验证官服首个安装分卷支持 Range。没有下载完整游戏、安装、修改已有游戏或执行修复；预下载当前为空，未做真实预下载到正式更新的验证。

## 已验证协议

POST `https://launcher.hypergryph.com/api/proxy/batch_proxy`

Content-Type: `application/json`。本次请求不需要账号 Cookie 或登录 Token。

```json
{
  "proxy_reqs": [
    {
      "kind": "get_latest_game",
      "get_latest_game_req": {
        "appcode": "6LL0KJuqHBVz33WK",
        "channel": "1",
        "sub_channel": "1",
        "version": "",
        "launcher_appcode": ""
      }
    }
  ]
}
```

官服用 `channel=1, sub_channel=1`；B服两者均为 `2`。响应入口为 `proxy_rsps[].get_latest_game_rsp`，按 kind 匹配，不依赖数组位置。

| 本轮响应 | 官服 | B服 |
| --- | --- | --- |
| version / client_version | 1.5.3 | 1.5.3 |
| pkg.packs 数量 | 54 | 54 |
| 分卷 package_size 合计 | 56,915,023,037 字节，约 53.01 GiB | 56,993,830,364 字节，约 53.08 GiB |
| pkg.total_size | 119,390,457,746 字节 | 119,666,537,293 字节 |
| 解码清单条数 | 1,600 | 1,668 |
| 清单文件 size 合计 | 62,233,424,718 字节，约 57.96 GiB | 62,426,541,666 字节，约 58.14 GiB |
| game_files MD5 | 8decb54655339e531806ea8dcd2d90da | 928d94f2dc0d194eaecb7daad274a9ca |
| pre_patch | null | null |

这里是时间点快照，不应硬编码版本、大小和 CDN 版本目录。`total_size` 不等于下载流量；下载进度应按分卷大小合计，空间预算另计缓存、解压、备份和提交阶段的峰值。

请求 `version=1.5.3` 时，两服返回 `action=0`，没有下载分卷；空版本返回完整包，`action=1`。探测 `version=1.5.2` 时两服返回完整包、`patch=null`；该探测不证明本机安装了此版本，也不代表其他源版本没有增量包。不可把 action 数值推断为完整枚举。

本次资源基址：

```text
https://beyond.hycdn.cn/6LL0KJuqHBVz33WK/1.5/update/1/1/Windows/1.5.3_1mexHxqVLooNlufz/files
https://beyond.hycdn.cn/6LL0KJuqHBVz33WK/1.5/update/2/2/Windows/1.5.3_1mexHxqVLooNlufz/files
```

实际运行必须读取 `pkg.file_path`。分卷地址包含 `auth_key` 查询参数，应从最新响应取得，过期后刷新并按内容哈希匹配缓存；不得把第三方仓库存档当永久下载源或把签名 URL 写入普通日志。

## 下载和渠道隔离

建议保留一个终末地游戏页面，提供“官服 / 哔哩哔哩”选择，每个渠道有独立安装记录、路径、版本、任务与预下载缓存。建议身份结构为 `GameId + ChannelId + InstallationId`，不要用 `Endfield.exe` 文件名判断渠道。

本轮比较发现：1,543 个同名文件哈希一致，54 个同名文件哈希不同，官服独有 3 个，B服独有 71 个。差异包括 config.ini、u8_channel.dll、Unity 和插件文件。两渠道仍必须有独立物理安装目录，也不能靠替换几个 DLL 转服。原先第一版不做硬链接共享的建议已被 2026-09-29 的明确需求覆盖：仅对两渠道清单一致、实际内容经校验、并有明确不可变依据的资源建立 NTFS 硬链接；其他文件保持独立。

完整安装流程：获取渠道元数据 → 检查空间 → 下载分卷并逐卷校验 → 暂存解压 → 按目标清单校验 → 提交安装 → 保存版本与渠道。多卷 ZIP 需要经样本验证的解压实现；不能把每个 .zip.001/.002 当独立 ZIP 解压。

官服第一个分卷请求 `Range: bytes=0-1023` 已返回 `206`，Content-Range 为 `bytes 0-1023/1073741824`，读取 1024 字节。断点实现仍需处理服务器返回 200、416、内容变化、取消和签名过期；没有实测完整中断恢复。

## 预下载

公开实现将 `pre_patch` 建模为包含 `version`、`patches`、`package_size`、`total_size`、`cd_key`、`v2_patch_info_url` 等字段的补丁描述。当前终末地两服 `pre_patch=null`；历史 issue 的非空样本来自明日方舟，不能冒充终末地样本。

建议行为：

1. 上报真实已安装版本检查预下载；只有响应带有效预下载内容才显示可下载状态。网络失败和未开放预下载是不同状态。
2. 缓存按渠道、源版本、目标版本和资源哈希隔离。下载并校验完成后标记“预下载完成”，不改当前游戏文件或已安装版本。
3. 正式发布后重新取得 `patch`，核对缓存内容；不能仅以文件名相同就复用。正式更新需要的解压密码以正式元数据为准。
4. 应用增量前校验源文件，在暂存区生成结果并校验后提交。源文件损坏或补丁不适用时，可回退为目标清单的逐文件同步。
5. 处理预下载被撤回、被替换，以及两服发布时间不同；缓存不等于当前可安装版本。

公开实现涉及 `patch.json`、VFS 资源映射、`delete_files.txt`、HDiffPatch/hpatchz 和可能的加密压缩包；预下载完成只是下载阶段，不能据此宣称完整预更新支持。

## 修复

已验证 `pkg.file_path + /game_files` 可直接取得。先验证密文 MD5 等于 `pkg.game_files_md5`，再按公开实现的 AES-256-CBC / PKCS7 格式解码。解码内容为逐行 JSON，各条有 `path`、`md5`、`size`。

两服均从清单选择 `marquee_config.bin` 实测下载：HTTP 200、528 字节、MD5 与清单一致，证明逐文件补齐路径可用。仅验证此小文件，不代表全部大文件下载已验证。

建议修复流程：选择目标版本与渠道 → 获取匹配的完整清单 → 检查文件大小和 MD5 → 汇总缺失/损坏项及下载量 → 下载至临时文件 → 再校验 → 原子替换 → 复验。

重要边界：

- 检查更新请求可能返回空 pkg，修复应另取完整包元数据（本次空 version 可取得）。不可将空清单视为修复成功。
- 缓存清单必须匹配渠道、版本和官方清单哈希，不能直接相信已有本地 game_files。
- 当前接口提供最新版本；用最新清单修复旧版本属于更新。旧版修复需要已保留且仍可访问的对应版本资源，否则明确转入更新流程。
- 校验所有清单路径、压缩包路径和最终物理路径，拒绝目录越界、重解析点逃逸与路径冲突。
- 不删除清单外的用户文件；config.ini 是版本/渠道状态文件，应单独处理并在最终成功后提交，不能机械套用普通资源修复逻辑。
- 游戏运行时不提交覆盖；安装、更新和修复按物理目录互斥，防止两个任务同时写入。
- 参考修复实现部分失败分支只记录后 return；AsterLauncher 必须返回明确失败，不能以任务正常返回判定修复完成。

## AsterLauncher 接入位置

当前 `GameAdapters.cs` 的 EndfieldGameAdapter 只有官网入口；Core 的 IGameAdapter 没有安装/更新接口，GameFeature 没有 Download/PreDownload/Repair；配置只有全局 GameDownloadDirectory。README 也明确标记目前未区分终末地渠道。

建议独立于 GameLaunchOrchestrator 新增：

- Core：渠道/安装身份、版本清单、DownloadPlan、RepairPlan、PreDownloadState，以及 `IGameDistributionProvider` 和 `IGameMaintenanceService`。
- Infrastructure：`EndfieldDistributionProvider` 处理渠道 API、版本/config 和清单；通用下载器处理限速、并发、恢复、哈希；独立 installer/repairer 处理文件事务。
- App：终末地渠道选择，分别保存目录；下载/更新主操作，预下载次操作，修复入口，展示校验、下载、解压、应用和复验阶段。
- 持久化：已安装版本、预下载目标版本、下载任务必须分开；旧配置渠道先标记 Unknown，经可信元数据或用户明确选择迁移，不默认归为官服。抽卡档案和启动配置继续保持兼容。

无需为了这项功能引入动态插件加载。可参考公开协议实现独立服务；若复用第三方库，固定提交并记录许可证及原生依赖，不直接绑定其他启动器的 UI 或本机绝对路径。

## 实施顺序与验收

1. 两服身份、元数据解析、清单解码和已有安装识别；先输出只读安装/修复计划。
2. 完整安装、Range 恢复、渠道目录隔离、完整校验与故障恢复。
3. 逐文件修复，覆盖缺失、大小错误、同大小内容损坏、清单损坏、网络失败和版本不匹配。
4. 预下载缓存与正式更新复用；预下载未开放时用固定脱敏样本验证状态机，在下一次真实窗口完成两服验收。
5. 增量优化，覆盖源文件不匹配、加密包、VFS 映射、补丁失败和提交中断；失败不能写入成功版本。

交付标准应分别记录单元/集成检查和真实游戏验证：两服各完整安装并启动一次；中断重启可续传；故意损坏隔离测试目录内文件后仅补齐对应资源；预下载不影响旧版启动；正式发布后复用缓存并成功应用。不要直接破坏用户现有安装做测试。

## 来源

- 官方实时 API：[batch_proxy](https://launcher.hypergryph.com/api/proxy/batch_proxy)，本轮通过 POST 验证。
- [HG-Link 官服请求脚本](https://github.com/ERSTT/HG-Link/blob/main/.github/workflows/update_links.yml) 与 [B服请求脚本](https://github.com/ERSTT/HG-Link/blob/main/.github/workflows/update_links_bilibili.yml)：用于发现协议，实际数据从官方请求。
- [Hypergryph 核心库说明](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/README.EN.md)。
- [清单加解密](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/Utils/HgCrypto.cs)、[API 数据结构](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/Management/Api/HgApiStructs.cs)、[修复实现](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/Management/HgGameRepairer.cs)。
- [预下载实现](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/Management/HgGameInstaller.cs)、[增量实现](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/Hi3Helper.Hypergryph.Core/Management/HgGameInstaller.Install.cs)。
- [预下载 issue](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/issues/14) 和 [发布记录](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/releases)：历史实现曾修复增量问题，预下载发布说明也保留待测试措辞，不能把存在实现当成可靠性证明。
- [核心库 LICENSE](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph/blob/main/LICENSE)：本轮读取为 MIT；子模块与原生依赖仍需分别核对并维护 THIRD_PARTY_NOTICES.md。
- [Xel 的接入服务](https://github.com/lTinchl/Xel-Launcher/blob/master/Helpers/EndfieldService.cs) 及 [项目依赖](https://github.com/lTinchl/Xel-Launcher/blob/master/XelLauncher.csproj)：可参考调用边界，当前 csproj 使用开发者本机绝对路径，不能直接当即插即用依赖。

## 2026-09-29 实施核对

当前开发工作树已新增两渠道配置、官方元数据与加密 game_files 解析、内容寻址缓存、Range 续传、逐文件修复计划、安全目录项提交、硬链接扫描/优化/解除共享，以及启动按钮展开区内的渠道维护状态。实际本机 B 服安装只做只读目录结构观察：Endfield_Data/StreamingAssets/VFS 下有大量十六进制名称的 .chk 文件，另有 .blc 和索引 JSON；此前设想的 .bundle/.resS 白名单与实物不符，已废弃。现仅将两级十六进制路径中的 .chk 作为可共享候选，仍要求两份官方清单和本地实际哈希匹配。对本机样本的三份小文件做 MD5 核对后，文件名不等于内容 MD5；因此十六进制名称不能当作内容寻址证据。此观察不能证明游戏或官方启动器永不原地写入 .chk，因此对外部维护仍需先解除共享。

自动化测试在 E 盘隔离目录验证了硬链接文件 ID、单侧安全替换、删除单侧目录项、解除共享、不同/可变文件独立、跨卷回退、同大小损坏、路径越界、续传与签名刷新、旧配置未知渠道及预下载不改本地版本。模拟数据和少量文件验证不等于真实完整安装。2026-09-29 本机访问官方接口时遇到 Windows Schannel 凭据错误，未取得新的实时响应；以上旧版号与资源量仍只是研究时间点快照，不应写进运行逻辑。正式预下载窗口依旧没有终末地样本。多卷 ZIP、正式补丁、预下载复用、外部官方启动器原地写入安全性和完整游戏启动都保留为验收项。

## 2026-09-29 当日元数据复核

通过本机已有 Python 3.12 的标准库 HTTPS 实现，仅在内存中请求官方 batch_proxy：官服 1/1 与 B 服 2/2 均返回 HTTP 200、version=1.5.3、pkg.packs=54、patch=null、pre_patch=null。没有将响应体或签名资源 URL 写入文件。随后通过同一独立 TLS 探针取得两服当日 game_files，使用本机已有 Crypto AES 模块分别校验密文 MD5 并解码出官服 1,600 条、B 服 1,668 条；两服各下载 marquee_config.bin 一份，均为 528 字节且 MD5 与当日清单一致。这证明两服当日小文件下载与清单校验可用，不证明完整客户端下载成功。相同接口由 Windows/.NET HttpClient 请求时，在 Schannel 认证阶段报“安全包中没有可用的凭证”；禁用代理和强制 TLS 1.2 均未解决。启动器目前仍需修复这个实际使用的传输路径，不能因为独立探针成功就宣称应用内官方下载已验证。
## 2026-09-30 应用传输与交付复核

之前记录的 Schannel 错误已在启动器实际服务路径修复：使用官方 Node.js v24.19.0 的独立 OpenSSL HTTPS 进程，限定发行商分发主机并把地址经 stdin 传递。C# 官方提供器与下载器实测取得官服 1,600 条、B服 1,668 条清单，当前版本均 1.5.3；两服各完成 marquee_config.bin（528 字节）MD5 校验，另取得并校验 config.ini。官方逐文件源的 config.ini 是与 game_files 相同 AES 格式的加密内容，解密包含 [Game]、version、entry、entry_md5、appcode、region、channel 与 sub_channel，没有账号授权数据。本次不保存官方签名 URL。

0.1.3 的完整安装走逐文件目标清单，不自动下载两套 54 卷完整 ZIP；安装第二服在下载前先核对可复用资源。补丁完整文件支持独立暂存、分卷 ZIP 与 cd_key 解压、官方目标 MD5 复验，正式更新按内容身份复用预下载。固定隔离样本已验证分卷只作为一个压缩包读取、加密 ZIP、预下载不改现有版本、正式更新缓存复用、单服修复不改另一服的硬链接内容及未完成事务恢复。HDiff/VFS 原生增量目前明确回退为目标完整清单同步。

真实两服完整下载后启动、预下载开放到正式发布的完整窗口、实际正式增量样本及 .chk 在游戏自身/外部启动器维护时的不可变性尚未验收。不能把这次小文件、固定样本或 UI 验证写成这些项目已通过。历史快照与“尚未完成”记录对应当时状态，以本节和发布验收记录为最新说明。
