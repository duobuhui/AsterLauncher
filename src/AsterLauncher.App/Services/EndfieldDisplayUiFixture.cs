#if DEBUG
using AsterLauncher.Infrastructure;
using System.Text.Json;
namespace AsterLauncher.App.Services;
// Filesystem-only backend restricted to the existing isolated DEBUG acceptance fixture.
internal sealed class EndfieldDisplayUiFixture : IEndfieldDisplayStore
{
    private readonly string _path = Path.Combine(LauncherDataPaths.ResolveDataDirectory(), "display-fixture.json");
    public EndfieldDisplayUiFixture()
    {
        if (!EndfieldMaintenanceUiFixture.Enabled) throw new InvalidOperationException("Display fixture is disabled.");
        if (!File.Exists(_path)) File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["video_resolution_width_h583690364"] = 2560, ["video_resolution_height_h2517654917"] = 1440,
            ["video_full_screen_h1998742411"] = 1, ["Screenmanager Fullscreen mode_h3630240806"] = 1,
            ["video_quality_main_h2648490162"] = 1, ["video_quality_shadowmap_1_h128234669"] = 1000
        }));
    }
    public Dictionary<string, int> Read() => JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_path))!;
    public void Write(string name, int value) { var values=Read(); values[name]=value; File.WriteAllText(_path,JsonSerializer.Serialize(values)); }
}
#endif