using AsterLauncher.App.Services;
using AsterLauncher.App.ViewModels;
using AsterLauncher.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Views;

public sealed partial class GachaPage : Page
{
    private static readonly SolidColorBrush HighCountBrush =
        new(Microsoft.UI.ColorHelper.FromArgb(255, 143, 29, 43));

    private readonly LauncherViewModel _launcher;
    private readonly IUigfArchiveService _archive;
    private readonly IEndfieldGachaAccountService _endfieldArchive;
    private readonly GachaAccountSelection _accountSelection;
    private bool _busy;
    private int _refreshing;
    private sealed record AccountChoice(string Id, string Label);
    private readonly IFilePickerService _filePicker;
    private int _poolCardCount;
    private UigfGachaAnalysis _analysis = UigfGachaAnalysis.Empty;
    private bool _settingAccounts;
    private bool _showStatistics;
    private int _refreshRevision;
    private bool _cloudLinkDialogOpen;

    private readonly IResourceCatalogProvider _resources;
    private void Resource_OnUpdated(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(async () => { if (IsLoaded) await RefreshAsync(); });
    public GachaPage(LauncherViewModel launcher, IUigfArchiveService archive, IEndfieldGachaAccountService endfieldArchive, IFilePickerService filePicker, IResourceCatalogProvider resources, GachaAccountSelection accountSelection)
    {
        _launcher = launcher;
        _resources = resources;
        Loaded += (_, _) => _resources.Updated += Resource_OnUpdated;
        Unloaded += (_, _) => _resources.Updated -= Resource_OnUpdated;
        _archive = archive;
        _endfieldArchive = endfieldArchive;
        _accountSelection = accountSelection;
        _filePicker = filePicker;
        InitializeComponent();
        HistoryPoolItems.ItemTemplate = PoolItems.ItemTemplate;
        PoolLeftItems.ItemTemplate = PoolItems.ItemTemplate;
        PoolRightItems.ItemTemplate = PoolItems.ItemTemplate;
        UigfRoleSections.ItemTemplate = UigfSections.ItemTemplate;
        UigfOtherSections.ItemTemplate = UigfSections.ItemTemplate;
    }

    private async void Page_OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        GachaScrollViewer.ChangeView(null, 0, null, true);
    }

