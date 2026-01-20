// InputDialog.xaml.cs
using System.Printing;
using System.Windows;

namespace CSharpToUppaal.GUI
{
    public partial class InputDialog : Window
    {
        public string Result { get; private set; }

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            InputBox.Text = defaultValue;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Result = InputBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}