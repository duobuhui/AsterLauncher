using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AsterLauncher.App.Services;

public sealed class FilePickerService : IFilePickerService
{
    public async Task<string?> PickGameDirectoryAsync(Window owner)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task<string?> PickExecutableAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".exe");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickImageAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            ViewMode = PickerViewMode.Thumbnail
        };
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".webp");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickJsonAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            ViewMode = PickerViewMode.List
        };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickJsonSaveAsync(Window owner, string suggestedFileName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add("UIGF JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    public async Task<string?> PickCsvSaveAsync(Window owner, string suggestedFileName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedFileName
        };
        picker.FileTypeChoices.Add("CSV", [".csv"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(owner));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
