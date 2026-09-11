using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.controls
{
    public partial class TranscriptTimeline : UserControl
    {
        private TranscriptSessionViewModel? viewModel;

        public TranscriptTimeline()
        {
            InitializeComponent();
            DataContextChanged += TranscriptTimeline_DataContextChanged;
        }

        public void ReturnToLive()
        {
            if (viewModel == null)
                return;

            viewModel.ReturnToLive();
            ScrollToLatest();
            viewModel.CompleteReturnToLive();
        }

        private void TranscriptTimeline_DataContextChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (viewModel != null)
                viewModel.Segments.CollectionChanged -= Segments_CollectionChanged;

            viewModel = e.NewValue as TranscriptSessionViewModel;
            if (viewModel != null)
                viewModel.Segments.CollectionChanged += Segments_CollectionChanged;
        }

        private void Segments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (viewModel?.IsFollowingLive != true)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                ScrollToLatest();
                AnimateNewItems(e.NewItems);
            }));
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (viewModel == null || e.VerticalChange >= 0 || e.ExtentHeightChange != 0)
                return;

            double distanceFromBottom = e.ExtentHeight - e.ViewportHeight - e.VerticalOffset;
            if (distanceFromBottom > 100)
                viewModel.BeginBrowsingHistory();
        }

        private void ReturnToLive_Click(object sender, RoutedEventArgs e)
        {
            ReturnToLive();
        }

        private void ScrollToLatest()
        {
            if (TimelineList.Items.Count > 0)
                TimelineList.ScrollIntoView(TimelineList.Items[^1]);
        }

        private void AnimateNewItems(System.Collections.IList? newItems)
        {
            if (newItems == null || Translator.Setting?.Appearance.ReduceMotion == true)
                return;

            foreach (object item in newItems)
            {
                if (TimelineList.ItemContainerGenerator.ContainerFromItem(item)
                    is not ListBoxItem container)
                {
                    continue;
                }

                container.BeginAnimation(
                    OpacityProperty,
                    new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(160))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
            }
        }
    }
}
