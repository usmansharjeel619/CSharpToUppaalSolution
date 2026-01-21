using System.Windows;
using CSharpToUppaal.GUI.ViewModels;

namespace CSharpToUppaal.GUI
{
    public partial class MainWindow : Window
    {
        private MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _viewModel.SetCfgCanvas(CfgCanvas);
            _viewModel.SetUppaalCanvas(UppaalCanvas);
        }
    }
}