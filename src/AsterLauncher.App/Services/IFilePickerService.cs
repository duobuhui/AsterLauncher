using Microsoft.UI.Xaml;

namespace AsterLauncher.App.Services;

public interface IFilePickerService
{
    Task<string?> PickExecutableAsync(Window owner);

    Task<string?> PickGameDirectoryAsync(Window owner);

    Task<string?> PickImageAsync(Window owner);

    Task<string?> PickJsonAsync(Window owner);

    Task<string?> PickJsonSaveAsync(Window owner, string suggestedFileName);

    Task<string?> PickCsvSaveAsync(Window owner, string suggestedFileName);
}
