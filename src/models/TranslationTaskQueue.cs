using System.Diagnostics;
using System.Threading.Channels;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public sealed record TranslationTaskIdentity(
        Guid SegmentId,
        long Sequence,
        int Revision,
        bool IsFinal,
        DateTimeOffset CapturedAt);

    public sealed record TranslationQueueResult(
        string OriginalText,
        string TranslatedText,
        bool IsComplete,
        string ApiName,
        long? SessionId,
        string TargetLanguage,
        TranslationTaskIdentity? Identity = null,
        bool IsPartial = false);

    public class TranslationTaskQueue
    {
        private const int DraftPreemptionGrowthBytes = 16;
        private readonly object queueLock = new();
        private readonly object persistenceLock = new();
        private readonly LinkedList<TranslationTask> pendingTasks = new();
        private readonly Func<TranslationQueueResult, CancellationToken, Task> persistenceHandler;
        private readonly Action<TranslationQueueResult>? acceptedResultHandler;
        private readonly Channel<TranslationQueueResult> outputChannel =
            Channel.CreateBounded<TranslationQueueResult>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = false,
                    FullMode = BoundedChannelFullMode.DropOldest,
                    AllowSynchronousContinuations = false
                });

        private TranslationTask? activeTask;
        private Task persistenceTail = Task.CompletedTask;
        private (string translatedText, bool isChoke) output;
        private TranslationQueueResult? latestResult;

        public (string translatedText, bool isChoke) Output
        {
            get
            {
                lock (queueLock)
                    return output;
            }
        }

        public TranslationTaskQueue(
            Func<TranslationQueueResult, CancellationToken, Task>? persistenceHandler = null,
            Action<TranslationQueueResult>? acceptedResultHandler = null)
        {
            output = (string.Empty, false);
            this.persistenceHandler = persistenceHandler ?? PersistWithTranslatorAsync;
            this.acceptedResultHandler = acceptedResultHandler;
        }

        public void Enqueue(
            Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText,
            string apiName,
            long? sessionId,
            CancellationToken parentToken,
            bool waitForPersistence = false)
        {
            Enqueue(
                worker,
                originalText,
                apiName,
                sessionId,
                string.Empty,
                parentToken,
                waitForPersistence);
        }

        public void Enqueue(
            Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText,
            string apiName,
            long? sessionId,
            string targetLanguage = "",
            CancellationToken parentToken = default,
            bool waitForPersistence = false)
        {
            Enqueue(
                (token, _) => worker(token),
                originalText,
                apiName,
                sessionId,
                targetLanguage,
                parentToken,
                waitForPersistence);
        }

        public void Enqueue(
            Func<CancellationToken, Action<string>, Task<(string, bool)>> worker,
            string originalText,
            string apiName,
            long? sessionId,
            string targetLanguage = "",
            CancellationToken parentToken = default,
            bool waitForPersistence = false)
        {
            var newTask = new TranslationTask(
                worker,
                originalText,
                apiName,
                sessionId,
                targetLanguage,
                parentToken,
                waitForPersistence,
                CancellationTokenSource.CreateLinkedTokenSource(parentToken),
                identity: null);
            Enqueue(newTask);
        }

        public void Enqueue(
            Func<CancellationToken, Action<string>, Task<(string, bool)>> worker,
            string originalText,
            string apiName,
            long? sessionId,
            string targetLanguage,
            TranslationTaskIdentity identity,
            CancellationToken parentToken = default,
            bool waitForPersistence = false)
        {
            ArgumentNullException.ThrowIfNull(identity);
            var newTask = new TranslationTask(
                worker,
                originalText,
                apiName,
                sessionId,
                targetLanguage,
                parentToken,
                waitForPersistence,
                CancellationTokenSource.CreateLinkedTokenSource(parentToken),
                identity);
            Enqueue(newTask);
        }

        private void Enqueue(TranslationTask newTask)
        {
            TranslationTask? taskToStart = null;

            lock (queueLock)
            {
                RemoveCanceledPendingTasks();
                if (activeTask == null)
                {
                    activeTask = newTask;
                    taskToStart = newTask;
                }
                else if (IsDuplicate(activeTask, newTask))
                {
                    newTask.Dispose();
                }
                else if (ShouldPreemptActiveRevision(activeTask, newTask))
                {
                    RemoveCoalesciblePendingTasks(newTask);
                    pendingTasks.AddFirst(newTask);
                    // Queue the replacement before cancellation because cancellation
                    // continuations are allowed to run synchronously.
                    activeTask.Cancel();
                }
                else if (pendingTasks.Last != null &&
                         CanCoalesce(pendingTasks.Last.Value, newTask))
                {
                    var replacedTask = pendingTasks.Last.Value;
                    pendingTasks.RemoveLast();
                    replacedTask.CancelAndDispose();
                    pendingTasks.AddLast(newTask);
                }
                else
                {
                    pendingTasks.AddLast(newTask);
                }
            }

            if (taskToStart != null)
                Start(taskToStart);
        }

        public async ValueTask<(string translatedText, bool isChoke)> ReadLatestOutputAsync(
            CancellationToken token = default)
        {
            TranslationQueueResult latest = await ReadLatestResultAsync(token);
            return (latest.TranslatedText, latest.IsComplete);
        }

        public async ValueTask<TranslationQueueResult> ReadLatestResultAsync(
            CancellationToken token = default)
        {
            TranslationQueueResult latest = await outputChannel.Reader.ReadAsync(token);
            while (outputChannel.Reader.TryRead(out TranslationQueueResult? newer))
                latest = newer;
            return latest;
        }

        public void SignalOutput()
        {
            TranslationQueueResult? result;
            lock (queueLock)
            {
                result = latestResult ?? new TranslationQueueResult(
                    string.Empty,
                    output.translatedText,
                    output.isChoke,
                    string.Empty,
                    null,
                    string.Empty);
            }
            outputChannel.Writer.TryWrite(result);
        }

        public Task WaitForPersistenceAsync()
        {
            lock (persistenceLock)
                return persistenceTail;
        }

        public void CancelPendingAndActive()
        {
            lock (queueLock)
            {
                activeTask?.Cancel();
                foreach (TranslationTask pending in pendingTasks)
                    pending.CancelAndDispose();
                pendingTasks.Clear();
                output = (string.Empty, false);
                latestResult = null;
            }

            while (outputChannel.Reader.TryRead(out _))
            {
            }
        }

        private static bool IsDuplicate(TranslationTask queuedTask, TranslationTask newTask)
        {
            if (queuedTask.Identity != null || newTask.Identity != null)
            {
                return queuedTask.Identity != null &&
                       newTask.Identity != null &&
                       queuedTask.Identity.SegmentId == newTask.Identity.SegmentId &&
                       queuedTask.Identity.Revision == newTask.Identity.Revision &&
                       queuedTask.Identity.IsFinal == newTask.Identity.IsFinal &&
                       string.Equals(
                           queuedTask.ApiName,
                           newTask.ApiName,
                           StringComparison.Ordinal) &&
                       string.Equals(
                           queuedTask.TargetLanguage,
                           newTask.TargetLanguage,
                           StringComparison.Ordinal);
            }

            return string.Equals(queuedTask.ApiName, newTask.ApiName, StringComparison.Ordinal) &&
                   queuedTask.SessionId == newTask.SessionId &&
                   string.Equals(
                       queuedTask.TargetLanguage,
                       newTask.TargetLanguage,
                       StringComparison.Ordinal) &&
                   string.Equals(
                       queuedTask.OriginalText,
                       newTask.OriginalText,
                       StringComparison.Ordinal);
        }

        private static bool ShouldPreemptActiveRevision(
            TranslationTask active,
            TranslationTask replacement)
        {
            if (!CanCoalesce(active, replacement))
            {
                return false;
            }

            bool activeIsFinal = active.Identity?.IsFinal ?? IsComplete(active.OriginalText);
            bool replacementIsFinal = replacement.Identity?.IsFinal ??
                                      IsComplete(replacement.OriginalText);
            if (activeIsFinal)
            {
                return replacementIsFinal &&
                       (active.Identity != null ||
                        CaptionRevisionPolicy.IsGrowingFinalRevision(
                            active.OriginalText,
                            replacement.OriginalText));
            }

            if (replacementIsFinal)
                return true;

            if (active.Identity != null && replacement.Identity != null)
                return replacement.Identity.Revision > active.Identity.Revision;

            int growth = System.Text.Encoding.UTF8.GetByteCount(replacement.OriginalText) -
                         System.Text.Encoding.UTF8.GetByteCount(active.OriginalText);
            return growth >= DraftPreemptionGrowthBytes;
        }

        private static bool IsComplete(string text)
        {
            return TranslationTextPolicy.IsCompleteSentence(text);
        }

        private static bool CanCoalesce(TranslationTask queuedTask, TranslationTask newTask)
        {
            if (!string.Equals(queuedTask.ApiName, newTask.ApiName, StringComparison.Ordinal) ||
                queuedTask.SessionId != newTask.SessionId ||
                !string.Equals(
                    queuedTask.TargetLanguage,
                    newTask.TargetLanguage,
                    StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(queuedTask.OriginalText) ||
                string.IsNullOrWhiteSpace(newTask.OriginalText))
            {
                return false;
            }

            if (queuedTask.Identity != null || newTask.Identity != null)
            {
                return queuedTask.Identity != null &&
                       newTask.Identity != null &&
                       queuedTask.Identity.SegmentId == newTask.Identity.SegmentId &&
                       newTask.Identity.Revision >= queuedTask.Identity.Revision;
            }

            return CaptionRevisionPolicy.IsRevision(
                queuedTask.OriginalText,
                newTask.OriginalText);
        }

        private void RemoveCoalesciblePendingTasks(TranslationTask replacement)
        {
            var node = pendingTasks.First;
            while (node != null)
            {
                var next = node.Next;
                if (CanCoalesce(node.Value, replacement))
                {
                    pendingTasks.Remove(node);
                    node.Value.CancelAndDispose();
                }
                node = next;
            }
        }

        private void Start(TranslationTask translationTask)
        {
            _ = RunAsync(translationTask);
        }

        private async Task RunAsync(TranslationTask translationTask)
        {
            try
            {
                long queueWaitMilliseconds = (long)Stopwatch
                    .GetElapsedTime(translationTask.EnqueuedTimestamp)
                    .TotalMilliseconds;
                if (queueWaitMilliseconds >= 500)
                {
                    ProductDiagnostics.WriteDuration(
                        $"translation.queue-wait.slow.{translationTask.ApiName}",
                        queueWaitMilliseconds);
                }

                (string translatedText, bool isChoke) result;
                try
                {
                    result = await translationTask.Worker(
                        translationTask.CTS.Token,
                        partial => TryPublishPartial(translationTask, partial));
                }
                catch (OperationCanceledException) when (translationTask.CTS.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    ProductDiagnostics.Write("translation.queue.worker-failed", ex);
                    result = ($"[ERROR] Translation Failed: {ex.Message}", false);
                }

                if (translationTask.CTS.IsCancellationRequested)
                    return;

                bool isComplete = translationTask.Identity?.IsFinal ?? result.isChoke;
                var completion = new TranslationQueueResult(
                    translationTask.OriginalText,
                    result.translatedText,
                    isComplete,
                    translationTask.ApiName,
                    translationTask.SessionId,
                    translationTask.TargetLanguage,
                    translationTask.Identity);
                if (!TryPublishOutput(translationTask, result))
                    return;

                try
                {
                    acceptedResultHandler?.Invoke(completion);
                }
                catch (Exception ex)
                {
                    ProductDiagnostics.Write("translation.queue.accepted-handler-failed", ex);
                }

                Task persistenceTask = QueuePersistence(
                    completion,
                    translationTask.PersistenceToken);
                if (translationTask.WaitForPersistence)
                    await persistenceTask;
            }
            finally
            {
                CompleteAndStartNext(translationTask);
            }
        }

        private void TryPublishPartial(TranslationTask translationTask, string partialText)
        {
            if (string.IsNullOrWhiteSpace(partialText))
                return;

            TranslationQueueResult partial;
            lock (queueLock)
            {
                if (translationTask.CTS.IsCancellationRequested ||
                    !ReferenceEquals(activeTask, translationTask) ||
                    pendingTasks.Any(pending => CanCoalesce(translationTask, pending)))
                {
                    return;
                }

                partial = new TranslationQueueResult(
                    translationTask.OriginalText,
                    partialText,
                    translationTask.Identity?.IsFinal ?? false,
                    translationTask.ApiName,
                    translationTask.SessionId,
                    translationTask.TargetLanguage,
                    translationTask.Identity,
                    IsPartial: true);
                output = (partialText, false);
                latestResult = partial;
            }
            outputChannel.Writer.TryWrite(partial);
        }

        private bool TryPublishOutput(
            TranslationTask translationTask,
            (string translatedText, bool isChoke) result)
        {
            TranslationQueueResult published;
            lock (queueLock)
            {
                if (translationTask.CTS.IsCancellationRequested ||
                    !ReferenceEquals(activeTask, translationTask))
                {
                    return false;
                }

                // If a newer revision of this draft is already waiting, do not
                // flash or persist the obsolete result while handing off to it.
                if (pendingTasks.Any(pending =>
                        CanCoalesce(translationTask, pending)))
                {
                    return false;
                }

                output = result;
                published = new TranslationQueueResult(
                    translationTask.OriginalText,
                    result.translatedText,
                    translationTask.Identity?.IsFinal ?? result.isChoke,
                    translationTask.ApiName,
                    translationTask.SessionId,
                    translationTask.TargetLanguage,
                    translationTask.Identity);
                latestResult = published;
            }
            outputChannel.Writer.TryWrite(published);
            return true;
        }

        private Task QueuePersistence(
            TranslationQueueResult result,
            CancellationToken token)
        {
            if (!result.SessionId.HasValue)
                return Task.CompletedTask;

            lock (persistenceLock)
            {
                persistenceTail = PersistAfterAsync(persistenceTail, result, token);
                return persistenceTail;
            }
        }

        private async Task PersistAfterAsync(
            Task previous,
            TranslationQueueResult result,
            CancellationToken token)
        {
            try
            {
                await previous;
                token.ThrowIfCancellationRequested();
                await persistenceHandler(result, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                ProductDiagnostics.Write("translation.queue.persist-failed", ex);
            }
        }

        private static async Task PersistWithTranslatorAsync(
            TranslationQueueResult result,
            CancellationToken token)
        {
            if (!result.SessionId.HasValue)
                return;

            bool isOverwrite = result.Identity == null &&
                               await Translator.IsOverwrite(
                                   result.OriginalText,
                                   result.SessionId,
                                   token);
            TranslationHistoryChange? change = await Translator.Log(
                result.OriginalText,
                result.TranslatedText,
                isOverwrite,
                result.ApiName,
                result.SessionId,
                token,
                result.TargetLanguage,
                result.Identity);
            if (result.IsComplete && change != null)
                Translator.ApplyLoggedContext(change);
        }

        private void CompleteAndStartNext(TranslationTask completedTask)
        {
            TranslationTask? nextTask = null;
            lock (queueLock)
            {
                if (ReferenceEquals(activeTask, completedTask))
                    activeTask = null;

                RemoveCanceledPendingTasks();
                if (pendingTasks.First != null)
                {
                    nextTask = pendingTasks.First.Value;
                    pendingTasks.RemoveFirst();
                    activeTask = nextTask;
                }
            }

            completedTask.Dispose();
            if (nextTask != null)
                Start(nextTask);
        }

        private void RemoveCanceledPendingTasks()
        {
            var node = pendingTasks.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.CTS.IsCancellationRequested)
                {
                    pendingTasks.Remove(node);
                    node.Value.Dispose();
                }
                node = next;
            }
        }
    }

    public class TranslationTask : IDisposable
    {
        public Func<CancellationToken, Action<string>, Task<(string, bool)>> Worker { get; }
        public string OriginalText { get; }
        public string ApiName { get; }
        public long? SessionId { get; }
        public string TargetLanguage { get; }
        public TranslationTaskIdentity? Identity { get; }
        public CancellationToken PersistenceToken { get; }
        public bool WaitForPersistence { get; }
        public CancellationTokenSource CTS { get; }

        public TranslationTask(
            Func<CancellationToken, Action<string>, Task<(string, bool)>> worker,
            string originalText,
            string apiName,
            long? sessionId,
            string targetLanguage,
            CancellationToken persistenceToken,
            bool waitForPersistence,
            CancellationTokenSource cts,
            TranslationTaskIdentity? identity)
        {
            Worker = worker;
            OriginalText = originalText;
            ApiName = apiName;
            SessionId = sessionId;
            TargetLanguage = targetLanguage;
            Identity = identity;
            PersistenceToken = persistenceToken;
            WaitForPersistence = waitForPersistence;
            CTS = cts;
            EnqueuedTimestamp = Stopwatch.GetTimestamp();
        }

        public long EnqueuedTimestamp { get; }

        public void Cancel()
        {
            if (!CTS.IsCancellationRequested)
                CTS.Cancel();
        }

        public void CancelAndDispose()
        {
            Cancel();
            CTS.Dispose();
        }

        public void Dispose()
        {
            CTS.Dispose();
        }
    }
}
