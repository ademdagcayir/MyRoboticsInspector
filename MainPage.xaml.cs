using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector;

public partial class MainPage : ContentPage
{
    private readonly LiveViewModel _vm;

    public MainPage(LiveViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadSettingsAsync();
    }
}
