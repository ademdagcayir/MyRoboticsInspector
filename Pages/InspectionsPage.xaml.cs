using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class InspectionsPage : ContentPage
{
    private readonly InspectionsViewModel _vm;

    public InspectionsPage(InspectionsViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();
    }
}
