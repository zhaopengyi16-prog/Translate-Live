using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.services
{
    public sealed record CaptionFrame(
        TimeSpan Offset,
        string Text,
        bool IsFinal);

    public interface ICaptionSource
    {
        IAsyncEnumerable<CaptionFrame> ReadAsync(CancellationToken token = default);
    }

    public interface ITranslationService
    {
        Task<TranslationResult> TranslateAsync(
            TranslationRequest request,
            CancellationToken token = default);
    }
}
