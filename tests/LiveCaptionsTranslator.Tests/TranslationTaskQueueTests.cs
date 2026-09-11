using System.Collections.Concurrent;

using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests
{
    [TestClass]
    public sealed class TranslationTaskQueueTests
    {
        [TestMethod]
        public async Task QueueRunsOneRequestAtATimeAndCoalescesPendingRevisions()
        {
            var queue = new TranslationTaskQueue();
            var started = new ConcurrentQueue<string>();
            var releases = new ConcurrentDictionary<string, TaskCompletionSource>();
            int activeCount = 0;
            int maxActiveCount = 0;

            Func<string, Func<CancellationToken, Task<(string, bool)>>> worker = text => async token =>
            {
                started.Enqueue(text);
                int currentActive = Interlocked.Increment(ref activeCount);
                UpdateMaximum(ref maxActiveCount, currentActive);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releases[text] = release;
                try
                {
                    await release.Task.WaitAsync(token);
                    return (text, false);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCount);
                }
            };

            queue.Enqueue(worker("Hello"), "Hello", "OpenAI", null);
            queue.Enqueue(worker("Hello world"), "Hello world", "OpenAI", null);
            queue.Enqueue(worker("Hello world again"), "Hello world again", "OpenAI", null);

            await WaitUntilAsync(() => releases.ContainsKey("Hello"));
            CollectionAssert.AreEqual(new[] { "Hello" }, started.ToArray());
            releases["Hello"].SetResult();

            await WaitUntilAsync(() => releases.ContainsKey("Hello world again"));
            CollectionAssert.AreEqual(
                new[] { "Hello", "Hello world again" },
                started.ToArray());
            releases["Hello world again"].SetResult();
            await WaitUntilAsync(() => queue.Output.translatedText == "Hello world again");

            Assert.AreEqual(1, maxActiveCount);
            Assert.IsFalse(started.Contains("Hello world"));
        }

        [TestMethod]
        public async Task QueuePreservesDistinctSentencesInOrder()
        {
            var queue = new TranslationTaskQueue();
            var started = new ConcurrentQueue<string>();
            var releases = new ConcurrentDictionary<string, TaskCompletionSource>();

            Func<string, Func<CancellationToken, Task<(string, bool)>>> worker = text => async token =>
            {
                started.Enqueue(text);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                releases[text] = release;
                await release.Task.WaitAsync(token);
                return (text, false);
            };

            foreach (string text in new[] { "First sentence.", "Second sentence.", "Third sentence." })
                queue.Enqueue(worker(text), text, "Google", null);

            foreach (string text in new[] { "First sentence.", "Second sentence.", "Third sentence." })
            {
                await WaitUntilAsync(() => releases.ContainsKey(text));
                releases[text].SetResult();
            }

            await WaitUntilAsync(() => queue.Output.translatedText == "Third sentence.");
            CollectionAssert.AreEqual(
                new[] { "First sentence.", "Second sentence.", "Third sentence." },
                started.ToArray());
        }

        [TestMethod]
        public async Task QueueContinuesAfterActiveWorkerIsCanceled()
        {
            var queue = new TranslationTaskQueue();
            using var firstCancellation = new CancellationTokenSource();

            queue.Enqueue(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ("unused", false);
                },
                "First sentence.",
                "OpenAI",
                null,
                firstCancellation.Token);
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("第二句。", false)),
                "Second sentence.",
                "OpenAI",
                null);

            firstCancellation.Cancel();

            await WaitUntilAsync(() => queue.Output.translatedText == "第二句。");
            Assert.AreEqual("第二句。", queue.Output.translatedText);
        }

        [TestMethod]
        public async Task FinalSentencePreemptsItsInFlightDraft()
        {
            var queue = new TranslationTaskQueue();
            var draftStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var draftCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            queue.Enqueue(
                async token =>
                {
                    draftStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return ("unused", false);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            draftCanceled.TrySetResult();
                    }
                },
                "The final sentence",
                "OpenAI",
                null);

            await draftStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("最终译文。", true)),
                "The final sentence.",
                "OpenAI",
                null);

            await draftCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => queue.Output.translatedText == "最终译文。");
            Assert.AreEqual("最终译文。", queue.Output.translatedText);
        }

        [TestMethod]
        public async Task ShiftedFinalSuffixPreemptsItsInFlightDraft()
        {
            var queue = new TranslationTaskQueue();
            var draftStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var draftCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            queue.Enqueue(
                async token =>
                {
                    draftStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return ("unused", false);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            draftCanceled.TrySetResult();
                    }
                },
                "The queue keeps its order and waits",
                "OpenAI",
                null);

            await draftStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("最终译文。", true)),
                "queue keeps its order and waits for the final response.",
                "OpenAI",
                null);

            await draftCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => queue.Output.translatedText == "最终译文。");
            Assert.AreEqual("最终译文。", queue.Output.translatedText);
        }

        [TestMethod]
        public async Task SubstantiallyGrowingDraftPreemptsItsStaleRequest()
        {
            var queue = new TranslationTaskQueue();
            var draftStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var draftCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);

            queue.Enqueue(
                async token =>
                {
                    draftStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return ("stale", false);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            draftCanceled.TrySetResult();
                    }
                },
                "The current sentence",
                "OpenAI",
                null);

            await draftStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("latest", false)),
                "The current sentence now includes enough useful detail",
                "OpenAI",
                null);

            await draftCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => queue.Output.translatedText == "latest");
            Assert.AreEqual("latest", queue.Output.translatedText);
        }

        [TestMethod]
        public async Task CompletedStaleDraftIsNotDisplayedOrPersisted()
        {
            var releaseStale = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var persisted = new ConcurrentQueue<string>();
            var queue = new TranslationTaskQueue((result, _) =>
            {
                persisted.Enqueue(result.OriginalText);
                return Task.CompletedTask;
            });

            queue.Enqueue(
                async token =>
                {
                    await releaseStale.Task.WaitAsync(token);
                    return ("stale", false);
                },
                "A useful draft with detail",
                "OpenAI",
                1);
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("latest", false)),
                "A useful draft with details",
                "OpenAI",
                1);

            releaseStale.SetResult();

            await WaitUntilAsync(() => queue.Output.translatedText == "latest");
            await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
            CollectionAssert.AreEqual(
                new[] { "A useful draft with details" },
                persisted.ToArray());
        }

        [TestMethod]
        public async Task OrderedPersistenceDoesNotBlockTheNextNetworkRequest()
        {
            var firstPersistenceStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstPersistence = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var secondWorkerStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var persisted = new ConcurrentQueue<string>();

            var queue = new TranslationTaskQueue(async (result, token) =>
            {
                if (result.OriginalText == "First.")
                {
                    firstPersistenceStarted.SetResult();
                    await releaseFirstPersistence.Task.WaitAsync(token);
                }
                persisted.Enqueue(result.OriginalText);
            });

            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("第一。", true)),
                "First.",
                "OpenAI",
                1);
            queue.Enqueue(
                _ =>
                {
                    secondWorkerStarted.SetResult();
                    return Task.FromResult<(string, bool)>(("第二。", true));
                },
                "Second.",
                "OpenAI",
                1);

            await firstPersistenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await secondWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("第二。", queue.Output.translatedText);
            Assert.AreEqual(0, persisted.Count);

            releaseFirstPersistence.SetResult();
            await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
            CollectionAssert.AreEqual(
                new[] { "First.", "Second." },
                persisted.ToArray());
        }

        [TestMethod]
        public async Task CompletedTranslationSignalsTheDisplayWithoutPolling()
        {
            var queue = new TranslationTaskQueue();
            Task<(string translatedText, bool isChoke)> displayWait =
                queue.ReadLatestOutputAsync().AsTask();

            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("即时结果。", true)),
                "Immediate.",
                "OpenAI",
                null);

            var result = await displayWait.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("即时结果。", result.translatedText);
            Assert.IsTrue(result.isChoke);
        }

        [TestMethod]
        public async Task StreamingPartialIsPublishedBeforeTheRequestCompletes()
        {
            var releaseFinal = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var persisted = new ConcurrentQueue<string>();
            var queue = new TranslationTaskQueue((result, _) =>
            {
                persisted.Enqueue(result.TranslatedText);
                return Task.CompletedTask;
            });

            queue.Enqueue(
                async (token, publishPartial) =>
                {
                    publishPartial("首字");
                    await releaseFinal.Task.WaitAsync(token);
                    return ("完整译文", true);
                },
                "Streaming sentence.",
                "OpenAI",
                1,
                "zh-CN");

            var partial = await queue.ReadLatestOutputAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("首字", partial.translatedText);
            Assert.AreEqual(0, persisted.Count);

            releaseFinal.SetResult();
            await WaitUntilAsync(() => queue.Output.translatedText == "完整译文");
            await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
            CollectionAssert.AreEqual(new[] { "完整译文" }, persisted.ToArray());
        }

        [TestMethod]
        public async Task GrowingCompletedRevisionPreemptsTheStaleCompletedRequest()
        {
            var firstStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var firstCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var queue = new TranslationTaskQueue();

            queue.Enqueue(
                async token =>
                {
                    firstStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return ("过期", true);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            firstCanceled.TrySetResult();
                    }
                },
                "If.",
                "OpenAI",
                1,
                "zh-CN");
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("如果你的安全受到威胁。", true)),
                "If your safety is threatened.",
                "OpenAI",
                1,
                "zh-CN");

            await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() =>
                queue.Output.translatedText == "如果你的安全受到威胁。");
        }

        [TestMethod]
        public async Task PersistenceKeepsTheLanguageCapturedAtEnqueueTime()
        {
            TranslationQueueResult? persisted = null;
            var queue = new TranslationTaskQueue((result, _) =>
            {
                persisted = result;
                return Task.CompletedTask;
            });

            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("日本語", true)),
                "Japanese.",
                "OpenAI",
                4,
                "ja-JP");

            await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => persisted != null);
            Assert.AreEqual("ja-JP", persisted!.TargetLanguage);
        }

        [TestMethod]
        public async Task SameCaptionForDifferentTargetLanguagesIsNotCoalesced()
        {
            var completedLanguages = new ConcurrentQueue<string>();
            var queue = new TranslationTaskQueue((result, _) =>
            {
                completedLanguages.Enqueue(result.TargetLanguage);
                return Task.CompletedTask;
            });

            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("中文", true)),
                "Language snapshot.",
                "OpenAI",
                5,
                "zh-CN");
            queue.Enqueue(
                _ => Task.FromResult<(string, bool)>(("日本語", true)),
                "Language snapshot.",
                "OpenAI",
                5,
                "ja-JP");

            await queue.WaitForPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => completedLanguages.Count == 2);
            CollectionAssert.AreEqual(
                new[] { "zh-CN", "ja-JP" },
                completedLanguages.ToArray());
        }

        [TestMethod]
        public async Task ResetCancelsActiveAndPendingWork()
        {
            var queue = new TranslationTaskQueue();
            var activeStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var activeCanceled = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool pendingStarted = false;

            queue.Enqueue(
                async token =>
                {
                    activeStarted.SetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                        return ("unused", false);
                    }
                    finally
                    {
                        if (token.IsCancellationRequested)
                            activeCanceled.TrySetResult();
                    }
                },
                "Active draft",
                "OpenAI",
                null);
            queue.Enqueue(
                _ =>
                {
                    pendingStarted = true;
                    return Task.FromResult<(string, bool)>(("unused", false));
                },
                "Different pending sentence.",
                "OpenAI",
                null);

            await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.CancelPendingAndActive();
            await activeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(50);

            Assert.IsFalse(pendingStarted);
            Assert.AreEqual(string.Empty, queue.Output.translatedText);
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref target);
                if (current >= value)
                    return;
            } while (Interlocked.CompareExchange(ref target, value, current) != current);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!predicate())
                await Task.Delay(10, timeout.Token);
        }
    }
}
