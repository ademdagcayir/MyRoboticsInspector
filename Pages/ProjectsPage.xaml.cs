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
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ProjectsPage.OnAppearing error: {ex}");
            await DisplayAlert("Hata", $"Projeler yüklenirken hata: {ex.Message}", "Tamam");
        }
    }
}
