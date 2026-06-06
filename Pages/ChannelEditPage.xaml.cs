using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ChannelEditPage : ContentPage
{
    private bool _loaded;

    public ChannelEditPage(ChannelEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }

    protected override async void OnNavigatedTo(NavigatedToEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (_loaded) return;
        _loaded = true;

        var vm = (ChannelEditViewModel)BindingContext;
        // Yeni kanal: jobId var, channelId yok — InitNewAsync henüz çalışmadıysa çalıştır
        if (vm.ChannelId == 0 && vm.JobId > 0)
            await vm.InitNewAsync(vm.JobId);
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        _loaded = false;
    }
}
