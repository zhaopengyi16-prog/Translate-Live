using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    internal sealed record TranslationPersistenceRequest(
        long SessionId,
        TranslationTaskIdentity Identity,
        string SourceText,
        string TranslatedText,
        string TargetLanguage,
        string ApiName);

    internal sealed record TranslationPersistenceResult(
        TranslationHistoryEntry Entry,
        bool Applied);

    /// <summary>
    /// Persists one database row per logical caption identity. Revisions update
    /// that row in place and stale revisions are rejected at the persistence
    /// boundary even if they arrive after queue cancellation.
    /// </summary>
    internal sealed class TranslationSegmentPersistence
    {
        private sealed record PersistedState(
            int Revision,
            TranslationHistoryEntry Entry);

        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Dictionary<(long SessionId, Guid SegmentId), PersistedState>
            persistedSegments = [];
        private readonly Func<TranslationPersistenceRequest, CancellationToken,
            Task<TranslationHistoryEntry>> createEntry;
        private readonly Func<long, TranslationPersistenceRequest, CancellationToken,
            Task<TranslationHistoryEntry?>> updateEntry;

        public TranslationSegmentPersistence(
            Func<TranslationPersistenceRequest, CancellationToken,
                Task<TranslationHistoryEntry>> createEntry,
            Func<long, TranslationPersistenceRequest, CancellationToken,
                Task<TranslationHistoryEntry?>> updateEntry)
        {
            this.createEntry = createEntry ?? throw new ArgumentNullException(nameof(createEntry));
            this.updateEntry = updateEntry ?? throw new ArgumentNullException(nameof(updateEntry));
        }

        public async Task<TranslationPersistenceResult> UpsertAsync(
            TranslationPersistenceRequest request,
            CancellationToken token = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            await gate.WaitAsync(token);
            try
            {
                var key = (request.SessionId, request.Identity.SegmentId);
                if (persistedSegments.TryGetValue(key, out PersistedState? state))
                {
                    if (request.Identity.Revision < state.Revision)
                        return new TranslationPersistenceResult(state.Entry, Applied: false);

                    // Source-first admission may race a fast provider. It must
                    // never replace that same version's completed translation.
                    if (request.Identity.Revision == state.Revision &&
                        string.IsNullOrEmpty(request.TranslatedText) &&
                        !string.IsNullOrEmpty(state.Entry.TranslatedText) &&
                        string.Equals(request.SourceText, state.Entry.SourceText, StringComparison.Ordinal))
                        return new TranslationPersistenceResult(state.Entry, Applied: false);

                    bool sameRevisionAndPayload =
                        request.Identity.Revision == state.Revision &&
                        string.Equals(
                            state.Entry.SourceText,
                            request.SourceText,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            state.Entry.TranslatedText,
                            request.TranslatedText,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            state.Entry.TargetLanguage,
                            request.TargetLanguage,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            state.Entry.ApiUsed,
                            request.ApiName,
                            StringComparison.Ordinal);
                    if (sameRevisionAndPayload)
                        return new TranslationPersistenceResult(state.Entry, Applied: false);

                    TranslationHistoryEntry? updated = await updateEntry(
                        state.Entry.Id,
                        request,
                        token);
                    if (updated != null)
                    {
                        persistedSegments[key] = new PersistedState(
                            Math.Max(state.Revision, request.Identity.Revision),
                            updated);
                        return new TranslationPersistenceResult(updated, Applied: true);
                    }
                }

                TranslationHistoryEntry created = await createEntry(request, token);
                persistedSegments[key] = new PersistedState(
                    request.Identity.Revision,
                    created);
                return new TranslationPersistenceResult(created, Applied: true);
            }
            finally
            {
                gate.Release();
            }
        }

        public async Task ClearAsync(CancellationToken token = default)
        {
            await gate.WaitAsync(token);
            try
            {
                persistedSegments.Clear();
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
