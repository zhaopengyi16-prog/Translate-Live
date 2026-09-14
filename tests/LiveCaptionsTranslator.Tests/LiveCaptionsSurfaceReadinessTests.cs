using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class LiveCaptionsSurfaceReadinessTests
    {
        [TestMethod]
        public void ReadyShellWithoutTextNodeIsAConfirmedEmptySurface()
        {
            LiveCaptionsSnapshotReadResult result =
                LiveCaptionsHandler.ClassifyCaptionSurface(
                    captionNodeReadable: false,
                    captionText: null,
                    settingsAvailable: true,
                    continueAvailable: false);

            Assert.IsTrue(result.IsReadable);
            Assert.AreEqual(string.Empty, result.Text);
            Assert.AreEqual(
                LiveCaptionsSnapshotKind.ConfirmedEmptySurface,
                result.Kind);
        }

        [TestMethod]
        public void PreparationPromptIsNotMisreportedAsAnEmptySurface()
        {
            LiveCaptionsSnapshotReadResult result =
                LiveCaptionsHandler.ClassifyCaptionSurface(
                    captionNodeReadable: false,
                    captionText: null,
                    settingsAvailable: true,
                    continueAvailable: true);

            Assert.IsFalse(result.IsReadable);
            Assert.AreEqual("caption-preparation-required", result.ErrorCode);
        }

        [TestMethod]
        public void MissingShellIsNotMisreportedAsAnEmptySurface()
        {
            LiveCaptionsSnapshotReadResult result =
                LiveCaptionsHandler.ClassifyCaptionSurface(
                    captionNodeReadable: false,
                    captionText: null,
                    settingsAvailable: false,
                    continueAvailable: false);

            Assert.IsFalse(result.IsReadable);
            Assert.AreEqual("captions-shell-unavailable", result.ErrorCode);
        }

        [TestMethod]
        public void AutomationFailureRemainsRetryableEvenWhenSettingsWasSeen()
        {
            LiveCaptionsSnapshotReadResult result =
                LiveCaptionsHandler.ClassifyCaptionSurface(
                    captionNodeReadable: false,
                    captionText: null,
                    settingsAvailable: true,
                    continueAvailable: false,
                    automationFailureObserved: true,
                    failureCode: "captions-node-stale");

            Assert.IsFalse(result.IsReadable);
            Assert.AreEqual("captions-node-stale", result.ErrorCode);
        }

        [TestMethod]
        public void ReadableTextNodeKeepsItsSnapshot()
        {
            LiveCaptionsSnapshotReadResult result =
                LiveCaptionsHandler.ClassifyCaptionSurface(
                    captionNodeReadable: true,
                    captionText: "Existing caption.",
                    settingsAvailable: false,
                    continueAvailable: true);

            Assert.IsTrue(result.IsReadable);
            Assert.AreEqual("Existing caption.", result.Text);
            Assert.AreEqual(
                LiveCaptionsSnapshotKind.CaptionTextNode,
                result.Kind);
        }
    }
}
