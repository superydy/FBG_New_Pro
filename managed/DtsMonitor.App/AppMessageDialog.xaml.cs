using System.Windows;

namespace DtsMonitor.App;

public partial class AppMessageDialog : Window
{
    public string DialogTitle { get; }
    public string MessageText { get; }

    public AppMessageDialog(string title, string message, bool showCancel = true, string confirmText = "确定", string cancelText = "取消")
    {
        InitializeComponent();
        DialogTitle = title;
        MessageText = message;
        ConfirmActionButton.Content = confirmText;
        CancelActionButton.Content = cancelText;
        CancelActionButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DataContext = this;
        Loaded += AppMessageDialog_Loaded;
    }

    public static bool ShowConfirm(Window owner, string title, string message, string confirmText = "确定", string cancelText = "取消")
    {
        var dialog = new AppMessageDialog(title, message, showCancel: true, confirmText, cancelText)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    public static void ShowInfo(Window owner, string title, string message, string confirmText = "确定")
    {
        var dialog = new AppMessageDialog(title, message, showCancel: false, confirmText)
        {
            Owner = owner
        };

        dialog.ShowDialog();
    }

    private void ConfirmActionButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void CancelActionButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void AppMessageDialog_Loaded(object sender, RoutedEventArgs e)
    {
        WindowLayoutHelper.CenterCurrentSize(this);
    }
}

