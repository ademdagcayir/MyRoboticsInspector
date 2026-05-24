using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyRoboticsInspector.Models;
using MyRoboticsInspector.Services;

namespace MyRoboticsInspector.ViewModels;

public partial class LoginViewModel : BaseViewModel
{
    private readonly AuthService _auth;

    public ObservableCollection<Profile> Profiles { get; } = new();

    [ObservableProperty] private Profile? selectedProfile;
    [ObservableProperty] private string pinInput = string.Empty;
    [ObservableProperty] private string? errorMessage;

    // New profile form
    [ObservableProperty] private bool isCreating;
    [ObservableProperty] private string newName = string.Empty;
    [ObservableProperty] private string newPin = string.Empty;
    [ObservableProperty] private string newEmail = string.Empty;

    public bool HasProfiles => Profiles.Count > 0;
    public bool NeedsPin => SelectedProfile is { Pin.Length: > 0 };

    partial void OnSelectedProfileChanged(Profile? value)
    {
        PinInput = "";
        ErrorMessage = null;
        OnPropertyChanged(nameof(NeedsPin));
    }

    public LoginViewModel(AuthService auth)
    {
        _auth = auth;
        Title = "Giriş";
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        var list = await _auth.GetProfilesAsync();
        Profiles.Clear();
        foreach (var p in list) Profiles.Add(p);
        OnPropertyChanged(nameof(HasProfiles));

        // First-run: no profiles → jump straight to "create" form.
        if (!HasProfiles) IsCreating = true;
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (SelectedProfile is null)
        {
            ErrorMessage = "Bir profil seçin";
            return;
        }
        var ok = await _auth.LoginAsync(SelectedProfile, NeedsPin ? PinInput : null);
        if (!ok) ErrorMessage = "PIN hatalı";
    }

    [RelayCommand]
    private void ShowCreate()
    {
        IsCreating = true;
        NewName = "";
        NewPin = "";
        NewEmail = "";
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreating = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(NewName))
        {
            ErrorMessage = "Ad gerekli";
            return;
        }
        if (!string.IsNullOrEmpty(NewPin) && (NewPin.Length < 4 || NewPin.Length > 6 || !NewPin.All(char.IsDigit)))
        {
            ErrorMessage = "PIN 4-6 rakamdan oluşmalı veya boş bırakılmalı";
            return;
        }

        var profile = await _auth.CreateAsync(NewName, string.IsNullOrEmpty(NewPin) ? null : NewPin,
                                              string.IsNullOrWhiteSpace(NewEmail) ? null : NewEmail);
        Profiles.Insert(0, profile);
        OnPropertyChanged(nameof(HasProfiles));
        IsCreating = false;

        // Auto-login on first profile creation.
        await _auth.LoginAsync(profile, profile.Pin);
    }

    [RelayCommand]
    private async Task DeleteAsync(Profile? profile)
    {
        if (profile is null) return;
        await _auth.DeleteAsync(profile);
        Profiles.Remove(profile);
        OnPropertyChanged(nameof(HasProfiles));
        if (!HasProfiles) IsCreating = true;
    }
}
