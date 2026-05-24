using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ProjectChannelsPage : ContentPage
{
    public ProjectChannelsPage(ProjectChannelsViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
