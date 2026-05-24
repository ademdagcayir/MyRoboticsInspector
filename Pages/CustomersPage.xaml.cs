using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class CustomersPage : ContentPage
{
    private readonly CustomersViewModel _vm;

    public CustomersPage(CustomersViewModel vm)
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
