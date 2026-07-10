using CommunityToolkit.Mvvm.ComponentModel;

namespace Slopworks.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public DashboardViewModel Dashboard { get; }

    public MainWindowViewModel(SlopworksHost host)
    {
        Dashboard = new DashboardViewModel(host);
        Dashboard.RefreshCommand.Execute(null);
    }
}
