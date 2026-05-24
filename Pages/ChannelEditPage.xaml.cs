using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ChannelEditPage : ContentPage
{
    public ChannelEditPage(ChannelEditViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
