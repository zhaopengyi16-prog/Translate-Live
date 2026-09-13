using System.ComponentModel;
using System.Runtime.CompilerServices;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.viewmodels
{
    public sealed class TranscriptSegmentViewModel : INotifyPropertyChanged
    {
        private TranscriptSegment segment;

        public event PropertyChangedEventHandler? PropertyChanged;

        public Guid Id => segment.Id;
        public long Sequence => segment.Sequence;
        public int Revision => segment.Revision;
        public string SourceText => segment.SourceText;
        public string TranslatedText => segment.TranslatedText ?? StatusPlaceholder;
        public DateTimeOffset CapturedAt => segment.CapturedAt;
        public TranscriptSegment Snapshot => segment;
        public SegmentState State => segment.State;
        public bool IsIncomplete => segment.IsIncomplete;
        public string CapturedTime => segment.CapturedAt.LocalDateTime.ToString("HH:mm:ss");
        public string SequenceText => $"{segment.Sequence:00}";

        public string StatusText => segment.State switch
        {
            SegmentState.Draft => "正在识别",
            SegmentState.Committed => "等待翻译",
            SegmentState.Queued => "已排队",
            SegmentState.Translating => "翻译中",
            SegmentState.Translated => "已完成",
            SegmentState.TranslationFailed => "翻译失败",
            _ => "未知"
        };

        public string StatusPlaceholder => segment.State switch
        {
            SegmentState.TranslationFailed => "翻译失败，原文已安全保留。",
            SegmentState.Translated => string.Empty,
            SegmentState.Queued or SegmentState.Translating => "正在生成译文…",
            _ => "尚无译文"
        };

        public TranscriptSegmentViewModel(TranscriptSegment segment)
        {
            this.segment = segment;
        }

        public bool Apply(TranscriptSegment replacement)
        {
            if (replacement.Id != segment.Id || replacement.Revision < segment.Revision)
                return false;

            if (replacement.Revision == segment.Revision &&
                (((segment.State is SegmentState.Translated or SegmentState.TranslationFailed) &&
                  (replacement.State is SegmentState.Committed or SegmentState.Queued or SegmentState.Translating)) ||
                 (segment.State == SegmentState.Translated && replacement.State == SegmentState.TranslationFailed)))
            {
                return false;
            }

            segment = replacement;
            OnPropertyChanged(string.Empty);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