    private async Task RefreshAsync()
    {
        ++_refreshing;
        UpdateCommands();
        try { await RefreshCoreAsync(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(new GachaImportResult(false, 0, "抽卡档案暂时无法读取，原文件已保留。请检查数据目录后重试。"));
        }
        finally { --_refreshing; UpdateCommands(); }
    }

    private async Task RefreshCoreAsync()
    {
        var revision = ++_refreshRevision;
        var gameId = _launcher.CurrentGame?.Id ?? "";
        var gameName = _launcher.CurrentGame?.DisplayName ?? "未选择游戏";

        bool IsCurrent() => revision == _refreshRevision && _launcher.CurrentGame?.Id == gameId;
        var isEndfield = gameId == BuiltInGameIds.Endfield;
        CurrentGameText.Text = gameName;
        ClearAccountView();

        var supportsUigfCapture = GachaCapabilities.SupportsUigf(gameId);
        NewEndfieldAccountButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        GachaAccountSelector.Visibility = GachaCapabilities.SupportsRecords(gameId) ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        CaptureButton.Content = "增量同步";
        CloudSyncHint.Visibility = _launcher.CurrentGame?.IsOfficialCloud == true ? Visibility.Visible : Visibility.Collapsed;
        FullCaptureButton.Visibility = isEndfield || supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Content = isEndfield ? "导入终末地 JSON" : "导入 UIGF v4 JSON";
        ExportButton.Content = isEndfield ? "导出终末地 JSON" : "导出 UIGF v4.2 JSON";
        ExportCsvButton.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldAnalysisPanel.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPityStrip.Visibility = isEndfield ? Visibility.Visible : Visibility.Collapsed;
        UigfAnalysisPanel.Visibility = supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;
        UigfPityStrip.Visibility = supportsUigfCapture ? Visibility.Visible : Visibility.Collapsed;

        if (isEndfield)
        {
            var accounts = await _endfieldArchive.GetAccountsAsync();
            if (!IsCurrent()) return;
            _analysis = UigfGachaAnalysis.Empty;
            var selectedId = _endfieldArchive.SelectedAccountId;
            SetAccountChoices(accounts.Select(account => new AccountChoice(account.Id, account.Name)).ToArray(), selectedId);
            var name = accounts.FirstOrDefault(account => account.Id == selectedId)?.Name ?? "当前账号";
            AccountHintText.Text = $"终末地记录未提供可靠 UID。同步和导入写入“{name}”；请先核对游戏内账号，不同账号请新建档案。";
            var endfieldSummary = await _endfieldArchive.GetSummaryAsync();
            var analysis = await _endfieldArchive.GetAnalysisAsync();
            if (!IsCurrent()) return;
            StatisticsView.SetEndfield(analysis, endfieldSummary.WeaponCount);
            CurrentGameText.Text = gameName;
            ArchiveSummaryText.Text = $"{endfieldSummary.TotalCount} 条 · {analysis.CharacterDrawCount} 抽 · 六星 {analysis.SixStarCount}（免费 {analysis.FreeSixStarCount}）";
            ToolTipService.SetToolTip(ArchiveSummaryText, _endfieldArchive.ArchivePath);
            StandardPityText.Text = analysis.Pity.StandardText;
            LimitedPityLabel.Text = analysis.Pity.LimitedLabel;
            LimitedPityText.Text = analysis.Pity.LimitedText;
            LimitedPityBar.Value = analysis.Pity.LimitedPercent;
            LimitedPityBar.Foreground = analysis.Pity.LimitedOver65 ? HighCountBrush :
                (Brush)Application.Current.Resources["PageTitleBrush"];
            UpRemainingLabel.Text = analysis.Pity.UpLabel;
            UpRemainingText.Text = analysis.Pity.UpText;
            UpPityBar.Visibility = analysis.Pity.UpIsRefactor && !analysis.Pity.UpRecorded && analysis.Pity.UpOperatorKnown
                ? Visibility.Visible : Visibility.Collapsed;
            UpPityBar.Value = analysis.Pity.UpPercent;
            var historyPools = analysis.Pools.Where(pool => pool.PreviousPhaseCount > 0).ToArray();
            var otherPools = analysis.Pools.Where(pool => pool.PreviousPhaseCount == 0).ToArray();
            HistoryPoolItems.ItemsSource = historyPools;
            HistoryPoolItems.Visibility = historyPools.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _poolCardCount = otherPools.Length;
            PoolItems.ItemsSource = otherPools;
            PoolLeftItems.ItemsSource = otherPools.Where((_, index) => index % 2 == 0).ToArray();
            PoolRightItems.ItemsSource = otherPools.Where((_, index) => index % 2 == 1).ToArray();
            SixStarEmptyText.Visibility = analysis.Pools.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            UpdatePoolLayout(GachaScrollViewer.ActualWidth);
            ApplyStatisticsView();
            return;
        }

        _poolCardCount = 0;
        HistoryPoolItems.ItemsSource = null;
        HistoryPoolItems.Visibility = Visibility.Collapsed;
        PoolItems.ItemsSource = null;
        PoolLeftItems.ItemsSource = null;
        PoolRightItems.ItemsSource = null;
        if (supportsUigfCapture)
        {
            var analysis = await _archive.GetAnalysisAsync(gameId);
            if (!IsCurrent()) return;
            _analysis = analysis;
            var uid = _accountSelection.Resolve(analysis);
            await StatisticsView.SetUigfAsync(analysis, uid);
            if (!IsCurrent()) return;
            CurrentGameText.Text = gameName;
            UigfRulesText.Text = gameId switch
            {
                BuiltInGameIds.GenshinImpact => "角色活动祈愿 301 / 400 共用计数，武器、常驻和集录分别统计；不推断定轨或 UP。",
                BuiltInGameIds.ZenlessZoneZero => "各调频类别分别统计 S / A / B 级；特殊类别保底规则待核对，不推断 UP。",
                _ => "同类跃迁共用五星计数；特殊类别保留原始记录，不推断 UP。"
            };
            UigfEmptyText.Text = $"暂无{gameName}记录；同步或导入 UIGF 后显示。";
            var numbered = analysis.Accounts.OrderBy(account => account.Uid, StringComparer.Ordinal)
                .Select((account, index) => (account.Uid, Number: index + 1)).ToDictionary(item => item.Uid, item => item.Number, StringComparer.Ordinal);
            SetAccountChoices(analysis.Accounts.Select(account => new AccountChoice(account.Uid,
                $"账号 {numbered[account.Uid]} · {account.AccountLabel}")).ToArray(), uid);
            AccountHintText.Text = "记录和统计使用同一个账号；同步、导入按记录中的 UID 合并，当前选择保持不变。";
            ShowUigfAccount(uid);
            ApplyStatisticsView();
            return;
        }

        UigfSections.ItemsSource = null;
        UigfRoleSections.ItemsSource = null;
        UigfOtherSections.ItemsSource = null;
        var summary = await _archive.GetSummaryAsync();
        if (!IsCurrent()) return;
        CurrentGameText.Text = gameName;
        ArchiveSummaryText.Text = $"本地档案 · 原神 {summary.GenshinCount} · 星穹铁道 {summary.StarRailCount} · 绝区零 {summary.ZenlessCount} · 共 {summary.TotalCount} 条";
    }

    private void ClearAccountView()
    {
        _analysis = UigfGachaAnalysis.Empty;
        _poolCardCount = 0;
        HistoryPoolItems.ItemsSource = null;
        PoolItems.ItemsSource = null;
        PoolLeftItems.ItemsSource = null;
        PoolRightItems.ItemsSource = null;
        UigfSections.ItemsSource = null;
        UigfRoleSections.ItemsSource = null;
        UigfOtherSections.ItemsSource = null;
        UigfPityStrip.ItemsSource = null;
        StandardPityText.Text = LimitedPityText.Text = UpRemainingText.Text = "—";
        LimitedPityBar.Value = UpPityBar.Value = 0;
        ArchiveSummaryText.Text = "正在读取当前账号档案…";
        StatisticsView.Clear();
    }
    private void SetAccountChoices(IReadOnlyList<AccountChoice> choices, string? selectedId)
    {
        _settingAccounts = true;
        try
        {
            GachaAccountSelector.ItemsSource = choices;
            GachaAccountSelector.SelectedItem = choices.FirstOrDefault(choice => choice.Id == selectedId);
        }
        finally { _settingAccounts = false; }
    }

    private async void GachaAccountSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingAccounts || _busy || _refreshing > 0 || GachaAccountSelector.SelectedItem is not AccountChoice choice) return;
        if (_launcher.CurrentGame?.Id != BuiltInGameIds.Endfield)
        {
            if (!_accountSelection.Select(_analysis, choice.Id)) return;
            ShowUigfAccount(choice.Id);
            StatisticsView.SelectUigfAccount(choice.Id);
            return;
        }
        _busy = true;
        UpdateCommands();
        try
        {
            await _endfieldArchive.SelectAccountAsync(choice.Id);
            ResultInfo.IsOpen = false;
            await RefreshAsync();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowResult(new GachaImportResult(false, 0, "账号档案未切换，原有记录已保留。请检查数据目录后重试。"));
            await RefreshAsync();
        }
        finally { _busy = false; UpdateCommands(); }
    }

    private async void NewEndfieldAccount_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _refreshing > 0 || _launcher.CurrentGame?.Id != BuiltInGameIds.Endfield) return;
        _busy = true;
        UpdateCommands();
        var input = new TextBox { Header = "本地账号名称", PlaceholderText = "例如：我的主账号", MaxLength = 40 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "本地账号名称");
        var content = new StackPanel { Spacing = 12, MaxWidth = 440 };
        content.Children.Add(new TextBlock
        {
            Text = "创建一个独立的本地抽卡档案；不会登录或切换游戏账号。",
            FontSize = 13, TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = "终末地记录没有可靠 UID。同步或导入前，请核对游戏内账号；记录将合并到当前选中的档案。",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = AppearanceBrushes.Get("GlassMutedTextBrush")
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = "新建终末地账号档案",
            Content = content, PrimaryButtonText = "创建并切换", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { args.Cancel = true; input.Focus(FocusState.Programmatic); }
        };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var created = await _endfieldArchive.CreateAccountAsync(input.Text);
            await _endfieldArchive.SelectAccountAsync(created.Id);
            await RefreshAsync();
            ShowResult(new GachaImportResult(true, 0, $"已切换到“{created.Name}”，同步或导入前请核对游戏内账号。"));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            ShowResult(new GachaImportResult(false, 0, exception is ArgumentException or InvalidOperationException
                ? exception.Message : "账号档案未创建或切换，原有记录已保留。请检查数据目录后重试。"));
            await RefreshAsync();
        }
        finally { _busy = false; UpdateCommands(); }
    }

    private void RecordsTab_OnClick(object sender, RoutedEventArgs e) { _showStatistics = false; ApplyStatisticsView(); }
    private void StatisticsTab_OnClick(object sender, RoutedEventArgs e) { _showStatistics = true; ApplyStatisticsView(); }
    private void ApplyStatisticsView()
    {
        RecordsTab.IsChecked = !_showStatistics;
        StatisticsTab.IsChecked = _showStatistics;
        StatisticsView.Visibility = _showStatistics ? Visibility.Visible : Visibility.Collapsed;
        ExportPanel.Visibility = _showStatistics ? Visibility.Collapsed : Visibility.Visible;
        var endfield = _launcher.CurrentGame?.Id == BuiltInGameIds.Endfield;
        EndfieldAnalysisPanel.Visibility = !_showStatistics && endfield ? Visibility.Visible : Visibility.Collapsed;
        EndfieldPityStrip.Visibility = !_showStatistics && endfield ? Visibility.Visible : Visibility.Collapsed;
        var uigf = GachaCapabilities.SupportsUigf(_launcher.CurrentGame?.Id);
        UigfAnalysisPanel.Visibility = !_showStatistics && uigf ? Visibility.Visible : Visibility.Collapsed;
        UigfPityStrip.Visibility = !_showStatistics && uigf ? Visibility.Visible : Visibility.Collapsed;
        GachaScrollViewer.ChangeView(null, 0, null, true);
    }
    private void ShowUigfAccount(string? uid)
    {
        var account = _analysis.Accounts.FirstOrDefault(account => account.Uid == uid);
        UigfSections.ItemsSource = account?.Sections;
        UigfRoleSections.ItemsSource = account?.Sections.Where(section => section.IsFeaturedCharacter).ToArray();
        UigfOtherSections.ItemsSource = account?.Sections.Where(section => !section.IsFeaturedCharacter).ToArray();
        UpdateUigfLayout(GachaScrollViewer.ActualWidth, account);
        UigfEmptyText.Visibility = account?.RecordCount > 0 ? Visibility.Collapsed : Visibility.Visible;
        ArchiveSummaryText.Text = account is null ? "本地档案 · 0 条"
            : $"{account.AccountLabel} · {account.RecordCount} 条 · {_analysis.HighRarityLabel} {account.Sections.Sum(section => section.HighRarityCount)}";
        var displayed = _analysis.Categories.Where(category => category.PityMaximum is not null &&
            (!IsOptionalCategory(category.Type) || account?.Sections.Any(section => section.Type == category.Type) == true)).ToList();
        displayed.AddRange(account?.Sections.Where(section => displayed.All(c => c.Type != section.Type))
            .Select(section => new UigfPoolDefinition(section.Type, section.Title, section.PityMaximum)) ?? []);
        UigfPityStrip.ItemsSource = displayed.Select(category =>
        {
            var section = account?.Sections.FirstOrDefault(section => section.Type == category.Type);
            return new UigfPityIndicator(category.Title, section?.PityCount, category.PityMaximum, section?.HighRarityCount > 0);
        }).ToArray();
    }

    private bool IsOptionalCategory(string type) => _analysis.GameId switch
    {
        BuiltInGameIds.GenshinImpact => type == "500",
        BuiltInGameIds.ZenlessZoneZero => type is "102" or "103",
        _ => false
    };

    private void UigfAnalysisPanel_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var account = _analysis.Accounts.FirstOrDefault(account => account.Uid == (GachaAccountSelector.SelectedItem as AccountChoice)?.Id);
        UpdateUigfLayout(e.NewSize.Width, account);
    }

    private void GachaScrollViewer_OnSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdatePoolLayout(e.NewSize.Width);

    private void UpdatePoolLayout(double width)
    {
        var useTwoColumns = _poolCardCount > 1 && width - 48 >= 740;
        PoolColumns.Visibility = useTwoColumns ? Visibility.Visible : Visibility.Collapsed;
        PoolItems.Visibility = useTwoColumns ? Visibility.Collapsed : Visibility.Visible;
        var account = _analysis.Accounts.FirstOrDefault(account => account.Uid == (GachaAccountSelector.SelectedItem as AccountChoice)?.Id);
        UpdateUigfLayout(width, account);
    }

    private void UpdateUigfLayout(double width, UigfAccountAnalysis? account)
    {
        var hasRole = account?.Sections.Any(section => section.IsFeaturedCharacter) == true;
        var hasOther = account?.Sections.Any(section => !section.IsFeaturedCharacter) == true;
        var useColumns = width >= 650 && hasRole && hasOther;
        UigfColumns.Visibility = useColumns ? Visibility.Visible : Visibility.Collapsed;
        UigfSections.Visibility = useColumns ? Visibility.Collapsed : Visibility.Visible;
    }


    private async void Capture_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _refreshing > 0 || _cloudLinkDialogOpen) return;
        var game = _launcher.CurrentGame;
        if (game is null)
        {
            ShowResult(new GachaImportResult(false, 0, "请先选择游戏。"));
            return;
        }

        if (game.IsOfficialCloud)
        {
            await CaptureFromCloudAsync(game, false);
            return;
        }

        if (game.Id == BuiltInGameIds.Endfield)
        {
            await RunAsync(() => _endfieldArchive.CaptureFromGameAsync(
                new Progress<string>(message =>
                {
                    ResultInfo.Title = "正在同步";
                    ResultInfo.Message = message;
                    ResultInfo.Severity = InfoBarSeverity.Informational;
                    ResultInfo.IsOpen = true;
                })), verifyEndfield: true);
            return;
        }

        await RunAsync(() => _archive.CaptureFromGameAsync(
            game.Id,
            game.State.ExecutablePath ?? string.Empty,
            new Progress<string>(message =>
            {
                ResultInfo.Title = "正在同步";
                ResultInfo.Message = message;
                ResultInfo.Severity = InfoBarSeverity.Informational;
                ResultInfo.IsOpen = true;
            })));
    }

    private async void FullCapture_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _refreshing > 0 || _cloudLinkDialogOpen) return;
        var game = _launcher.CurrentGame;
        if (game is null) return;
        if (game.IsOfficialCloud)
        {
            await CaptureFromCloudAsync(game, true);
            return;
        }
        var progress = new Progress<string>(message =>
        {
            ResultInfo.Title = "正在全量同步";
            ResultInfo.Message = message;
            ResultInfo.Severity = InfoBarSeverity.Informational;
            ResultInfo.IsOpen = true;
        });
        await RunAsync(() => game.Id == BuiltInGameIds.Endfield
            ? _endfieldArchive.CaptureFromGameAsync(progress, fullRefresh: true)
            : _archive.CaptureFromGameAsync(
                game.Id, game.State.ExecutablePath ?? string.Empty, progress, fullRefresh: true), verifyEndfield: game.Id == BuiltInGameIds.Endfield);
    }
    private async Task CaptureFromCloudAsync(GameCardViewModel game, bool fullRefresh)
    {
        if (_cloudLinkDialogOpen) return;
        _cloudLinkDialogOpen = true;
        UpdateCommands();
        var input = new TextBox
        {
            Header = "官方记录链接",
            PlaceholderText = "粘贴当前游戏的完整 HTTPS 记录链接",
            MaxLength = 32768,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(input, "官方记录链接");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input, "CloudGachaHistoryUrl");
        var selectedEndfieldId = _endfieldArchive.SelectedAccountId;
        var content = new StackPanel { Spacing = 12, MaxWidth = 460 };
        if (game.Id == BuiltInGameIds.Endfield)
        {
            var name = (GachaAccountSelector.SelectedItem as AccountChoice)?.Label ?? "当前账号";
            content.Children.Add(new TextBlock
            {
                Text = $"记录将合并到“{name}”。终末地记录没有可靠 UID，请确认链接属于此账号。",
                FontSize = 13, TextWrapping = TextWrapping.Wrap
            });
        }
        content.Children.Add(new TextBlock
        {
            Text = game.Id == BuiltInGameIds.Endfield
                ? "终末地目前支持官方记录接口链接（/api/record/char 或 /api/record/weapon）。若云游戏平台无法提供该链接，可在本地客户端同步，或导入已有记录文件。"
                : "如果云游戏平台能提供链接，请从游戏内的抽卡记录页复制完整官方链接。无法复制时，可在本地客户端同步，或导入已有记录文件。",
            TextWrapping = TextWrapping.Wrap, FontSize = 13
        });
        content.Children.Add(input);
        content.Children.Add(new TextBlock
        {
            Text = game.Id == BuiltInGameIds.Endfield
                ? "终末地每次同步链接对应的记录分组；其他角色或武器分组需分别粘贴链接。链接仅用于本次同步，关闭即清空，不写入日志或配置。"
                : "链接仅用于本次同步，关闭即清空，不写入日志或配置。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12,
            Foreground = AppearanceBrushes.Get("GlassMutedTextBrush")
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme,
            Title = fullRefresh ? "从云游戏链接全量同步" : "从云游戏链接增量同步",
            Content = content, PrimaryButtonText = "同步", CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        var cancelForeground = AppearanceBrushes.Get("GlassTextBrush");
        var cancelStyle = new Style { TargetType = typeof(Button), BasedOn = (Style)Application.Current.Resources["ThemeSecondaryButtonStyle"] };
        cancelStyle.Setters.Add(new Setter(Control.ForegroundProperty, cancelForeground));
        dialog.CloseButtonStyle = cancelStyle;
        foreach (var key in new[] { "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed" })
            dialog.Resources[key] = cancelForeground;
        string? historyUrl = null;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                args.Cancel = true;
                input.Focus(FocusState.Programmatic);
                return;
            }
            historyUrl = input.Text.Trim();
        };
        void ClearInput()
        {
            input.Text = string.Empty;
            input.ClearUndoRedoHistory();
        }
        dialog.Closed += (_, _) => ClearInput();
        try
        {
            var result = await dialog.ShowAsync();
            ClearInput();
            dialog.Content = null;
            if (result != ContentDialogResult.Primary || historyUrl is null
                || !ReferenceEquals(game, _launcher.CurrentGame) || !game.IsOfficialCloud
                || game.Id == BuiltInGameIds.Endfield && _endfieldArchive.SelectedAccountId != selectedEndfieldId) return;
            var progress = new Progress<string>(message =>
            {
                ResultInfo.Title = fullRefresh ? "正在全量同步" : "正在同步";
                ResultInfo.Message = message;
                ResultInfo.Severity = InfoBarSeverity.Informational;
                ResultInfo.IsOpen = true;
            });
            await RunAsync(() => game.Id == BuiltInGameIds.Endfield
                ? _endfieldArchive.CaptureFromUrlAsync(historyUrl, progress, fullRefresh: fullRefresh)
                : _archive.CaptureFromUrlAsync(game.Id, historyUrl, progress, fullRefresh: fullRefresh));
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            ShowResult(new GachaImportResult(false, 0, "暂时无法打开或完成同步，请关闭其他弹窗后重试。"));
        }
        finally
        {
            ClearInput();
            historyUrl = null;
            dialog.Content = null;
            _cloudLinkDialogOpen = false;
            UpdateCommands();
        }
    }

    private async void Import_OnClick(object sender, RoutedEventArgs e)
    {
        var gameId = _launcher.CurrentGame?.Id;
        await RunFileAsync(() => _filePicker.PickJsonAsync(App.MainWindow),
            path => gameId == BuiltInGameIds.Endfield ? _endfieldArchive.ImportAsync(path) : _archive.ImportAsync(path),
            verifyEndfield: gameId == BuiltInGameIds.Endfield);
    }

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var gameId = _launcher.CurrentGame?.Id;
        var name = gameId == BuiltInGameIds.Endfield ? "AsterLauncher-Endfield" : "AsterLauncher-UIGF";
        await RunFileAsync(() => _filePicker.PickJsonSaveAsync(App.MainWindow, $"{name}-{DateTime.Now:yyyyMMdd}"),
            path => gameId == BuiltInGameIds.Endfield ? _endfieldArchive.ExportJsonAsync(path) : _archive.ExportAsync(path));
    }

    private async void ExportCsv_OnClick(object sender, RoutedEventArgs e)
    {
        await RunFileAsync(() => _filePicker.PickCsvSaveAsync(App.MainWindow, $"AsterLauncher-Endfield-{DateTime.Now:yyyyMMdd}"),
            path => _endfieldArchive.ExportCsvAsync(path));
    }

    private async Task RunFileAsync(Func<Task<string?>> pick, Func<string, Task<GachaImportResult>> operation, bool verifyEndfield = false)
    {
        await RunOperationAsync(async () =>
        {
            var path = await pick();
            if (path is null || verifyEndfield && !await ConfirmEndfieldTargetAsync("导入")) return null;
            return await operation(path);
        });
    }

    private Task RunAsync(Func<Task<GachaImportResult>> operation, bool verifyEndfield = false) =>
        RunOperationAsync(async () =>
        {
            if (verifyEndfield && !await ConfirmEndfieldTargetAsync("同步")) return null;
            return await operation();
        });

    private async Task<bool> ConfirmEndfieldTargetAsync(string action)
    {
        var accounts = await _endfieldArchive.GetAccountsAsync();
        var id = _endfieldArchive.SelectedAccountId;
        var name = accounts.FirstOrDefault(account => account.Id == id)?.Name ?? "当前账号";
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, RequestedTheme = ActualTheme, Title = $"将记录合并到“{name}”",
            Content = $"终末地记录没有可靠 UID。请确认{(action == "导入" ? "所选文件" : "当前游戏")}中的记录属于此账号；属于其他账号时，请先取消并切换档案。",
            PrimaryButtonText = $"确认并{action}", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary && _endfieldArchive.SelectedAccountId == id;
    }

    private async Task RunOperationAsync(Func<Task<GachaImportResult?>> operation)
    {
        if (_busy || _refreshing > 0) return;
        _busy = true;
        UpdateCommands();
        try
        {
            var result = await operation();
            if (result is not null)
            {
                ShowResult(result);
                await RefreshAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException
            or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            ShowResult(new GachaImportResult(false, 0, "抽卡档案操作未完成，原有记录已保留。请检查文件和数据目录后重试。"));
        }
        finally { _busy = false; UpdateCommands(); }
    }

    private void UpdateCommands()
    {
        var enabled = !_busy && _refreshing == 0 && !_cloudLinkDialogOpen
            && GachaCapabilities.SupportsRecords(_launcher.CurrentGame?.Id);
        BusyRing.IsActive = _busy || _refreshing > 0;
        GachaAccountSelector.IsEnabled = enabled;
        NewEndfieldAccountButton.IsEnabled = enabled;
        CaptureButton.IsEnabled = enabled;
        FullCaptureButton.IsEnabled = enabled;
        ImportButton.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled;
        ExportCsvButton.IsEnabled = enabled;
    }
    private void ShowResult(GachaImportResult result)
    {
        ResultInfo.Title = result.Success ? "操作完成" : "操作未完成";
        ResultInfo.Message = result.Message;
        ResultInfo.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        ResultInfo.IsOpen = true;
    }
}
