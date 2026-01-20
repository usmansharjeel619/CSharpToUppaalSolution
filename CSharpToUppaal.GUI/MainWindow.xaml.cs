using System.Windows;
using CSharpToUppaal.GUI.ViewModels;

namespace CSharpToUppaal.GUI
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }
    }
}