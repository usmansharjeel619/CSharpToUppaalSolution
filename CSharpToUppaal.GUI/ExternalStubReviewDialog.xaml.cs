using System.Windows;

namespace CSharpToUppaal.GUI;

public partial class ExternalStubReviewDialog : Window
{
    public ExternalStubReviewDialog(IReadOnlyCollection<string> calls)
    {
        InitializeComponent();
        CallCount.Text = $"External calls ({calls.Count})";
        CallDetails.Text = string.Join(Environment.NewLine + Environment.NewLine,
            calls.Select((call, index) => $"{index + 1}. {call}"));

        // Keep the dialog and its fixed action area within the available desktop.
        var workArea = SystemParameters.WorkArea;
        MaxWidth = Math.Max(1, workArea.Width - 32);
        MaxHeight = Math.Max(1, workArea.Height - 32);
        Width = Math.Min(Width, MaxWidth);
        Height = Math.Min(Height, MaxHeight);
        MinWidth = Math.Min(420, MaxWidth);
        MinHeight = Math.Min(300, MaxHeight);
        Loaded += (_, _) => CancelButton.Focus();
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
