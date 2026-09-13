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
        private TranscriptSegment? draftSegment;
        private TranscriptSegment? liveSegment;
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
                RaiseLiveProperties();
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
                RaiseLiveProperties();
            }
        }
        public bool HasDraftTranslation => !string.IsNullOrWhiteSpace(DraftTranslation);
        public string LiveText => liveSegment?.SourceText ?? DraftText;
        public string LiveTranslation => liveSegment != null
            ? liveSegment.TranslatedText ?? string.Empty
            : DraftTranslation;
        public bool HasLiveCaption => !string.IsNullOrWhiteSpace(LiveText);
        public bool HasLiveTranslation => !string.IsNullOrWhiteSpace(LiveTranslation);
        public string LivePlaceholder => CanEndSession || IsDemoRunning
            ? "等待讲话…"
            : "开始课堂后，实时字幕将在这里显示";
        public TimelineFollowState FollowState => followState;
        public bool IsFollowingLive => followState == TimelineFollowState.FollowingLive;
        public bool IsBrowsingHistory => followState == TimelineFollowState.BrowsingHistory;
        public bool IsReturnToLiveVisible =>
            followState is TimelineFollowState.BrowsingHistory or TimelineFollowState.ReturningLive;
        public Guid? DraftSegmentId => draftSegment?.Id;
        public int DraftRevision => draftSegment?.Revision ?? -1;

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
                OnPropertyChanged(nameof(LivePlaceholder));
            }
        }

        public string DemoButtonText => IsDemoRunning ? "停止界面演示" : "播放界面演示";

        public bool CanEndSession
        {
            get => canEndSession;
            set
            {
                if (SetField(ref canEndSession, value))
                {
                    OnPropertyChanged(nameof(SaveStatusText));
                    OnPropertyChanged(nameof(LivePlaceholder));
                }
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

        public void ApplySegment(TranscriptSegment segment, bool updateLive = true)
        {
            // A provisional observation belongs to the fixed live surface. It
            // must never allocate a classroom row merely because a caller used
            // the general segment event rather than the draft event.
            if (segment.State == SegmentState.Draft)
            {
                if (updateLive)
                    SetDraft(segment);
                return;
            }

            if (updateLive)
                UpdateLiveSegment(segment);

            if (segmentsById.TryGetValue(segment.Id, out var existing))
            {
                // Segment identity also owns its recognition position and first
                // capture time. Translation completion and later revisions may
                // update content/state but must not move the sentence in history.
                existing.Apply(segment with
                {
                    Sequence = existing.Sequence,
                    CapturedAt = existing.CapturedAt
                });
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

        public bool RebindSegmentIdentity(
            Guid projectedId,
            TranscriptSegment canonical)
        {
            if (projectedId == canonical.Id)
            {
                ApplySegment(canonical, updateLive: false);
                return true;
            }

            if (!segmentsById.TryGetValue(projectedId, out var projected))
            {
                ApplySegment(canonical, updateLive: false);
                return false;
            }

            if (segmentsById.TryGetValue(canonical.Id, out var existingCanonical))
            {
                existingCanonical.Apply(canonical with
                {
                    Sequence = existingCanonical.Sequence,
                    CapturedAt = existingCanonical.CapturedAt
                });
                Segments.Remove(projected);
                segmentsById.Remove(projectedId);
                OnPropertyChanged(nameof(IsEmpty));
                return true;
            }

            int projectedIndex = Segments.IndexOf(projected);
            var rebound = new TranscriptSegmentViewModel(canonical);
            Segments[projectedIndex] = rebound;
            segmentsById.Remove(projectedId);
            segmentsById[canonical.Id] = rebound;

            int targetIndex = Segments
                .OrderBy(segment => segment.Sequence)
                .ToList()
                .IndexOf(rebound);
            if (projectedIndex != targetIndex)
                Segments.Move(projectedIndex, targetIndex);
            return true;
        }

        public void SetDraft(TranscriptSegment? draft)
        {
            if (draft == null)
            {
                draftSegment = null;
                DraftText = string.Empty;
                DraftTranslation = string.Empty;
                OnPropertyChanged(nameof(DraftSegmentId));
                OnPropertyChanged(nameof(DraftRevision));
                return;
            }

            if (!UpdateLiveSegment(draft))
                return;

            bool identityChanged = draftSegment?.Id != draft.Id;
            bool revisionChanged = draftSegment?.Revision != draft.Revision;
            draftSegment = draft;
            DraftText = draft.SourceText.Trim();
            if (identityChanged || revisionChanged || draft.TranslatedText != null)
                DraftTranslation = draft.TranslatedText?.Trim() ?? string.Empty;
            OnPropertyChanged(nameof(DraftSegmentId));
            OnPropertyChanged(nameof(DraftRevision));
        }

        public void SetDraft(string? text)
        {
            liveSegment = null;
            draftSegment = null;
            DraftText = text?.Trim() ?? string.Empty;
            if (DraftText.Length == 0)
                DraftTranslation = string.Empty;
            OnPropertyChanged(nameof(DraftSegmentId));
            OnPropertyChanged(nameof(DraftRevision));
            RaiseLiveProperties();
        }

        public void SetDraftTranslation(string? text)
        {
            DraftTranslation = text?.Trim() ?? string.Empty;
            if (liveSegment != null && draftSegment != null &&
                liveSegment.Id == draftSegment.Id &&
                liveSegment.Revision == draftSegment.Revision &&
                liveSegment.State == SegmentState.Draft)
            {
                liveSegment = liveSegment with { TranslatedText = DraftTranslation };
                RaiseLiveProperties();
            }
        }

        public bool SetDraftTranslation(
            Guid segmentId,
            int revision,
            string? text)
        {
            if (draftSegment == null || liveSegment == null ||
                draftSegment.Id != segmentId || draftSegment.Revision != revision ||
                liveSegment.Id != segmentId || liveSegment.Revision != revision ||
                liveSegment.State != SegmentState.Draft)
                return false;

            SetDraftTranslation(text);
            return true;
        }

        public bool ClearDraft(Guid segmentId, int finalRevision)
        {
            if (draftSegment?.Id != segmentId || draftSegment.Revision > finalRevision)
                return false;

            SetDraft((TranscriptSegment?)null);
            return true;
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
            draftSegment = null;
            liveSegment = null;
            OnPropertyChanged(nameof(DraftSegmentId));
            OnPropertyChanged(nameof(DraftRevision));
            followState = TimelineFollowState.FollowingLive;
            PendingSegmentCount = 0;
            RaiseFollowStateProperties();
            OnPropertyChanged(nameof(IsEmpty));
            RaiseLiveProperties();
        }

        private bool UpdateLiveSegment(TranscriptSegment segment)
        {
            if (liveSegment != null)
            {
                if (segment.SessionId != liveSegment.SessionId ||
                    segment.CaptureEpoch < liveSegment.CaptureEpoch)
                {
                    return false;
                }

                if (segment.CaptureEpoch == liveSegment.CaptureEpoch &&
                    (segment.Sequence < liveSegment.Sequence ||
                    (segment.Sequence == liveSegment.Sequence && segment.Id != liveSegment.Id) ||
                    (segment.Id == liveSegment.Id && segment.Revision < liveSegment.Revision)))
                {
                    return false;
                }

                if (segment.Id == liveSegment.Id && segment.Revision == liveSegment.Revision)
                {
                    // A delayed provisional callback cannot reopen a committed
                    // revision. A later, higher revision may legitimately do so.
                    if (segment.State == SegmentState.Draft && liveSegment.State != SegmentState.Draft)
                        return false;

                    if (segment.TranslatedText == null && liveSegment.TranslatedText != null)
                        segment = segment with { TranslatedText = liveSegment.TranslatedText };
                }
            }

            liveSegment = segment;
            RaiseLiveProperties();
            return true;
        }

        private void RaiseLiveProperties()
        {
            OnPropertyChanged(nameof(LiveText));
            OnPropertyChanged(nameof(LiveTranslation));
            OnPropertyChanged(nameof(HasLiveCaption));
            OnPropertyChanged(nameof(HasLiveTranslation));
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
