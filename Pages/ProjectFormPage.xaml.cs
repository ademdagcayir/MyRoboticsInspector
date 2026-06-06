using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ProjectFormPage : ContentPage
{
    private bool _loaded;

    public ProjectFormPage(ProjectFormViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        // ProjectId=0 ise QueryProperty tetiklenmez → burada yüklüyoruz
        if (!_loaded)
        {
            _loaded = true;
            await ((ProjectFormViewModel)BindingContext).LoadAsync();
        }
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _loaded = false; // Geri dönüldüğünde yeniden yüklensin
    }
}
