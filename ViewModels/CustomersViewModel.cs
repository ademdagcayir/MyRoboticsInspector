using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

public partial class CustomersViewModel : BaseViewModel
{
    private readonly DatabaseService _db;

    public ObservableCollection<Customer> Customers { get; } = new();

    [ObservableProperty] private Customer editing = new();
    [ObservableProperty] private bool isEditing;

    public CustomersViewModel(DatabaseService db)
    {
        _db = db;
        Title = "Müşteriler";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            IsBusy = true;
            var list = await _db.GetCustomersAsync();
            Customers.Clear();
            foreach (var c in list) Customers.Add(c);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void New()
    {
        Editing = new Customer();
        IsEditing = true;
    }

    [RelayCommand]
    private void Edit(Customer? customer)
    {
        if (customer is null) return;
        Editing = new Customer
        {
            Id = customer.Id,
            Name = customer.Name,
            Phone = customer.Phone,
            Email = customer.Email,
            Address = customer.Address,
            TaxNo = customer.TaxNo,
            Notes = customer.Notes,
            CreatedAt = customer.CreatedAt
        };
        IsEditing = true;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Editing.Name))
        {
            StatusMessage = "Müşteri adı boş olamaz";
            return;
        }
        await _db.SaveCustomerAsync(Editing);
        IsEditing = false;
        await LoadAsync();
    }

    [RelayCommand]
    private void Cancel() => IsEditing = false;

    [RelayCommand]
    private async Task DeleteAsync(Customer? customer)
    {
        if (customer is null) return;

        // Müşteriye bağlı proje varsa silmeyi engelle — projeler öksüz CustomerId ile kalmasın.
        var jobs = await _db.GetJobsAsync(customer.Id);
        if (jobs.Count > 0)
        {
            await Shell.Current.DisplayAlert(
                "Silinemiyor",
                $"\"{customer.Name}\" müşterisine bağlı {jobs.Count} proje var.\nÖnce bu projeleri silin veya başka müşteriye taşıyın.",
                "Tamam");
            return;
        }

        var confirmed = await Shell.Current.DisplayAlert(
            "Müşteriyi sil",
            $"\"{customer.Name}\" silinsin mi?\n\nBu işlem geri alınamaz.",
            "Sil", "Vazgeç");
        if (!confirmed) return;

        await _db.DeleteCustomerAsync(customer);
        StatusMessage = "Müşteri silindi";
        await LoadAsync();
    }
}
