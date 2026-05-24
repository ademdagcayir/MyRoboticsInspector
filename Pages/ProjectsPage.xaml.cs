using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ProjectsPage : ContentPage
{
    private readonly ProjectsViewModel _vm;

    public ProjectsPage(ProjectsViewModel vm)
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
