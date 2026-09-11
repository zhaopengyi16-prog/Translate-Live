namespace LiveCaptionsTranslator.services
{
    public sealed class LiveCaptionsConnectionPolicy
    {
        private int automaticConnectionClaimed;

        public bool TryClaimAutomaticConnection()
        {
            return Interlocked.CompareExchange(
                ref automaticConnectionClaimed,
                1,
                0) == 0;
        }
    }
}
