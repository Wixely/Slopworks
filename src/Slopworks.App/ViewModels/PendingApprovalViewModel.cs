using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Slopworks.Core.Actions;

namespace Slopworks.App.ViewModels;

/// <summary>One alternative on a choice card.</summary>
public partial class ApprovalChoiceViewModel(int index, ActionChoice choice, Action<int> choose) : ObservableObject
{
    public string Label => index == 0 ? $"{choice.Label} (default)" : choice.Label;
    public string Detail => choice.Detail;

    [RelayCommand]
    private void Choose() => choose(index);
}

/// <summary>One safe-mode confirmation card. The verbatim Detail is what will actually run.</summary>
public partial class PendingApprovalViewModel : ObservableObject
{
    private readonly PendingApproval _pending;
    private readonly Action _done;

    public PendingApprovalViewModel(PendingApproval pending, Action done)
    {
        _pending = pending;
        _done = done;
        Choices = pending.Action.Choices is { Count: > 0 } choices
            ? [.. choices.Select((c, i) => new ApprovalChoiceViewModel(i, c, ResolveChoice))]
            : [];
    }

    public string Kind => _pending.Action.Kind.ToString();
    public string Description => _pending.Action.Description;
    public string Detail => _pending.Action.Detail;

    public IReadOnlyList<ApprovalChoiceViewModel> Choices { get; }
    public bool HasChoices => Choices.Count > 0;
    public bool HasSingleApprove => Choices.Count == 0;

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
        _pending.Resolve(decision);
        _done();
    }

    private void ResolveChoice(int index)
    {
        _pending.ResolveChoice(index);
        _done();
    }
}
