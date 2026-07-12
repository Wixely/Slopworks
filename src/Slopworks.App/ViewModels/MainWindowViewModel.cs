using CommunityToolkit.Mvvm.ComponentModel;

namespace Slopworks.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public DashboardViewModel Dashboard { get; }
    public SetupWizardViewModel Wizard { get; }
    public ServerViewModel Server { get; }
    public MaintenanceViewModel Maintenance { get; }
    public SettingsViewModel Settings { get; }
    public SystemUsageViewModel SystemUsage { get; }

    public MainWindowViewModel(SlopworksHost host)
    {
        Dashboard = new DashboardViewModel(host);
        Wizard = new SetupWizardViewModel(host);
        Server = new ServerViewModel(host);
        Maintenance = new MaintenanceViewModel(host);
        Settings = new SettingsViewModel(host);
        SystemUsage = new SystemUsageViewModel(host);
        Dashboard.RefreshCommand.Execute(null);
        Maintenance.RefreshCommand.Execute(null);
    }
}
