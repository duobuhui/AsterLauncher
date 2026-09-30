# AsterLauncher

独立开发的 Windows 游戏启动器，使用 .NET 10、Windows App SDK 2.4 和 WinUI 3。当前版本：**0.1.3-beta**。

[下载](https://github.com/duobuhui/AsterLauncher/releases) · [反馈问题](https://github.com/duobuhui/AsterLauncher/issues)

## 安装与使用

下载 Windows x64 完整包，解压后运行顶层 `AsterLauncher.exe`。请完整保留 `App` 和 `MigrationTools` 文件夹，不要在压缩包内运行。

首次启动可查找已有游戏，也可手动选择游戏程序。设置中的游戏下载目录用于新安装；每个游戏的路径可在启动按钮右侧的展开面板中调整。

配置、日志、抽卡记录和缓存默认保存在程序旁的 `Data` 文件夹，可在设置中迁移。备份时请同时保留数据目录和 `asterlauncher.data-location.json`（如有）。

### 启动方案与伴随工具

在启动方案中添加工具、参数及执行顺序，可安排在游戏启动前、启动后或退出后运行。伴随工具需要自行安装，再选择对应程序路径。

### 抽卡记录

终末地、原神、星穹铁道、绝区零可在抽卡记录页面同步、导入和导出记录。同步需要游戏产生有效的本地授权缓存；保底和统计根据本地记录计算，历史记录缺失时可能不完整。

### 终末地（Beta）

点击启动按钮右侧箭头切换官服或 B服。两服使用独立安装目录，分别保存版本、任务和启动方案；下载任务不会因切服而转移。

进入游戏页面时检查更新。有更新时主按钮显示“更新游戏”，下载时显示进度并可暂停。文件检查、修复、预下载和共享管理位于展开面板；预下载仅在官方开放时可用。

资源共享要求同卷 NTFS，仅共享符合规则且内容一致的资源。使用官方启动器维护前，请关闭两服游戏并解除共享。HDiff/VFS 原生增量目前使用目标清单逐文件同步回退。

### 启动器更新

在设置中检查新版本。0.1.3 起使用多文件整包和文件差量更新。

从 0.1.2 升级需要迁移一次：

1. 退出旧版，下载完整包、同名 `.zip.sha256` 和迁移工具包。
2. 将迁移工具解压到旧安装目录之外的同一卷。
3. 运行 `AsterLauncher.Migrator.exe`，选择旧安装目录和完整 ZIP。
4. 完成后仍从原目录运行 `AsterLauncher.exe`。原数据保留，旧程序备份在 `MigrationBackup`。

## 完整支持一款游戏需要哪些功能

欢迎通过 Pull Request 补充游戏适配。下表是完整支持的目标，当前内置游戏不一定具备全部功能；提交时请写明支持的游戏、渠道和暂未实现的部分。

| 功能 | 需要实现的内容 |
| --- | --- |
| 游戏信息与素材 | 游戏名称、官网、图标及版本主视觉；保留离线回退，注明素材来源与使用权利。 |
| 安装识别 | 手动选择、可信路径或官方配置查找、本地版本识别；处理多份安装和目录移动，不能仅凭 EXE 文件名判断渠道。 |
| 渠道与服务器 | 独立保存各渠道的路径、版本、任务和启动方案；切换后立即更新图标与按钮状态，异步结果不能串服。 |
| 下载与更新 | 使用官方实时清单和地址，支持进度、暂停/续传、重启恢复、校验、空间检查和失败恢复。更新完成后才写入新版本。 |
| 预下载与修复 | 预下载不修改当前安装，正式更新核对后复用缓存；修复检查大小和内容哈希，仅补齐问题文件，不删除用户文件。 |
| 启动与伴随工具 | 正确的入口、参数、工作目录、进程与退出检测；接入现有启动编排，保留工具执行顺序和进程所有权。 |
| 抽卡记录（如适用） | 安全同步、导入、合并去重、导出和卡池分析；遵循适用的交换格式，不保存或记录授权 URL、Token。 |
| 资源共享（如适用） | 两服独立目录，仅共享确认不可变的相同资源；写入先生成独立文件再替换，支持解除共享和不支持硬链接时的回退。 |
| 界面与兼容性 | 沿用现有控件和布局，显示清楚的状态与错误；兼容旧配置和记录，不影响其他游戏，提供相应测试。 |

适配逻辑放在游戏适配器或独立服务中，界面通过状态模型调用。启动职责保持在 `GameLaunchOrchestrator`。结构和样式可参考 [ARCHITECTURE.md](ARCHITECTURE.md) 与 [UI_SPEC.md](UI_SPEC.md)。

### 源码构建

需要 Windows x64 和 .NET 10 SDK，SDK 放在 `.sdk/dotnet`。构建前取得官方 Node.js 24.19.0 Windows x64 `node.exe`，运行 `eng/prepare-maintenance-runtime.ps1 -NodeSource <路径>`。发布包已包含所需运行依赖。

```powershell
. .\eng\set-env.ps1
dotnet restore .\AsterLauncher.sln
& .\eng\build.ps1 -Configuration Debug -NoRestore
dotnet test .\tests\AsterLauncher.Core.Tests\AsterLauncher.Core.Tests.csproj --no-build --configuration Debug --logger 'console;verbosity=minimal'
```

打包使用 `eng/package-release.ps1`。工具、缓存和临时文件保存在项目目录，不修改全局环境变量。

## 鸣谢

感谢以下项目和社区：

- [Starward](https://github.com/Scighost/Starward)：游戏启动器的产品方向与功能参考。
- [UIGF](https://uigf.org/)：抽卡记录交换格式。
- [Hi3Helper.Plugin.Hypergryph](https://github.com/misaka10843/Hi3Helper.Plugin.Hypergryph)、[ake-tracker](https://github.com/mmgfrcs/ake-tracker)：终末地公开协议与记录格式参考。
- [MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights)、[maa-cli](https://github.com/MaaAssistantArknights/maa-cli)、[MaaEnd](https://github.com/MaaEnd/MaaEnd)、[BetterGI](https://github.com/babalae/better-genshin-impact)、[March7thAssistant](https://github.com/moesnow/March7thAssistant)：伴随工具预设所对应的项目，工具需自行安装。
- [.NET](https://github.com/dotnet/runtime)、[WinUI](https://github.com/microsoft/microsoft-ui-xaml)、[Windows App SDK](https://github.com/microsoft/WindowsAppSDK)：应用框架。
- [Node.js](https://nodejs.org/)、[7-Zip](https://www.7-zip.org/)：公开资源传输与压缩包处理的运行依赖。

也感谢提交问题、建议和 Pull Request 的贡献者。

## 来源与许可

游戏素材权利归发行商，来源见 [ASSET_SOURCES.md](ASSET_SOURCES.md)。依赖许可与产品参考见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目独立实现，源码许可尚未单独声明。
