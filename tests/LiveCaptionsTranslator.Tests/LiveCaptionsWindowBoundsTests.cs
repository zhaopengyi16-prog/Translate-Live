using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LiveCaptionsWindowBoundsTests
    {
        [TestMethod]
        public void ReadableBoundsStayInsideAStandardWorkArea()
        {
            Rect bounds = LiveCaptionsHandler.CalculateReadableBounds(
                new Rect(0, 0, 1920, 1040));

            Assert.IsGreaterThanOrEqualTo(960, bounds.Width);
            Assert.IsGreaterThanOrEqualTo(280, bounds.Height);
            Assert.IsGreaterThanOrEqualTo(0, bounds.Left);
            Assert.IsGreaterThanOrEqualTo(0, bounds.Top);
            Assert.IsLessThanOrEqualTo(1920, bounds.Right);
            Assert.IsLessThanOrEqualTo(1040, bounds.Bottom);
        }

        [TestMethod]
        public void ReadableBoundsRespectAnOffsetWorkArea()
        {
            var workArea = new Rect(-1600, 40, 1600, 860);

            Rect bounds = LiveCaptionsHandler.CalculateReadableBounds(workArea);

            Assert.IsGreaterThanOrEqualTo(workArea.Left, bounds.Left);
            Assert.IsGreaterThanOrEqualTo(workArea.Top, bounds.Top);
            Assert.IsLessThanOrEqualTo(workArea.Right, bounds.Right);
            Assert.IsLessThanOrEqualTo(workArea.Bottom, bounds.Bottom);
        }

        [TestMethod]
        public void ParkedBoundsKeepAReadableSizeOutsideTheDesktop()
        {
            Rect bounds = LiveCaptionsHandler.CalculateParkedBounds(
                new Rect(0, 0, 1920, 1040));

            Assert.IsLessThanOrEqualTo(-30000, bounds.Left);
            Assert.IsLessThanOrEqualTo(-30000, bounds.Top);
            Assert.IsGreaterThanOrEqualTo(960, bounds.Width);
            Assert.IsGreaterThanOrEqualTo(280, bounds.Height);
        }
    }
}
