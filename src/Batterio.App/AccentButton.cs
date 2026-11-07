using Microsoft.Maui.Handlers;

namespace Batterio.App;

/// <summary>
/// A button drawn with the native WinUI accent style, so it uses the same Windows accent color
/// as the checkboxes and follows accent and light/dark theme changes, including hover, pressed
/// and disabled states.
/// </summary>
public sealed class AccentButton : Button
{
    internal static void Register() =>
        ButtonHandler.Mapper.AppendToMapping(nameof(AccentButton), (handler, view) =>
        {
            if (view is AccentButton &&
                Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) &&
                style is Microsoft.UI.Xaml.Style accentStyle)
            {
                handler.PlatformView.Style = accentStyle;
            }
        });
}
