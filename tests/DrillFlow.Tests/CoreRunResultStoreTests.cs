using System;
using System.Collections.Generic;
using DrillFlow.Core.Runtime;
using Xunit;

namespace DrillFlow.Tests
{
    public sealed class CoreRunResultStoreTests
    {
        [Fact]
        public void PreservesEveryIterationDuringCurrentRunAndReturnsLatest()
        {
            var actionId = Guid.NewGuid();
            var store = new RunResultStore();
            store.StartNewRun(Guid.NewGuid());
            store.Record(Create(actionId, 1, 0));
            store.Record(Create(actionId, 2, 1));
            store.Record(Create(actionId, 3, 2));

            Assert.Equal(3, store.GetAll(actionId).Count);
            Assert.Equal(3, store.GetLatest(actionId)!.CorrelationId);
            Assert.Equal(new[] { 2 }, store.GetLatest(actionId)!.IterationPath);
        }

        [Fact]
        public void StartingNewRunClearsAllPriorRuntimeValues()
        {
            var actionId = Guid.NewGuid();
            var store = new RunResultStore();
            var firstRun = store.StartNewRun();
            store.Record(Create(actionId, 1, 0));

            var secondRun = store.StartNewRun();

            Assert.NotEqual(firstRun, secondRun);
            Assert.Empty(store.GetAll(actionId));
            Assert.Null(store.GetLatest(actionId));
        }

        [Fact]
        public void CannotRecordBeforeRunOrWithoutActionId()
        {
            var store = new RunResultStore();
            Assert.Throws<ArgumentException>(() => store.Record(new ActionExecutionResult()));

            var result = Create(Guid.NewGuid(), 1, 0);
            Assert.Throws<InvalidOperationException>(() => store.Record(result));
        }

        private static ActionExecutionResult Create(Guid actionId, int index, int iteration)
        {
            return new ActionExecutionResult
            {
                ActionId = actionId,
                ActionKey = "measure_1",
                CorrelationId = index,
                IterationPath = new List<int> { iteration },
                Values = new Dictionary<string, object?> { ["value"] = iteration }
            };
        }
    }
}
