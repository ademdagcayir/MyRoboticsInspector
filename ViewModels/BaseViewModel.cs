using CommunityToolkit.Mvvm.ComponentModel;

namespace MyRoboticsInspector.ViewModels;

public abstract partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string? title;

    [ObservableProperty]
    private string? statusMessage;
}
