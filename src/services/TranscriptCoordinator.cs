using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    public sealed class TranscriptCoordinator
    {
        private readonly ITranslationService translationService;
        private readonly List<TranscriptSegment> segments = [];
        private readonly object segmentsLock = new();
        private readonly SemaphoreSlim runGate = new(1, 1);
        private long nextSequence = 1;

        public event Action<TranscriptSegment>? SegmentChanged;
        public event Action<TranscriptSegment?>? DraftChanged;

        public IReadOnlyList<TranscriptSegment> Segments
        {
            get
            {
                lock (segmentsLock)
                {
                    return new ReadOnlyCollection<TranscriptSegment>(
                        segments.OrderBy(segment => segment.Sequence).ToArray());
                }
            }
        }

        public TranscriptCoordinator(ITranslationService translationService)
        {
            this.translationService = translationService ??
                throw new ArgumentNullException(nameof(translationService));
        }

        public async Task RunAsync(
            ICaptionSource source,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(source);

            await runGate.WaitAsync(token);
            try
            {
                var translationTasks = new List<Task>();
                TranscriptSegment? draft = null;
                var startedAt = DateTimeOffset.UtcNow;
                Exception? sourceFailure = null;

                try
                {
                    await foreach (var frame in source.ReadAsync(token))
                    {
                        token.ThrowIfCancellationRequested();

                        if (!frame.IsFinal)
                        {
                            draft = UpsertDraft(draft, frame, startedAt);
                            continue;
                        }

                        var committed = Commit(draft, frame, startedAt);
                        draft = null;
                        translationTasks.Add(TranslateAsync(committed, token));
                    }
                }
                catch (Exception exception)
                {
                    sourceFailure = exception;
                }

                try
                {
                    await Task.WhenAll(translationTasks);
                }
                catch when (sourceFailure != null)
                {
                    // Preserve the source failure after observing all translation tasks.
                }

                if (sourceFailure != null)
                    ExceptionDispatchInfo.Capture(sourceFailure).Throw();
            }
            finally
            {
                runGate.Release();
            }
        }

        private TranscriptSegment UpsertDraft(
            TranscriptSegment? currentDraft,
            CaptionFrame frame,
            DateTimeOffset startedAt)
        {
            TranscriptSegment draft;
            lock (segmentsLock)
            {
                if (currentDraft == null)
                {
                    draft = new TranscriptSegment(
                        Guid.NewGuid(),
                        nextSequence++,
                        0,
                        frame.Text,
                        null,
                        SegmentState.Draft,
                        startedAt + frame.Offset);
                    segments.Add(draft);
                }
                else
                {
                    draft = currentDraft with
                    {
                        Revision = currentDraft.Revision + 1,
                        SourceText = frame.Text,
                        TranslatedText = null,
                        State = SegmentState.Draft
                    };
                    ReplaceSegment(draft);
                }
            }

            DraftChanged?.Invoke(draft);
            return draft;
        }

        private TranscriptSegment Commit(
            TranscriptSegment? currentDraft,
            CaptionFrame frame,
            DateTimeOffset startedAt)
        {
            TranscriptSegment committed;
            lock (segmentsLock)
            {
                if (currentDraft == null)
                {
                    committed = new TranscriptSegment(
                        Guid.NewGuid(),
                        nextSequence++,
                        0,
                        frame.Text,
                        null,
                        SegmentState.Committed,
                        startedAt + frame.Offset);
                    segments.Add(committed);
                }
                else
                {
                    committed = currentDraft with
                    {
                        Revision = currentDraft.Revision + 1,
                        SourceText = frame.Text,
                        TranslatedText = null,
                        State = SegmentState.Committed
                    };
                    ReplaceSegment(committed);
                }
            }

            SegmentChanged?.Invoke(committed);
            if (currentDraft != null)
                DraftChanged?.Invoke(null);
            return committed;
        }

        private async Task TranslateAsync(
            TranscriptSegment committed,
            CancellationToken token)
        {
            var queued = UpdateState(committed.Id, committed.Revision, SegmentState.Queued);
            if (queued == null)
                return;

            var translating = UpdateState(queued.Id, queued.Revision, SegmentState.Translating);
            if (translating == null)
                return;

            var request = new TranslationRequest(
                translating.Id,
                translating.Sequence,
                translating.Revision,
                translating.SourceText,
                string.Empty);

            TranslationResult result;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                result = await translationService.TranslateAsync(request, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                result = new TranslationResult(
                    request.SegmentId,
                    request.Sequence,
                    request.Revision,
                    null,
                    exception.Message,
                    stopwatch.Elapsed);
            }

            ApplyResult(result);
        }

        private TranscriptSegment? UpdateState(
            Guid segmentId,
            int revision,
            SegmentState state)
        {
            TranscriptSegment? changed;
            lock (segmentsLock)
            {
                var current = segments.FirstOrDefault(segment => segment.Id == segmentId);
                if (current == null || current.Revision != revision)
                    return null;

                changed = current with { State = state };
                ReplaceSegment(changed);
            }

            SegmentChanged?.Invoke(changed);
            return changed;
        }

        private void ApplyResult(TranslationResult result)
        {
            TranscriptSegment? changed;
            lock (segmentsLock)
            {
                var current = segments.FirstOrDefault(segment => segment.Id == result.SegmentId);
                if (current == null || current.Revision != result.Revision)
                    return;

                changed = current with
                {
                    TranslatedText = result.IsSuccess ? result.TranslatedText : null,
                    State = result.IsSuccess
                        ? SegmentState.Translated
                        : SegmentState.TranslationFailed
                };
                ReplaceSegment(changed);
            }

            SegmentChanged?.Invoke(changed);
        }

        private void ReplaceSegment(TranscriptSegment replacement)
        {
            var index = segments.FindIndex(segment => segment.Id == replacement.Id);
            if (index < 0)
                throw new InvalidOperationException("Transcript segment was not found.");

            segments[index] = replacement;
        }
    }
}
