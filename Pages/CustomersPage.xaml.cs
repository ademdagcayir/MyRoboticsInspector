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
        try
        {
            await _vm.LoadAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CustomersPage.OnAppearing error: {ex}");
            await DisplayAlert("Hata", $"Müşteriler yüklenirken hata: {ex.Message}", "Tamam");
        }
    }
}
