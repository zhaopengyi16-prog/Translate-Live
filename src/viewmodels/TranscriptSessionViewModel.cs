using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.viewmodels
{
    public enum TimelineFollowState
    {
        FollowingLive,
        BrowsingHistory,
        ReturningLive
    }

    public sealed class TranscriptSessionViewModel : INotifyPropertyChanged
    {
        private readonly Dictionary<Guid, TranscriptSegmentViewModel> segmentsById = [];
        private string draftText = string.Empty;
        private string draftTranslation = string.Empty;
        private TimelineFollowState followState = TimelineFollowState.FollowingLive;
        private int pendingSegmentCount;
        private string sessionStatus = "正在准备 Windows 实时字幕";
        private string captureMode = "请选择课程模式";
        private string microphoneStatus = "麦克风未启用";
        private string liveCaptionsStatus = "正在连接";
        private string elapsedText = "00:00:00";
        private bool isDemoRunning;
        private bool canEndSession;
        private bool isModeTransitioning;
        private bool isSummaryRunning;
        private string summaryStatus = "尚未生成课堂总结";
        private string summaryText = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<TranscriptSegmentViewModel> Segments { get; } = [];

        public string DraftText
        {
            get => draftText;
            private set
            {
                if (draftText == value)
                    return;
                draftText = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDraft));
            }
        }

        public bool HasDraft => !string.IsNullOrWhiteSpace(DraftText);
        public string DraftTranslation
        {
            get => draftTranslation;
            private set
            {
                if (draftTranslation == value)
                    return;
                draftTranslation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDraftTranslation));
            }
        }
        public bool HasDraftTranslation => !string.IsNullOrWhiteSpace(DraftTranslation);
        public TimelineFollowState FollowState => followState;
        public bool IsFollowingLive => followState == TimelineFollowState.FollowingLive;
        public bool IsBrowsingHistory => followState == TimelineFollowState.BrowsingHistory;
        public bool IsReturnToLiveVisible => IsBrowsingHistory && PendingSegmentCount > 0;

        public int PendingSegmentCount
        {
            get => pendingSegmentCount;
            private set
            {
                if (pendingSegmentCount == value)
                    return;
                pendingSegmentCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReturnToLiveText));
                OnPropertyChanged(nameof(IsReturnToLiveVisible));
            }
        }

        public string ReturnToLiveText => PendingSegmentCount > 0
            ? $"回到实时 · {PendingSegmentCount} 条新内容"
            : "回到实时";

        public string SessionStatus
        {
            get => sessionStatus;
            set => SetField(ref sessionStatus, value);
        }

        public string CaptureMode
        {
            get => captureMode;
            set => SetField(ref captureMode, value);
        }

        public string MicrophoneStatus
        {
            get => microphoneStatus;
            set => SetField(ref microphoneStatus, value);
        }

        public string LiveCaptionsStatus
        {
            get => liveCaptionsStatus;
            set => SetField(ref liveCaptionsStatus, value);
        }

        public string ElapsedText
        {
            get => elapsedText;
            set => SetField(ref elapsedText, value);
        }

        public bool IsDemoRunning
        {
            get => isDemoRunning;
            set
            {
                if (!SetField(ref isDemoRunning, value))
                    return;
                OnPropertyChanged(nameof(DemoButtonText));
            }
        }

        public string DemoButtonText => IsDemoRunning ? "停止界面演示" : "播放界面演示";

        public bool CanEndSession
        {
            get => canEndSession;
            set
            {
                if (SetField(ref canEndSession, value))
                    OnPropertyChanged(nameof(SaveStatusText));
            }
        }

        public string SaveStatusText => CanEndSession
            ? "课堂记录自动保存中"
            : "当前没有进行中的课堂";

        public bool IsModeTransitioning
        {
            get => isModeTransitioning;
            set
            {
                if (!SetField(ref isModeTransitioning, value))
                    return;
                OnPropertyChanged(nameof(CanChangeMode));
            }
        }

        public bool CanChangeMode => !IsModeTransitioning;

        public bool IsSummaryRunning
        {
            get => isSummaryRunning;
            set
            {
                if (!SetField(ref isSummaryRunning, value))
                    return;
                OnPropertyChanged(nameof(SummaryButtonText));
                OnPropertyChanged(nameof(CanGenerateSummary));
            }
        }

        public bool CanGenerateSummary => !IsSummaryRunning;
        public string SummaryButtonText => IsSummaryRunning ? "正在生成…" : "生成课堂总结";

        public string SummaryStatus
        {
            get => summaryStatus;
            set => SetField(ref summaryStatus, value);
        }

        public string SummaryText
        {
            get => summaryText;
            set
            {
                if (!SetField(ref summaryText, value))
                    return;
                OnPropertyChanged(nameof(HasSummary));
            }
        }

        public bool HasSummary => !string.IsNullOrWhiteSpace(SummaryText);

        public bool IsEmpty => Segments.Count == 0;

        public void ApplySegment(TranscriptSegment segment)
        {
            if (segmentsById.TryGetValue(segment.Id, out var existing))
            {
                existing.Apply(segment);
                return;
            }

            var item = new TranscriptSegmentViewModel(segment);
            int insertAt = 0;
            while (insertAt < Segments.Count && Segments[insertAt].Sequence < segment.Sequence)
                insertAt++;

            Segments.Insert(insertAt, item);
            segmentsById.Add(segment.Id, item);
            if (IsBrowsingHistory)
                PendingSegmentCount++;
            OnPropertyChanged(nameof(IsEmpty));
        }

        public bool ReplaceSegmentBySequence(long sequence, TranscriptSegment replacement)
        {
            var existing = Segments.FirstOrDefault(segment => segment.Sequence == sequence);
            if (existing == null)
                return false;

            var stableReplacement = replacement with { Id = existing.Id };
            if (!existing.Apply(stableReplacement))
                return false;

            int currentIndex = Segments.IndexOf(existing);
            int targetIndex = Segments
                .OrderBy(segment => segment.Sequence)
                .ToList()
                .IndexOf(existing);
            if (currentIndex != targetIndex)
                Segments.Move(currentIndex, targetIndex);

            return true;
        }

        public void SetDraft(TranscriptSegment? draft)
        {
            DraftText = draft?.SourceText ?? string.Empty;
        }

        public void SetDraft(string? text)
        {
            DraftText = text?.Trim() ?? string.Empty;
            if (DraftText.Length == 0)
                DraftTranslation = string.Empty;
        }

        public void SetDraftTranslation(string? text)
        {
            DraftTranslation = text?.Trim() ?? string.Empty;
        }

        public void BeginBrowsingHistory()
        {
            if (Segments.Count == 0 || IsBrowsingHistory)
                return;

            followState = TimelineFollowState.BrowsingHistory;
            RaiseFollowStateProperties();
        }

        public void ReturnToLive()
        {
            followState = TimelineFollowState.ReturningLive;
            RaiseFollowStateProperties();
        }

        public void CompleteReturnToLive()
        {
            followState = TimelineFollowState.FollowingLive;
            PendingSegmentCount = 0;
            RaiseFollowStateProperties();
        }

        public void ResetTimeline()
        {
            Segments.Clear();
            segmentsById.Clear();
            DraftText = string.Empty;
            DraftTranslation = string.Empty;
            followState = TimelineFollowState.FollowingLive;
            PendingSegmentCount = 0;
            RaiseFollowStateProperties();
            OnPropertyChanged(nameof(IsEmpty));
        }

        private void RaiseFollowStateProperties()
        {
            OnPropertyChanged(nameof(FollowState));
            OnPropertyChanged(nameof(IsFollowingLive));
            OnPropertyChanged(nameof(IsBrowsingHistory));
            OnPropertyChanged(nameof(IsReturnToLiveVisible));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
