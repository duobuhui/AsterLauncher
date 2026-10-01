using System.Text.Json;
using AsterLauncher.Core;
using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Views;

public sealed class GachaStatisticsControl : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 12 };
    private readonly ComboBox _account = new() { MinWidth = 150 };
    private readonly ComboBox _pool = new() { MinWidth = 160 };
    private readonly TextBlock _summary = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _rules = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly Grid _plots = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly GachaScatterControl _highPlot = new();
    private readonly GachaScatterControl _upPlot = new();
    private readonly Expander _editor = new() { Header = "校对 UP 判定", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _record = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _status = new() { MinWidth = 130 };
    private readonly TextBlock _correctionInfo = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly GachaClassificationStore _store = new(LauncherDataPaths.ResolveDataDirectory());
    private UigfGachaAnalysis _analysis = UigfGachaAnalysis.Empty;
    private EndfieldGachaAnalysis? _endfield;
    private IReadOnlyDictionary<string, GachaUpStatus> _corrections = new Dictionary<string,GachaUpStatus>();
    private IReadOnlyList<GachaPoolStatistics> _stats = [];
    private IReadOnlyList<GachaStatisticPoint> _editPoints = [];
    private HashSet<string> _knownItems = [];
    private bool _setting;
    private int _revision;

    public GachaStatisticsControl()
    {
        var selectors = new Grid { ColumnSpacing = 10 };
        selectors.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        selectors.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        selectors.Children.Add(_pool); Grid.SetColumn(_account,1); selectors.Children.Add(_account);
        AutomationProperties.SetName(_pool,"统计卡池"); AutomationProperties.SetName(_account,"统计账号");
        _body.Children.Add(selectors);
        _body.Children.Add(new Border { Child=_summary, Padding=new(14), CornerRadius=new(12),
            Background=Brush("HeroGlassBrush"),BorderBrush=Brush("GlassEdgeBrush"),BorderThickness=new(1) });
        _plots.ColumnDefinitions.Add(new() { Width=new GridLength(1,GridUnitType.Star) });
        _plots.ColumnDefinitions.Add(new() { Width=new GridLength(1,GridUnitType.Star) });
        _plots.RowDefinitions.Add(new() { Height=GridLength.Auto });
        _plots.RowDefinitions.Add(new() { Height=GridLength.Auto });
        _plots.Children.Add(_highPlot); _plots.Children.Add(_upPlot); _body.Children.Add(_plots);
        SizeChanged+=(_,_)=>LayoutPlots();
        _body.Children.Add(_rules);
        var editor = new StackPanel { Spacing = 10 };
        editor.Children.Add(new TextBlock { Text="部分角色加入可歪名单后，仅凭名字无法区分 UP。选择具体记录进行校对，可随时恢复自动判定。",FontSize=12,TextWrapping=TextWrapping.Wrap });
        AutomationProperties.SetName(_record,"校对出货记录"); editor.Children.Add(_record);
        var actions = new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };
        foreach(var title in new[]{"自动判定","UP","非 UP","待校对"}) _status.Items.Add(title);
        _status.SelectedIndex=0; AutomationProperties.SetName(_status,"出货判定");
        actions.Children.Add(_status);
        var save=new Button{Content="保存判定"}; save.Click+=SaveCorrection; actions.Children.Add(save);
        editor.Children.Add(actions); editor.Children.Add(_correctionInfo);
        _editor.Content=editor; _body.Children.Add(_editor);
        Content=_body;
        _account.SelectionChanged+=(_,_)=>{if(!_setting) PopulatePools();};
        _pool.SelectionChanged+=(_,_)=>{if(!_setting) Render();};
        _record.SelectionChanged+=(_,_)=>ShowCorrection();
    }
    public async Task SetUigfAsync(UigfGachaAnalysis analysis)
    {
        var revision=++_revision;
        IReadOnlyDictionary<string,GachaUpStatus> corrections;
        try { corrections=await _store.ReadAsync(); } catch(Exception ex) when(ex is IOException or InvalidDataException) { corrections=new Dictionary<string,GachaUpStatus>(); _correctionInfo.Text="校对文件无法读取，当前显示自动判定；原始抽卡档案保留。"; }
        if(revision!=_revision)return;
        _corrections=corrections; _analysis=analysis; _endfield=null;
        _knownItems=ReadKnownItems(analysis.GameId);
        _setting=true;
        _account.ItemsSource=analysis.Accounts.Select(a=>a.AccountLabel).ToArray();
        _account.Visibility=analysis.Accounts.Count>1?Visibility.Visible:Visibility.Collapsed;
        _account.SelectedIndex=analysis.Accounts.Count==0?-1:0;
        _setting=false; PopulatePools();
    }
    public void SetEndfield(EndfieldGachaAnalysis analysis, int weaponRecords)
    {
        ++_revision; _endfield=analysis; _analysis=UigfGachaAnalysis.Empty;
        _account.Visibility=Visibility.Collapsed;
        _setting=true; _pool.ItemsSource=Enum.GetValues<EndfieldPoolCategory>().Where(c=>Flatten(analysis.Pools).Any(p=>p.Category==c))
            .Select(c=>new EndfieldCategory(c, c switch{EndfieldPoolCategory.Standard=>"基础寻访",EndfieldPoolCategory.Chartered=>"特许寻访",EndfieldPoolCategory.Refactor=>"重构寻访",EndfieldPoolCategory.Celebration=>"庆典寻访",_=>"其他寻访"})).Cast<object>().Concat(analysis.WeaponPools.Select(p=>new EndfieldWeaponSelection(p))).ToArray();
        _pool.DisplayMemberPath="Title"; _pool.SelectedIndex=_pool.Items.Count>0?0:-1; _setting=false;
        _rules.Text=$"终末地暂不提供出货散点图。付费抽、免费抽和 UP 分别统计；重构池保底跨期继承，免费出货不重置付费保底。另有武器记录 {weaponRecords} 条。";
        Render();
    }
    private static IEnumerable<EndfieldPoolAnalysis> Flatten(IEnumerable<EndfieldPoolAnalysis> pools)
        => pools.SelectMany(p=>new[]{p}.Concat(Flatten(p.PreviousPhases??[]))).DistinctBy(p=>p.PoolKey.Length>0?p.PoolKey:p.PoolId+"\u001F"+p.PoolVersion);
    private void PopulatePools()
    {
        var account=_account.SelectedIndex>=0&&_account.SelectedIndex<_analysis.Accounts.Count?_analysis.Accounts[_account.SelectedIndex]:null;
        _stats=account is null?[]:GachaStatisticsAnalyzer.Analyze(_analysis.GameId,account,_corrections,_knownItems);
        _setting=true; _pool.DisplayMemberPath="Title"; _pool.ItemsSource=_stats;
        _pool.SelectedIndex=_stats.Count>0?0:-1; _setting=false; Render();
    }
    private void LayoutPlots()
    {
        var sideBySide=ActualWidth>=700 && _upPlot.Visibility==Visibility.Visible;
        _plots.ColumnDefinitions[1].Width=sideBySide?new GridLength(1,GridUnitType.Star):new GridLength(0);
        Grid.SetColumn(_upPlot,sideBySide?1:0); Grid.SetRow(_upPlot,sideBySide?0:1);
    }
    private void Render()
    {
        if(_endfield is not null)
        {
            _highPlot.Visibility=_upPlot.Visibility=_editor.Visibility=Visibility.Collapsed;
            if(_pool.SelectedItem is EndfieldWeaponSelection weapon)
            {
                var p=weapon.Pool;
                _summary.Text=$"{p.Title} · 武器申领 · {p.Draws} 条出货记录\n六星 {p.SixStars}（{100d*p.SixStars/Math.Max(1,p.Draws):0.00}%） · 五星 {p.FiveStars} · 其他星级 {p.OtherRarity}";
                _rules.Text="武器池按池标识独立统计，仅计算出货记录，赠送事件不计入。档案未提供抽数、定轨及 UP 判定的完整规则，暂不推算保底或绘制散点图。";
                return;
            }
            _rules.Text="终末地暂不提供出货散点图。付费抽、免费抽和 UP 分别统计；重构池保底跨期继承，免费出货不重置付费保底。";
            if(_pool.SelectedItem is not EndfieldCategory category){_summary.Text="暂无可统计的终末地寻访记录。";return;}
            var pools=Flatten(_endfield.Pools).Where(p=>p.Category==category.Category).ToArray();
            var total=pools.Sum(p=>p.DrawCount); var six=pools.Sum(p=>p.SixStarPulls.Count);
            _summary.Text=$"{category.Title} · 共 {total} 抽 · {pools.Length} 个卡池 / 分期\n付费 {pools.Sum(p=>p.PaidDrawCount)} · 免费 {pools.Sum(p=>p.FreeDrawCount)} · 六星 {six} · 六星占比 {(total>0?100d*six/total:0):0.00}%\n付费六星 {six-pools.Sum(p=>p.FreeSixStarCount)} · 免费六星 {pools.Sum(p=>p.FreeSixStarCount)} · 已识别 UP {pools.Sum(p=>p.SixStarPulls.Count(s=>s.IsFeatured))}";
            return;
        }
        if(_pool.SelectedItem is not GachaPoolStatistics pool)
        {
            _highPlot.Visibility=_upPlot.Visibility=_editor.Visibility=Visibility.Collapsed;
            _summary.Text="暂无可统计的抽卡记录；同步或导入档案后显示。"; _rules.Text=""; return;
        }
        var complete=pool.HighPoints.Where(p=>p.Complete).Select(p=>p.Pulls).Order().ToArray();
        var median=complete.Length==0?"—":((complete[(complete.Length-1)/2]+complete[complete.Length/2])/2d).ToString("0.0");
        var identified=pool.FeaturedCount+pool.NonFeaturedCount;
        _summary.Text=$"{pool.Title} · {pool.Draws} 抽 · {pool.HighLabel} {pool.HighCount}（{100d*pool.HighCount/Math.Max(1,pool.Draws):0.00}%） · 次高星级 {pool.MiddleCount} · 其他星级 {pool.LowCount}\n完整出货间隔 {complete.Length} 个 · 均值 {Mean(pool.Average)} · 中位数 {median} · 最短 {pool.Minimum?.ToString()??"—"} / 最长 {pool.Maximum?.ToString()??"—"} 抽 · 本地已垫 {pool.CurrentPity}\n已识别 UP {pool.FeaturedCount} · 非 UP {pool.NonFeaturedCount} · 待校对 {pool.UnknownCount}"+(identified>0?$" · UP 占比 {pool.FeaturedCount*100d/identified:0.00}%（含保底）":"");
        _highPlot.Visibility=Visibility.Visible;
        var max=pool.PityMaximum??Math.Max(10,pool.HighPoints.Select(p=>p.Pulls).DefaultIfEmpty(10).Max());
        _highPlot.SetData(pool.HighLabel+"出货散点",pool.HighPoints,max,pool.Average);
        _upPlot.Visibility=pool.FeaturedMaximum is not null?Visibility.Visible:Visibility.Collapsed;
        LayoutPlots();
        if(pool.FeaturedMaximum is int upMax)_upPlot.SetData("UP "+pool.HighLabel+"出货散点",pool.FeaturedPoints,upMax,pool.FeaturedAverage);
        _rules.Text="每个圆点对应一次出货；同抽数的结果错位排列。均值只使用有前一次出货记录的完整间隔，各卡池种类独立计算。"
            +(pool.FeaturedMaximum is not null?" UP 间隔从上一件确定的 UP 物品起算；待校对结果会中断可确认的间隔，避免误计均值。UP 占比包含保底出货，不能当作小保底胜率。":"")
            +(pool.PityMaximum is null?" 此类卡池的横轴按本地记录范围绘制，不代表保底上限。":"")
            +(_analysis.GameId==BuiltInGameIds.GenshinImpact&&pool.Type=="302"?" 武器 UP 指当期任一 UP 武器，不表示定轨目标。":"");
        _editor.Visibility=pool.FeaturedMaximum is not null?Visibility.Visible:Visibility.Collapsed;
        _editPoints=pool.HighPoints.Reverse().ToArray();
        _record.ItemsSource=_editPoints.Select(p=>$"{p.Name} · {p.Time} · {p.Pulls} 抽").ToArray();
        _record.SelectedIndex=_editPoints.Count>0?0:-1;
        ShowCorrection();
    }
    private void ShowCorrection()
    {
        if(_record.SelectedIndex<0||_record.SelectedIndex>=_editPoints.Count||_pool.SelectedItem is not GachaPoolStatistics pool)return;
        var point=_editPoints[_record.SelectedIndex]; var uid=_analysis.Accounts[_account.SelectedIndex].Uid;
        var key=GachaStatisticsAnalyzer.CorrectionKey(_analysis.GameId,uid,pool.Type,point.RecordId);
        _status.SelectedIndex=_corrections.TryGetValue(key,out var saved)?saved switch{GachaUpStatus.Featured=>1,GachaUpStatus.NonFeatured=>2,_=>3}:0;
        _correctionInfo.Text=$"当前结果：{point.UpStatus switch{GachaUpStatus.Featured=>"UP",GachaUpStatus.NonFeatured=>"非 UP",_=>"待校对"}}。校对只影响本地统计，不修改原始抽卡档案。";
    }
    private async void SaveCorrection(object sender,RoutedEventArgs e)
    {
        if(_record.SelectedIndex<0||_record.SelectedIndex>=_editPoints.Count||_pool.SelectedItem is not GachaPoolStatistics pool)return;
        var point=_editPoints[_record.SelectedIndex];
        if(string.IsNullOrWhiteSpace(point.RecordId)){_correctionInfo.Text="此记录缺少唯一编号，不能保存校对。";return;}
        var game=_analysis.GameId;
        var uid=_analysis.Accounts[_account.SelectedIndex].Uid; var revision=_revision;
        var status=_status.SelectedIndex switch{1=>GachaUpStatus.Featured,2=>GachaUpStatus.NonFeatured,3=>GachaUpStatus.Unknown,_=>(GachaUpStatus?)null};
        try
        {
            await _store.SetAsync(GachaStatisticsAnalyzer.CorrectionKey(game,uid,pool.Type,point.RecordId),status);
            IReadOnlyDictionary<string,GachaUpStatus> corrections;
        try { corrections=await _store.ReadAsync(); } catch(Exception ex) when(ex is IOException or InvalidDataException) { corrections=new Dictionary<string,GachaUpStatus>(); _correctionInfo.Text="校对文件无法读取，当前显示自动判定；原始抽卡档案保留。"; }
            if(revision!=_revision)return;
            _corrections=corrections;
            var selected=pool.Type; PopulatePools();
            _pool.SelectedIndex=_stats.ToList().FindIndex(p=>p.Type==selected);
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException){_correctionInfo.Text="校对未保存，请检查数据目录权限及校对文件。";}
    }
    private static string Mean(double? value)=>value is double mean?$"{mean:0.0} 抽":"—";
    private static Brush Brush(string name)=>(Brush)Application.Current.Resources[name];
    private static HashSet<string> ReadKnownItems(string game)
    {
        var result=new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var index=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Assets","Games","Gacha","Portraits","portrait-index.json")));
            foreach(var item in index.RootElement.EnumerateArray())
                if(item.GetProperty("GameId").GetString()==game)
                {
                    if(item.GetProperty("Name").GetString() is string name)result.Add(name);
                    if(item.TryGetProperty("Aliases",out var aliases))foreach(var alias in aliases.EnumerateArray())if(alias.GetString() is string value)result.Add(value);
                }
        }
        catch(Exception ex) when(ex is IOException or JsonException){ }
        return result;
    }
    private sealed record EndfieldWeaponSelection(EndfieldWeaponPoolStatistics Pool) { public string Title => "武器 · " + Pool.Title; }
    private sealed record EndfieldCategory(EndfieldPoolCategory Category,string Title);
}