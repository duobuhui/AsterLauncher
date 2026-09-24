using Microsoft.UI.Xaml.Media;

namespace AsterLauncher.App.ViewModels;

public sealed record InfoChipViewModel(
    string Label,
    string Value,
    string Glyph,
    string ToolTip,
    string Detail,
    Brush AccentBrush);
