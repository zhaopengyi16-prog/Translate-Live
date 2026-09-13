namespace LiveCaptionsTranslator.services
{
    /// <summary>
    /// Production entry point for converting one complete Live Captions
    /// accessibility snapshot into canonical draft and finalized identities.
    /// Tests use this same entry point so admission behavior cannot drift from
    /// the Translator capture loop.
    /// </summary>
    internal sealed class LiveCaptionIdentityResolver
    {
        private readonly LiveCaptionSegmenter segmenter;
        private readonly FinalCaptionAdmissionGate admissionGate = new();

        public LiveCaptionIdentityResolver(bool splitLongDrafts = true)
        {
            segmenter = new LiveCaptionSegmenter(splitLongDrafts);
        }

        public LiveCaptionUpdate Process(string? rawText)
        {
            return Process(rawText, DateTimeOffset.UtcNow);
        }

        internal LiveCaptionUpdate Process(
            string? rawText,
            DateTimeOffset observedAt)
        {
            LiveCaptionUpdate update = segmenter.Process(rawText, observedAt);
            LiveCaptionSegment? draft = update.DraftSegment;
            if (draft != null)
                draft = admissionGate.ResolveDraft(draft, observedAt);

            var admitted = new List<LiveCaptionSegment>(
                update.FinalizedSegments.Count);
            var canonicalMappings = new Dictionary<Guid, LiveCaptionSegment>();
            foreach (LiveCaptionSegment candidate in update.FinalizedSegments)
            {
                FinalCaptionAdmissionDecision decision =
                    admissionGate.Evaluate(candidate, observedAt);
                if (decision.CanonicalSegment.Id != candidate.Id ||
                    !decision.IsAdmitted)
                {
                    canonicalMappings[candidate.Id] = decision.CanonicalSegment;
                }
                if (decision.IsAdmitted)
                    admitted.Add(decision.CanonicalSegment);
            }

            LiveCaptionSegment? current = update.CurrentSegment;
            if (draft != null && current?.Id == update.DraftSegment?.Id)
                current = draft;
            if (current != null &&
                canonicalMappings.TryGetValue(
                    current.Id,
                    out LiveCaptionSegment? canonical))
            {
                current = canonical;
            }

            return update with
            {
                FinalizedSegments = admitted,
                DraftSegment = draft,
                CurrentSegment = current,
                CurrentText = current?.Text ?? update.CurrentText
            };
        }

        public void Reset()
        {
            segmenter.Reset();
            admissionGate.Reset();
        }

        public void StartFromCurrentSnapshot(string? rawText)
        {
            segmenter.StartFromCurrentSnapshot(rawText);
            admissionGate.Reset();
        }

        internal void StartFromCurrentSnapshot(
            string? rawText,
            DateTimeOffset observedAt)
        {
            segmenter.StartFromCurrentSnapshot(rawText, observedAt);
            admissionGate.Reset();
        }

        internal int RecentLedgerCount => segmenter.RecentLedgerCount;
        internal int RecentSnapshotCount => segmenter.RecentSnapshotCount;
        internal int PendingCandidateCount => segmenter.PendingCandidateCount;
    }
}
