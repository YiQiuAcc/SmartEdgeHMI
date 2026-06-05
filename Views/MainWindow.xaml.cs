using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SmartEdgeHMI.ViewModels;

namespace SmartEdgeHMI.Views;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        DataContext = serviceProvider.GetService<MainViewModel>();
    }
}
