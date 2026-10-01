using AsterLauncher.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.Views;

public sealed class StorageManagementControl:UserControl
{
    private readonly LocalStorageService _service=new(LauncherDataPaths.ResolveDataDirectory(),LauncherDataPaths.InstallationDirectory,Path.GetTempPath());
    private readonly StackPanel _items=new(){Spacing=8};
    private readonly TextBlock _summary=new(){Text="扫描启动器缓存、日志、云壁纸及已识别的旧配置目录。当前配置、抽卡档案和游戏安装文件不列入清理。",TextWrapping=TextWrapping.Wrap,FontSize=13};
    private readonly Button _scan=new(){Content="扫描本地文件"};
    private readonly Button _delete=new(){Content="删除所选项目",IsEnabled=false};
    private readonly TextBlock _result=new(){TextWrapping=TextWrapping.Wrap,FontSize=12};
    private readonly List<(CheckBox Box,IReadOnlyList<StorageFile> Files,IReadOnlyList<StorageDirectory> Directories)> _groups=[];
    public StorageManagementControl()
    {
        var body=new StackPanel{Spacing=12};
        body.Children.Add(new TextBlock{Text="本地存储",FontSize=20,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold});
        body.Children.Add(_summary);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8};actions.Children.Add(_scan);actions.Children.Add(_delete);body.Children.Add(actions);
        body.Children.Add(_items);body.Children.Add(_result);Content=body;
        _scan.Click+=async(_,_)=>await ScanAsync();_delete.Click+=DeleteAsync;
    }
    private async Task ScanAsync()
    {
        _scan.IsEnabled=_delete.IsEnabled=false;_result.Text="正在扫描…";
        try
        {
            var scan=await Task.Run(()=>_service.ScanAsync());_items.Children.Clear();_groups.Clear();
            foreach(var group in scan.Files.Select(f=>(f.Category,f.Root))
                .Concat(scan.EmptyDirectories.Select(d=>(d.Category,d.Root))).Distinct())
            {
                var files=scan.Files.Where(f=>f.Category==group.Category&&f.Root==group.Root).ToArray();
                var directories=scan.EmptyDirectories.Where(d=>d.Category==group.Category&&d.Root==group.Root).ToArray();
                var legacy=files.Any(f=>f.IsLegacy)||directories.Any(d=>d.IsLegacy);
                var box=new CheckBox{Content=$"{group.Category} · {files.Length} 个文件 · {directories.Length} 个空目录 · {Bytes(files.Sum(f=>f.Size))}",IsChecked=false};
                box.Checked+=(_,_)=>UpdateSelection();box.Unchecked+=(_,_)=>UpdateSelection();
                var panel=new StackPanel{Spacing=4};panel.Children.Add(box);
                panel.Children.Add(new TextBlock{Text=group.Root,FontSize=12,TextWrapping=TextWrapping.Wrap,IsTextSelectionEnabled=true});
                if(legacy)panel.Children.Add(new TextBlock{Text="包含旧配置、时长和抽卡档案。请先确认已迁移或备份，再选择删除。",FontSize=12,TextWrapping=TextWrapping.Wrap});
                var details=new Expander{Header="查看文件列表",HorizontalAlignment=HorizontalAlignment.Stretch};
                details.Content=new TextBox{Text=string.Join(Environment.NewLine,files.Select(f=>$"{f.Relative}  ·  {Bytes(f.Size)}").Concat(directories.Select(d=>$"{(d.Relative.Length==0?"此目录":d.Relative)}  ·  空目录"))),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=160};panel.Children.Add(details);
                _items.Children.Add(new Border{Child=panel,Padding=new(14),CornerRadius=new(12),Background=(Brush)Application.Current.Resources["HeroGlassBrush"],BorderBrush=(Brush)Application.Current.Resources["GlassEdgeBrush"],BorderThickness=new(1)});
                _groups.Add((box,files,directories));
            }
            _result.Text=$"找到 {scan.Files.Count} 个可清理文件、{scan.EmptyDirectories.Count} 个空目录，文件大小 {Bytes(scan.Files.Sum(f=>f.Size))}；保护或跳过 {scan.ProtectedFiles} 项。"+string.Join(" ",scan.Notes);
            if(scan.Files.Count==0&&scan.EmptyDirectories.Count==0)_result.Text+=" 当前没有可清理的内容。";
        }
        catch(Exception ex) when(ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {_result.Text="扫描未完成：目录被占用、任务索引损坏或路径无法访问。请结束正在进行的任务后重试。";}
        finally{_scan.IsEnabled=true;UpdateSelection();}
    }
    private void UpdateSelection()=>_delete.IsEnabled=_scan.IsEnabled&&_groups.Any(g=>g.Box.IsChecked==true);
    private async void DeleteAsync(object sender,RoutedEventArgs e)
    {
        var selected=_groups.Where(g=>g.Box.IsChecked==true).SelectMany(g=>g.Files).ToArray();
        var directories=_groups.Where(g=>g.Box.IsChecked==true).SelectMany(g=>g.Directories).ToArray();
        if(selected.Length==0&&directories.Length==0)return;
        var legacy=selected.Any(f=>f.IsLegacy)||directories.Any(d=>d.IsLegacy);
        var dialog=new ContentDialog{XamlRoot=XamlRoot,Title="删除所选项目",Content=$"将删除 {selected.Length} 个文件、{directories.Length} 个空目录，文件大小 {Bytes(selected.Sum(f=>f.Size))}。\n"+(legacy?"其中包含旧配置或旧抽卡档案，请确认已迁移或备份。\n":"")+"文件删除后无法撤销。删除后会一并清理所属缓存目录中的空文件夹。当前配置和游戏安装文件保持原样。",PrimaryButtonText="删除",CloseButtonText="返回",DefaultButton=ContentDialogButton.Close};
        if(await dialog.ShowAsync()!=ContentDialogResult.Primary)return;
        _scan.IsEnabled=_delete.IsEnabled=false;
        try
        {
            var result=await Task.Run(()=>_service.DeleteAsync(selected,directories));
            await ScanAsync();_result.Text=$"已删除 {result.Deleted} 个文件、{result.DeletedDirectories} 个空目录（{Bytes(result.Bytes)}），预计释放 {Bytes(result.EstimatedFreedBytes)}；{result.Skipped} 项因变更或占用跳过。硬链接只删除本目录项，仍被引用的数据不计入释放空间。";
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or InvalidDataException)
        {_result.Text=ex is InvalidOperationException?ex.Message:"清理未完成，文件可能被占用；请重新扫描。";}
        finally{_scan.IsEnabled=true;UpdateSelection();}
    }
    private static string Bytes(long value)=>value>=1L<<30?$"{value/(double)(1L<<30):0.##} GiB":value>=1L<<20?$"{value/(double)(1L<<20):0.##} MiB":value>=1024?$"{value/1024d:0.##} KiB":$"{value:N0} B";
}