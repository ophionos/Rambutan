using Avalonia.Input;
using Avalonia.Interactivity;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// The mouse's back and forward buttons (XButton1/XButton2) drive the page history,
/// which is how most mouse users go back. Handled on the tunnel
/// route, so a list or button under the pointer can't swallow the press first.
/// </summary>
public static class HistoryButtons
{
    public static void Attach(InputElement root, NavigationService navigation)
    {
        root.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            switch (e.GetCurrentPoint(root).Properties.PointerUpdateKind)
            {
                case PointerUpdateKind.XButton1Pressed:
                    navigation.Back();
                    e.Handled = true;
                    break;
                case PointerUpdateKind.XButton2Pressed:
                    navigation.Forward();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
