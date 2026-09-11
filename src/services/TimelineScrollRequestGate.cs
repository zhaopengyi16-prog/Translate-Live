namespace LiveCaptionsTranslator.services
{
    /// <summary>
    /// Gives each deferred scroll request a generation. User navigation
    /// invalidates older requests before they reach the WPF dispatcher.
    /// </summary>
    internal sealed class TimelineScrollRequestGate
    {
        private long generation;

        public long ScheduleFollow(bool isFollowingLive)
        {
            if (!isFollowingLive)
                return -1;
            return Interlocked.Increment(ref generation);
        }

        public void Invalidate()
        {
            Interlocked.Increment(ref generation);
        }

        public bool CanExecute(long request, bool isFollowingLive)
        {
            return request >= 0 &&
                   isFollowingLive &&
                   request == Volatile.Read(ref generation);
        }

        public static double CompensateOffset(
            double currentOffset,
            double expectedAnchorTop,
            double actualAnchorTop)
        {
            return Math.Max(0, currentOffset + actualAnchorTop - expectedAnchorTop);
        }
    }
}
