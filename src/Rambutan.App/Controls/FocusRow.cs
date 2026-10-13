using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Rambutan.App.Controls;

/// <summary>
/// A row of a plain items control that is a focus stop of its own (the artist
/// page's top songs). A plain Grid's automation peer is not a control element,
/// so a screen reader would land on the row and say nothing; this one is a list
/// item that speaks its <c>AutomationProperties.Name</c>.
/// </summary>
public class FocusRow : Grid
{
    public FocusRow() => Focusable = true;

    protected override AutomationPeer OnCreateAutomationPeer() => new FocusRowAutomationPeer(this);

    private sealed class FocusRowAutomationPeer(FocusRow owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
    }
}
