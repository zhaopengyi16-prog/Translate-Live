using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.services;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class CredentialConnectionTesterTests
    {
        [TestMethod]
        public async Task IncompleteConfigurationReturnsBeforeAnyNetworkProbe()
        {
            var tester = new CredentialConnectionTester();
            var setting = new Setting();

            CredentialConnectionResult result = await tester.TestAsync(
                "OpenAI",
                setting,
                CancellationToken.None);

            Assert.AreEqual(
                CredentialConnectionState.MissingConfiguration,
                result.State);
        }

        [TestMethod]
        public void ProviderResponseClassifierRejectsErrorsAndEmptyResponses()
        {
            Assert.IsFalse(CredentialConnectionTester.IsSuccessfulProviderResponse(null));
            Assert.IsFalse(CredentialConnectionTester.IsSuccessfulProviderResponse(string.Empty));
            Assert.IsFalse(CredentialConnectionTester.IsSuccessfulProviderResponse(
                "[ERROR] Translation Failed"));
            Assert.IsFalse(CredentialConnectionTester.IsSuccessfulProviderResponse(
                "[WARNING] fallback"));
            Assert.IsTrue(CredentialConnectionTester.IsSuccessfulProviderResponse("OK"));
        }
    }
}
