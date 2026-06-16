using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class LoginPage : ContentPage
{
    private readonly LoginViewModel _vm;
    private bool _animated;

    public LoginPage(LoginViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadAsync();

        // Giriş animasyonu: logo yukarıdan süzülür, panel alttan kayar (tek sefer).
        if (_animated) return;
        _animated = true;
        try
        {
            HeroBlock.Opacity = 0; HeroBlock.TranslationY = -24;
            GlassPanel.Opacity = 0; GlassPanel.TranslationY = 28;
            FooterLabel.Opacity = 0;

            await Task.WhenAll(
                HeroBlock.FadeTo(1, 420, Easing.CubicOut),
                HeroBlock.TranslateTo(0, 0, 420, Easing.CubicOut));
            await Task.WhenAll(
                GlassPanel.FadeTo(1, 380, Easing.CubicOut),
                GlassPanel.TranslateTo(0, 0, 380, Easing.CubicOut),
                FooterLabel.FadeTo(1, 600, Easing.CubicIn));
        }
        catch { /* animasyon süsleme — hata UI'yi düşürmesin */ }
    }
}
