using System.Windows.Automation.Peers;
using System.Windows.Controls;
using TabControl = System.Windows.Controls.TabControl;

namespace StreamHub.Host;

public sealed class PageContentHost : TabControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new PageContentHostAutomationPeer(this);

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        base.OnSelectionChanged(e);
        if (ReferenceEquals(e.Source, this))
            UIElementAutomationPeer.FromElement(this)?.InvalidatePeer();
    }

    // Navigation lives in the sidebar; expose the selected visual content as a pane.
    // TabControlAutomationPeer requires header containers that this template omits.
    private sealed class PageContentHostAutomationPeer(PageContentHost owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string GetClassNameCore() => nameof(PageContentHost);
    }
}
