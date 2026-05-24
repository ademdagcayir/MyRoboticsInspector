using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class InspectionDetailPage : ContentPage
{
    public InspectionDetailPage(InspectionDetailViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
