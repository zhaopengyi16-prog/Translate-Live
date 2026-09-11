using System.Windows;
using System.Diagnostics;
using System.Windows.Navigation;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class WelcomeWindow : FluentWindow
    {
        public WelcomeWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }
    }
}
