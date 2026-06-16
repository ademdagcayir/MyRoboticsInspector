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
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"InspectionsPage.OnAppearing error: {ex}");
            await DisplayAlert("Hata", $"İncelemeler yüklenirken hata: {ex.Message}", "Tamam");
        }
    }
}
