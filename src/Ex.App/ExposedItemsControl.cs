using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Ex.App;

/// <summary>
/// An ItemsControl whose item contents (buttons, texts) are visible to UI Automation, so screen readers and UI tests
/// can reach them. A plain ItemsControl hides its templated items from the automation tree.
/// </summary>
public sealed class ExposedItemsControl : ItemsControl
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
