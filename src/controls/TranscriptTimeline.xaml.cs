using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using LiveCaptionsTranslator.services;
using LiveCaptionsTranslator.viewmodels;

namespace LiveCaptionsTranslator.controls
{
    public partial class TranscriptTimeline : UserControl
    {
        private readonly TimelineScrollRequestGate scrollGate = new();
        private readonly HashSet<TranscriptSegmentViewModel> observedSegments = [];
        private TranscriptSessionViewModel? viewModel;
        private ScrollViewer? scrollViewer;
        private Guid? readingAnchorId;
        private double readingAnchorTop;
        private long anchorRestoreGeneration;
        private long returnGeneration;
        private bool programmaticScroll;

        public TranscriptTimeline()
        {
            InitializeComponent();
            DataContextChanged += TranscriptTimeline_DataContextChanged;
            Loaded += (_, _) =>
            {
                scrollViewer = FindVisualChild<ScrollViewer>(TimelineList);
                ScheduleFollowToLatest(newItems: null);
            };
        }

        public void ReturnToLive()
        {
            if (viewModel == null)
                return;

            viewModel.ReturnToLive();
            scrollGate.Invalidate();
            long request = Interlocked.Increment(ref returnGeneration);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (viewModel?.FollowState != TimelineFollowState.ReturningLive ||
                    request != Volatile.Read(ref returnGeneration))
                {
                    return;
                }

                ScrollToLatestImmediate();
                Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
                {
                    if (viewModel?.FollowState == TimelineFollowState.ReturningLive &&
                        request == Volatile.Read(ref returnGeneration))
                    {
                        viewModel.CompleteReturnToLive();
                        readingAnchorId = null;
                    }
                }));
            }));
        }

        private void TranscriptTimeline_DataContextChanged(
            object sender,
            DependencyPropertyChangedEventArgs e)
        {
            if (viewModel != null)
            {
                viewModel.Segments.CollectionChanged -= Segments_CollectionChanged;
                foreach (TranscriptSegmentViewModel segment in observedSegments)
                    segment.PropertyChanged -= Segment_PropertyChanged;
                observedSegments.Clear();
            }

            viewModel = e.NewValue as TranscriptSessionViewModel;
            scrollGate.Invalidate();
            Interlocked.Increment(ref returnGeneration);
            Interlocked.Increment(ref anchorRestoreGeneration);
            readingAnchorId = null;
            if (viewModel == null)
                return;

            viewModel.Segments.CollectionChanged += Segments_CollectionChanged;
            foreach (TranscriptSegmentViewModel segment in viewModel.Segments)
                ObserveSegment(segment);
        }

        private void Segments_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                foreach (TranscriptSegmentViewModel segment in observedSegments)
                    segment.PropertyChanged -= Segment_PropertyChanged;
                observedSegments.Clear();
                if (viewModel != null)
                {
                    foreach (TranscriptSegmentViewModel segment in viewModel.Segments)
                        ObserveSegment(segment);
                }
            }
            else if (e.OldItems != null)
            {
                foreach (TranscriptSegmentViewModel segment in e.OldItems)
                {
                    segment.PropertyChanged -= Segment_PropertyChanged;
                    observedSegments.Remove(segment);
                }
            }
            if (e.NewItems != null)
            {
                foreach (TranscriptSegmentViewModel segment in e.NewItems)
                    ObserveSegment(segment);
            }

            if (viewModel?.IsFollowingLive == true)
                ScheduleFollowToLatest(e.NewItems);
            else if (viewModel?.IsBrowsingHistory == true)
                ScheduleAnchorRestore();
        }

        private void Segment_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (viewModel?.IsFollowingLive == true)
            {
                ScheduleFollowToLatest(newItems: null);
                return;
            }
            if (viewModel?.IsBrowsingHistory != true)
                return;

            CaptureReadingAnchor();
            ScheduleAnchorRestore();
        }

        private void ObserveSegment(TranscriptSegmentViewModel segment)
        {
            if (!observedSegments.Add(segment))
                return;
            segment.PropertyChanged += Segment_PropertyChanged;
        }

        private void TimelineList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
                BeginBrowsingFromUser();
        }

        private void TimelineList_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.PageUp or Key.Home)
                BeginBrowsingFromUser();
        }

        private void TimelineList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? current = e.OriginalSource as DependencyObject;
            while (current != null)
            {
                if (current is ScrollBar)
                {
                    BeginBrowsingFromUser();
                    return;
                }
                current = VisualTreeHelper.GetParent(current);
            }
        }

        private void TimelineScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            scrollViewer ??= e.OriginalSource as ScrollViewer ??
                             FindVisualChild<ScrollViewer>(TimelineList);
            if (viewModel == null || scrollViewer == null || programmaticScroll)
                return;

            double distanceFromBottom = Math.Max(
                0,
                e.ExtentHeight - e.ViewportHeight - e.VerticalOffset);
            bool directUserScroll = e.ExtentHeightChange == 0 &&
                                    e.ViewportHeightChange == 0 &&
                                    (e.VerticalChange < 0 ||
                                     (Mouse.LeftButton == MouseButtonState.Pressed &&
                                      Math.Abs(e.VerticalChange) > 0));
            if (directUserScroll && distanceFromBottom > 48)
                BeginBrowsingFromUser();

            if (viewModel.IsBrowsingHistory)
            {
                if (e.VerticalChange != 0)
                    CaptureReadingAnchor();
                if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                    ScheduleAnchorRestore();
            }
        }

        private void TimelineList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (viewModel?.IsBrowsingHistory == true)
                ScheduleAnchorRestore();
        }

        private void BeginBrowsingFromUser()
        {
            if (viewModel == null)
                return;

            scrollGate.Invalidate();
            Interlocked.Increment(ref returnGeneration);
            viewModel.BeginBrowsingHistory();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CaptureReadingAnchor));
        }

        private void ScheduleFollowToLatest(System.Collections.IList? newItems)
        {
            if (viewModel == null)
                return;

            long request = scrollGate.ScheduleFollow(viewModel.IsFollowingLive);
            if (request < 0)
                return;

            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (viewModel == null ||
                    !scrollGate.CanExecute(request, viewModel.IsFollowingLive))
                {
                    return;
                }

                ScrollToLatestImmediate();
                AnimateNewItems(newItems);
            }));
        }

        private void ScrollToLatestImmediate()
        {
            if (TimelineList.Items.Count == 0)
                return;

            programmaticScroll = true;
            try
            {
                TimelineList.ScrollIntoView(TimelineList.Items[^1]);
                TimelineList.UpdateLayout();
                scrollViewer ??= FindVisualChild<ScrollViewer>(TimelineList);
                scrollViewer?.ScrollToEnd();
            }
            finally
            {
                programmaticScroll = false;
            }
        }

        private void CaptureReadingAnchor()
        {
            if (viewModel?.IsBrowsingHistory != true)
                return;

            scrollViewer ??= FindVisualChild<ScrollViewer>(TimelineList);
            if (scrollViewer == null)
                return;

            for (int index = 0; index < TimelineList.Items.Count; index++)
            {
                if (TimelineList.ItemContainerGenerator.ContainerFromIndex(index)
                    is not ListBoxItem container)
                {
                    continue;
                }

                double top = container.TransformToAncestor(scrollViewer)
                    .Transform(new Point(0, 0)).Y;
                if (top + container.ActualHeight < 0)
                    continue;

                if (TimelineList.Items[index] is TranscriptSegmentViewModel segment)
                {
                    readingAnchorId = segment.Id;
                    readingAnchorTop = top;
                }
                return;
            }
        }

        private void ScheduleAnchorRestore()
        {
            if (viewModel?.IsBrowsingHistory != true || !readingAnchorId.HasValue)
                return;

            long request = Interlocked.Increment(ref anchorRestoreGeneration);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (viewModel?.IsBrowsingHistory != true ||
                    request != Volatile.Read(ref anchorRestoreGeneration))
                {
                    return;
                }
                RestoreReadingAnchor();
            }));
        }

        private void RestoreReadingAnchor()
        {
            if (viewModel == null || !readingAnchorId.HasValue)
                return;

            TranscriptSegmentViewModel? anchor = viewModel.Segments
                .FirstOrDefault(segment => segment.Id == readingAnchorId.Value);
            if (anchor == null)
            {
                readingAnchorId = null;
                return;
            }

            scrollViewer ??= FindVisualChild<ScrollViewer>(TimelineList);
            if (scrollViewer == null)
                return;

            programmaticScroll = true;
            try
            {
                TimelineList.ScrollIntoView(anchor);
                TimelineList.UpdateLayout();
                if (TimelineList.ItemContainerGenerator.ContainerFromItem(anchor)
                    is not ListBoxItem container)
                {
                    return;
                }

                double actualTop = container.TransformToAncestor(scrollViewer)
                    .Transform(new Point(0, 0)).Y;
                double targetOffset = TimelineScrollRequestGate.CompensateOffset(
                    scrollViewer.VerticalOffset,
                    readingAnchorTop,
                    actualTop);
                scrollViewer.ScrollToVerticalOffset(targetOffset);
                TimelineList.UpdateLayout();
            }
            finally
            {
                programmaticScroll = false;
            }
        }

        private void ReturnToLive_Click(object sender, RoutedEventArgs e)
        {
            ReturnToLive();
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
                    new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(140))
                    {
                        EasingFunction = new QuadraticEase
                        {
                            EasingMode = EasingMode.EaseOut
                        }
                    });
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent)
            where T : DependencyObject
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is T result)
                    return result;

                T? descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }
            return null;
        }
    }
}
