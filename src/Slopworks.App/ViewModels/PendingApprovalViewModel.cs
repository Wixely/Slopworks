using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Actions;

namespace Slopworks.App.ViewModels;

/// <summary>One safe-mode confirmation card. The verbatim Detail is what will actually run.</summary>
public partial class PendingApprovalViewModel(PendingApproval pending, Action done) : ObservableObject
{
    public string Kind => pending.Action.Kind.ToString();
    public string Description => pending.Action.Description;
    public string Detail => pending.Action.Detail;

    [RelayCommand]
    private void Approve() => Resolve(ActionDecision.Approved);

    [RelayCommand]
    private void ApproveAllForStep() => Resolve(ActionDecision.ApprovedAllForStep);

    [RelayCommand]
    private void Deny() => Resolve(ActionDecision.Denied);

    [RelayCommand]
    private void Abort() => Resolve(ActionDecision.Aborted);

    private void Resolve(ActionDecision decision)
    {
        pending.Resolve(decision);
        done();
    }
}
