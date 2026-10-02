using System.Windows;
using System.Windows.Input;

namespace RadCord.Views;

public partial class InputDialog : Window
{
    public string Result { get; private set; } = "";

    public InputDialog(string prompt, string title = "RadCord", string defaultValue = "")
    {
        InitializeComponent();
        Title            = title;
        PromptText.Text  = prompt;
        InputBox.Text    = defaultValue;
        InputBox.SelectAll();
        Loaded += (_, _) => InputBox.Focus();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Result       = InputBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => DialogResult = false;

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Ok_Click(sender, e);
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
