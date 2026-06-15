using System;
using System.Windows;
using CSharpToUppaal.GUI.Services;

namespace CSharpToUppaal.GUI
{
    public partial class SettingsWindow : Window
    {
        public GuiAppSettings Settings { get; }

        public SettingsWindow() : this(AppSettingsService.Load())
        {
        }

        public SettingsWindow(GuiAppSettings settings)
        {
            InitializeComponent();
            Settings = settings;

            OllamaEnabledCheckBox.IsChecked = Settings.OllamaEnabled;
            OllamaBaseUrlTextBox.Text = Settings.OllamaBaseUrl;
            OllamaModelTextBox.Text = Settings.OllamaModel;
            OllamaTimeoutTextBox.Text = Settings.OllamaTimeoutSeconds.ToString();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            Settings.OllamaEnabled = OllamaEnabledCheckBox.IsChecked == true;
            Settings.OllamaBaseUrl = string.IsNullOrWhiteSpace(OllamaBaseUrlTextBox.Text)
                ? "http://localhost:11434/api"
                : OllamaBaseUrlTextBox.Text.Trim();
            Settings.OllamaModel = string.IsNullOrWhiteSpace(OllamaModelTextBox.Text)
                ? "llama3:latest"
                : OllamaModelTextBox.Text.Trim();

            if (int.TryParse(OllamaTimeoutTextBox.Text, out var timeout) && timeout > 0)
                Settings.OllamaTimeoutSeconds = timeout;
            else
                Settings.OllamaTimeoutSeconds = 45;

            AppSettingsService.Save(Settings);
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
