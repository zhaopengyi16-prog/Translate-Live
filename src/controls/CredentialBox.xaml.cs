using System.Windows;
using System.Windows.Controls;

namespace LiveCaptionsTranslator.controls
{
    public partial class CredentialBox : UserControl
    {
        private bool synchronizing;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value),
            typeof(string),
            typeof(CredentialBox),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnValueChanged));

        public static readonly DependencyProperty IsRevealedProperty = DependencyProperty.Register(
            nameof(IsRevealed),
            typeof(bool),
            typeof(CredentialBox),
            new PropertyMetadata(false, OnIsRevealedChanged));

        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value ?? string.Empty);
        }

        public bool IsRevealed
        {
            get => (bool)GetValue(IsRevealedProperty);
            set => SetValue(IsRevealedProperty, value);
        }

        public CredentialBox()
        {
            InitializeComponent();
            SynchronizeEditors(Value);
            UpdateRevealState();
        }

        public void FocusEditor()
        {
            if (IsRevealed)
            {
                RevealedBox.Focus();
                RevealedBox.SelectAll();
            }
            else
            {
                MaskedBox.Focus();
                MaskedBox.SelectAll();
            }
        }

        private static void OnValueChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            var control = (CredentialBox)dependencyObject;
            control.SynchronizeEditors(args.NewValue as string ?? string.Empty);
        }

        private static void OnIsRevealedChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            ((CredentialBox)dependencyObject).UpdateRevealState();
        }

        private void SynchronizeEditors(string value)
        {
            if (MaskedBox == null || RevealedBox == null || synchronizing)
                return;

            synchronizing = true;
            try
            {
                if (!string.Equals(MaskedBox.Password, value, StringComparison.Ordinal))
                    MaskedBox.Password = value;
                if (!string.Equals(RevealedBox.Text, value, StringComparison.Ordinal))
                    RevealedBox.Text = value;
            }
            finally
            {
                synchronizing = false;
            }
        }

        private void UpdateRevealState()
        {
            if (MaskedBox == null || RevealedBox == null)
                return;

            MaskedBox.Visibility = IsRevealed ? Visibility.Collapsed : Visibility.Visible;
            RevealedBox.Visibility = IsRevealed ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MaskedBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (synchronizing)
                return;
            SetCurrentValue(ValueProperty, MaskedBox.Password);
        }

        private void RevealedBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (synchronizing)
                return;
            SetCurrentValue(ValueProperty, RevealedBox.Text ?? string.Empty);
        }
    }
}
