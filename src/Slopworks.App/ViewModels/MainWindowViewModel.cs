using CommunityToolkit.Mvvm.ComponentModel;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    // Tab order in MainWindow.axaml.
    private const int DashboardTab = 0;
    private const int ServerTab = 2;

    public DashboardViewModel Dashboard { get; }
    public SetupWizardViewModel Wizard { get; }
    public ServerViewModel Server { get; }
    public MaintenanceViewModel Maintenance { get; }
    public SettingsViewModel Settings { get; }
    public SystemUsageViewModel SystemUsage { get; }
    public LogsViewModel Logs { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainWindowViewModel(SlopworksHost host)
    {
        Dashboard = new DashboardViewModel(host);
        Wizard = new SetupWizardViewModel(host);
        Server = new ServerViewModel(host);
        Maintenance = new MaintenanceViewModel(host);
        Settings = new SettingsViewModel(host);
        SystemUsage = new SystemUsageViewModel(host);
        Logs = new LogsViewModel(host);
        Logs.RefreshCommand.Execute(null); // cheap file read

        if (SetupState.IsComplete(host.Journal))
        {
            // Already set up: land on Server and leave the (slow) probes to a Refresh click.
            SelectedTabIndex = ServerTab;
        }
        else
        {
            // Fresh/incomplete: land on the Dashboard and auto-diagnose to guide setup.
            SelectedTabIndex = DashboardTab;
            Dashboard.RefreshCommand.Execute(null);
            Maintenance.RefreshCommand.Execute(null);
        }
    }
}
