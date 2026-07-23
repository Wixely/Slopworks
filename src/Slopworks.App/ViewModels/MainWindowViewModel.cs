using CommunityToolkit.Mvvm.ComponentModel;
using Slopworks.Core.Engine;

namespace Slopworks.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    // Tab order in MainWindow.axaml.
    private const int DashboardTab = 0;
    private const int ServerTab = 2;
    private const int SettingsTab = 5;

    public DashboardViewModel Dashboard { get; }
    public SetupWizardViewModel Wizard { get; }
    public ServerViewModel Server { get; }
    public ModelsViewModel Models { get; }
    public MaintenanceViewModel Maintenance { get; }
    public SettingsViewModel Settings { get; }
    public PlatformsViewModel Platforms { get; }
    public SystemUsageViewModel SystemUsage { get; }
    public LogsViewModel Logs { get; }

    [ObservableProperty]
    private int _selectedTabIndex;

    public MainWindowViewModel(SlopworksHost host)
    {
        Dashboard = new DashboardViewModel(host);
        Wizard = new SetupWizardViewModel(host);
        Server = new ServerViewModel(host);
        Models = new ModelsViewModel(host);
        Maintenance = new MaintenanceViewModel(host);
        Settings = new SettingsViewModel(host);
        Platforms = new PlatformsViewModel(host);
        SystemUsage = new SystemUsageViewModel(host);
        Logs = new LogsViewModel(host);

        // Land on Server when already set up, else the Dashboard to guide setup. Each tab
        // does its own work only once activated, so nothing probes until it is viewed.
        // Set the backing field directly so we can activate the initial tab explicitly.
        _selectedTabIndex = SetupState.IsComplete(host.Journal) ? ServerTab : DashboardTab;
        (TabAt(_selectedTabIndex) as IActivatableTab)?.Activate();

        // The System page's "Edit" button jumps here to the Settings tab.
        host.Profiles.EditRequested += () => SelectedTabIndex = SettingsTab;
    }

    partial void OnSelectedTabIndexChanged(int oldValue, int newValue)
    {
        (TabAt(oldValue) as IActivatableTab)?.Deactivate();
        (TabAt(newValue) as IActivatableTab)?.Activate();
    }

    private object TabAt(int index) => index switch
    {
        0 => Dashboard,
        1 => Wizard,
        2 => Server,
        3 => SystemUsage,
        4 => Models,
        5 => Settings,
        6 => Platforms,
        7 => Maintenance,
        8 => Logs,
        _ => this,
    };
}
