# AsterLauncher

独立开发的 Windows 游戏启动器，当前版本 **0.1.3-beta**。使用 .NET 10、Windows App SDK 2.4 和 WinUI 3。

[下载](https://github.com/duobuhui/AsterLauncher/releases) · [反馈问题](https://github.com/duobuhui/AsterLauncher/issues)

## 功能

- 游戏库、路径查找、排序与隐藏、运行时间记录。
- 启动方案和伴随工具编排。
- 官方壁纸缓存、自选背景、主题与窗口设置。
- 终末地、原神、星穹铁道、绝区零抽卡记录导入与导出；终末地和星穹铁道记录分析。
- 终末地官服/B服下载、更新、修复、预下载和断点续传。
- 两服独立目录、服务器切换、NTFS 硬链接共享与解除共享。
- 启动器整包更新和文件差量更新。

内置游戏：终末地、原神、崩坏3、星穹铁道、绝区零、星布谷地、明日方舟。游戏下载与更新目前仅接入终末地。

## 下载与运行

下载 Windows x64 完整包，解压后运行顶层 `AsterLauncher.exe`，不要在压缩包内运行。

程序文件放在 `App` 和 `MigrationTools` 文件夹中。配置、日志、抽卡记录及缓存默认保存在 `Data`，可在设置中迁移。

### 从 0.1.2 升级

旧版单 EXE 更新器不能直接安装新布局，需要迁移一次：

1. 退出旧版，下载完整包、同名 `.zip.sha256` 和迁移工具包。
2. 将迁移工具解压到旧安装目录之外的同一卷。
3. 运行 `AsterLauncher.Migrator.exe`，选择旧安装目录和完整 ZIP。
4. 完成后继续运行原目录的 `AsterLauncher.exe`。原数据保留，旧程序备份在 `MigrationBackup`。

## 终末地

点击启动按钮右侧箭头，选择官服或 B服。每次进入游戏页面检查更新；有更新时主按钮显示“更新游戏”，下载时显示进度并可暂停。

- 安装与修复使用官方实时清单。两服分别保存目录、版本、任务和启动方案。
- 硬链接限同卷 NTFS 的 VFS `.chk` 候选资源；配置和用户数据不共享。交给官方启动器维护前，先关闭游戏并解除共享。
- 预下载只在官方开放时可用，不提前修改游戏文件。HDiff/VFS 增量目前回退为目标清单逐文件同步。

**尚待实机验证：**两服完整安装后的游戏启动、真实预下载窗口、正式增量样本，以及游戏运行时是否修改共享候选文件。

[验证记录](docs/VALIDATION_0.1.3.md) · [协议研究](ENDFIELD_DOWNLOAD_RESEARCH.md)

## 构建与测试

需要 Windows x64 和 .NET 10 SDK。项目使用 `.sdk/dotnet` 中的 SDK，构建缓存和临时文件保存在项目目录，不修改全局环境变量。

从源码构建前，按 [运行依赖说明](eng/prepare-maintenance-runtime.ps1) 准备官方 Node.js 24.19.0：下载 Windows x64 的 `node.exe`，执行 `eng/prepare-maintenance-runtime.ps1 -NodeSource <路径>`。发布包已包含 Node.js 和 7-Zip，无需另行安装。

```powershell
. .\eng\set-env.ps1
dotnet restore .\AsterLauncher.sln
& .\eng\build.ps1 -Configuration Debug -NoRestore
dotnet test .\tests\AsterLauncher.Core.Tests\AsterLauncher.Core.Tests.csproj --no-build --configuration Debug --logger 'console;verbosity=minimal'
```

打包使用 `eng/package-release.ps1`。

## 文档与来源

[架构](ARCHITECTURE.md) · [界面规范](UI_SPEC.md) · [里程碑](MILESTONES.md) · [素材来源](ASSET_SOURCES.md) · [第三方声明](THIRD_PARTY_NOTICES.md)

源码许可尚未单独声明。游戏素材权利归发行商，依赖许可见第三方声明。

- [Starward](https://github.com/Scighost/Starward)：产品方向参考，本项目独立实现。
- [UIGF](https://uigf.org/)：抽卡记录交换格式。
- 伴随工具预设： [MaaEnd](https://github.com/MaaEnd/MaaEnd)、[BetterGI](https://github.com/babalae/better-genshin-impact)、[March7thAssistant](https://github.com/moesnow/March7thAssistant)、[MAA](https://github.com/MaaAssistantArknights/MaaAssistantArknights) 和 [maa-cli](https://github.com/MaaAssistantArknights/maa-cli)，不随包捆绑。
