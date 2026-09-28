using TaxIntelligenceStudio.Desktop.ViewModels;
using Wpf.Ui.Controls;

namespace TaxIntelligenceStudio.Desktop;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
