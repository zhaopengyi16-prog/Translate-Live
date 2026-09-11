using System.Windows;
using System.Windows.Media.Animation;

namespace LiveCaptionsTranslator
{
    public partial class StartupWindow : Window
    {
        private int revealRaised;

        public event EventHandler? RevealReady;

        public StartupWindow(Rect targetBounds)
        {
            InitializeComponent();

            Rect bounds = NormalizeBounds(targetBounds);
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;

            double diameter = Math.Sqrt(
                bounds.Width * bounds.Width + bounds.Height * bounds.Height) * 1.12;
            LiquidDrop.Width = diameter;
            LiquidDrop.Height = diameter;
            LiquidRipple.Width = diameter;
            LiquidRipple.Height = diameter;

            Loaded += StartupWindow_Loaded;
        }

        private static Rect NormalizeBounds(Rect targetBounds)
        {
            if (!targetBounds.IsEmpty &&
                double.IsFinite(targetBounds.Left) &&
                double.IsFinite(targetBounds.Top) &&
                double.IsFinite(targetBounds.Width) &&
                double.IsFinite(targetBounds.Height) &&
                targetBounds.Width > 0 &&
                targetBounds.Height > 0)
            {
                return targetBounds;
            }

            Rect workArea = SystemParameters.WorkArea;
            double width = Math.Min(1360, workArea.Width);
            double height = Math.Min(840, workArea.Height);
            return new Rect(
                workArea.Left + (workArea.Width - width) / 2,
                workArea.Top + (workArea.Height - height) / 2,
                width,
                height);
        }

        private void StartupWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (FindResource("StartupRevealStoryboard") is not Storyboard storyboard)
            {
                RaiseRevealReady();
                return;
            }

            storyboard = storyboard.Clone();
            storyboard.Completed += (_, _) => RaiseRevealReady();
            storyboard.Begin(this, HandoffBehavior.SnapshotAndReplace, isControllable: false);
        }

        private void RaiseRevealReady()
        {
            if (Interlocked.Exchange(ref revealRaised, 1) == 0)
                RevealReady?.Invoke(this, EventArgs.Empty);
        }
    }
}
