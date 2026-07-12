namespace Slopworks.App.ViewModels;

/// <summary>
/// A tab that does work only while it is the visible tab. Activate runs when the tab is
/// shown; Deactivate when the user leaves it (used to stop background polling). This keeps
/// diagnostics and metric sampling from running when nobody is looking at them.
/// </summary>
public interface IActivatableTab
{
    void Activate();

    void Deactivate()
    {
    }
}
