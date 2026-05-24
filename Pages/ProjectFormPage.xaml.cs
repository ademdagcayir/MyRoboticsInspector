using MyRoboticsInspector.ViewModels;

namespace MyRoboticsInspector.Pages;

public partial class ProjectFormPage : ContentPage
{
    public ProjectFormPage(ProjectFormViewModel vm)
    {
        InitializeComponent();
        BindingContext = vm;
    }
}
