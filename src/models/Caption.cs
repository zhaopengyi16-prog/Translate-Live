using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayNoticePrefix = " ";
        private readonly Dictionary<long, Guid> contextSegmentIds = [];
        private Guid? overlayCurrentSegmentId;
        private int overlayCurrentRevision = -1;

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;

        public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards => 
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation =>
            GetPreviousText(
                Translator.Setting.DisplaySentences,
                TextType.Translation,
                overlayCurrentSegmentId);

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType)
        {
            return GetPreviousText(count, textType, excludedSegmentId: null);
        }

        private string GetPreviousText(
            int count,
            TextType textType,
            Guid? excludedSegmentId)
        {
            if (count <= 0)
                return string.Empty;

            var values = GetPreviousContexts(count, excludedSegmentId)
                .Select(entry => textType == TextType.Caption
                    ? entry.SourceText
                    : entry.TranslatedText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (values.Length == 0)
                return string.Empty;

            string prev = string.Empty;
            foreach (string value in values)
            {
                string current = RegexPatterns.NoticePrefix().Replace(value, "");
                if (!string.IsNullOrEmpty(prev))
                {
                    if (Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                        prev += TextUtil.isCJChar(prev[^1]) ? "。" : ". ";
                    else if (!TextUtil.isCJChar(prev[^1]))
                        prev += " ";
                }
                prev += current;
            }

            if (textType == TextType.Translation)
                prev = RegexPatterns.NoticePrefix().Replace(prev, "");
            if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
            if (!string.IsNullOrEmpty(prev) && Encoding.UTF8.GetByteCount(prev[^1].ToString()) < 2)
                prev += " ";
            return prev;
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            return GetPreviousContexts(count, excludedSegmentId: null);
        }

        private IEnumerable<TranslationHistoryEntry> GetPreviousContexts(
            int count,
            Guid? excludedSegmentId)
        {
            if (count <= 0)
                return [];

            TranslationHistoryEntry[] snapshot;
            Dictionary<long, Guid> segmentIds;
            lock (Contexts)
            {
                snapshot = Contexts.ToArray();
                segmentIds = new Dictionary<long, Guid>(contextSegmentIds);
            }

            return snapshot
                .Where(entry =>
                    !excludedSegmentId.HasValue ||
                    !segmentIds.TryGetValue(entry.Id, out Guid segmentId) ||
                    segmentId != excludedSegmentId.Value)
                .Reverse().Take(count).Reverse()
                .Where(entry => entry != null && string.CompareOrdinal(entry.TranslatedText, "N/A") != 0 &&
                                !entry.TranslatedText.Contains("[ERROR]") &&
                                !entry.TranslatedText.Contains("[WARNING]"))
                .ToArray();
        }

        internal void UpsertContext(
            TranslationHistoryEntry entry,
            long? replacedEntryId = null,
            Guid? segmentId = null)
        {
            lock (Contexts)
            {
                var entries = Contexts.ToList();
                int index = replacedEntryId.HasValue
                    ? entries.FindIndex(item => item.Id == replacedEntryId.Value)
                    : -1;
                if (index < 0)
                    index = entries.FindIndex(item => item.Id == entry.Id);
                if (index < 0 && segmentId.HasValue)
                {
                    index = entries.FindIndex(item =>
                        contextSegmentIds.TryGetValue(item.Id, out Guid existingSegmentId) &&
                        existingSegmentId == segmentId.Value);
                }
                if (index < 0 && entries.Count > 0 &&
                    !segmentId.HasValue &&
                    CaptionRevisionPolicy.IsRevision(entries[^1].SourceText, entry.SourceText))
                {
                    index = entries.Count - 1;
                }

                if (index >= 0)
                {
                    contextSegmentIds.Remove(entries[index].Id);
                    entries[index] = entry;
                }
                else
                    entries.Add(entry);

                if (segmentId.HasValue)
                    contextSegmentIds[entry.Id] = segmentId.Value;

                if (entries.Count > MAX_CONTEXTS)
                {
                    int removeCount = entries.Count - MAX_CONTEXTS;
                    foreach (TranslationHistoryEntry removed in entries.Take(removeCount))
                        contextSegmentIds.Remove(removed.Id);
                    entries.RemoveRange(0, removeCount);
                }

                Contexts.Clear();
                foreach (TranslationHistoryEntry context in entries)
                    Contexts.Enqueue(context);
            }

            OnPropertyChanged(nameof(DisplayLogCards));
            OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        internal void BeginCurrentSegment(TranslationTaskIdentity identity)
        {
            if (overlayCurrentSegmentId == identity.SegmentId &&
                overlayCurrentRevision >= identity.Revision)
            {
                return;
            }

            bool identityChanged = overlayCurrentSegmentId != identity.SegmentId;
            overlayCurrentSegmentId = identity.SegmentId;
            overlayCurrentRevision = identity.Revision;
            OverlayCurrentTranslation = string.Empty;
            if (identityChanged)
                OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        internal bool TryApplyCurrentTranslation(
            TranslationTaskIdentity identity,
            string translatedText)
        {
            if (overlayCurrentSegmentId != identity.SegmentId ||
                overlayCurrentRevision != identity.Revision)
            {
                return false;
            }

            OverlayCurrentTranslation = translatedText;
            return true;
        }

        internal void ClearCurrentSegment()
        {
            overlayCurrentSegmentId = null;
            overlayCurrentRevision = -1;
            OverlayCurrentTranslation = string.Empty;
            OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        internal void ClearContextHistory()
        {
            lock (Contexts)
            {
                Contexts.Clear();
                contextSegmentIds.Clear();
            }

            OnPropertyChanged(nameof(DisplayLogCards));
            OnPropertyChanged(nameof(OverlayPreviousTranslation));
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
