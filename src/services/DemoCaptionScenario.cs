namespace LiveCaptionsTranslator.services
{
    public static class DemoCaptionScenario
    {
        public static ICaptionSource Create(bool honorTiming = true)
        {
            return new ReplayCaptionSource(
            [
                new CaptionFrame(TimeSpan.Zero, "Welcome to", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(350), "Welcome to Translate Live.", true),

                new CaptionFrame(TimeSpan.FromMilliseconds(900), "Today we will learn", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(1250), "Today we will learn how state machines", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(1600),
                    "Today we will learn how state machines work.", true),

                new CaptionFrame(TimeSpan.FromMilliseconds(2150), "A draft caption can change", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(2500),
                    "A draft caption can change while the speaker is talking.", true),

                new CaptionFrame(TimeSpan.FromMilliseconds(3050), "Once a sentence is final", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(3400),
                    "Once a sentence is final, it is queued for translation.", true),

                new CaptionFrame(TimeSpan.FromMilliseconds(3950), "Each translation is matched", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(4300),
                    "Each translation is matched to its original sentence.", true),

                new CaptionFrame(TimeSpan.FromMilliseconds(4850), "The timeline stays ordered", false),
                new CaptionFrame(TimeSpan.FromMilliseconds(5200),
                    "The timeline stays ordered even when results arrive out of order.", true)
            ], honorTiming);
        }
    }
}
