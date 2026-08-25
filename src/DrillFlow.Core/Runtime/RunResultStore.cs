using System;
using System.Collections.Generic;
using System.Linq;

namespace DrillFlow.Core.Runtime
{
    /// <summary>
    /// Thread-safe in-memory results for the current run. Calling StartNewRun
    /// deliberately discards every result from the preceding run.
    /// </summary>
    public sealed class RunResultStore
    {
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, List<ActionExecutionResult>> _byAction =
            new Dictionary<Guid, List<ActionExecutionResult>>();

        public Guid? CurrentRunId { get; private set; }

        public DateTimeOffset? StartedAtUtc { get; private set; }

        public Guid StartNewRun(Guid? runId = null)
        {
            lock (_sync)
            {
                _byAction.Clear();
                CurrentRunId = runId ?? Guid.NewGuid();
                StartedAtUtc = DateTimeOffset.UtcNow;
                return CurrentRunId.Value;
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _byAction.Clear();
                CurrentRunId = null;
                StartedAtUtc = null;
            }
        }

        public void Record(ActionExecutionResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (result.ActionId == Guid.Empty)
            {
                throw new ArgumentException("A result must identify its action.", nameof(result));
            }

            lock (_sync)
            {
                if (CurrentRunId == null)
                {
                    throw new InvalidOperationException("StartNewRun must be called before recording results.");
                }

                if (!_byAction.TryGetValue(result.ActionId, out var results))
                {
                    results = new List<ActionExecutionResult>();
                    _byAction.Add(result.ActionId, results);
                }

                results.Add(result);
            }
        }

        public IReadOnlyList<ActionExecutionResult> GetAll(Guid actionId)
        {
            lock (_sync)
            {
                if (!_byAction.TryGetValue(actionId, out var results))
                {
                    return Array.Empty<ActionExecutionResult>();
                }

                return results.ToArray();
            }
        }

        public ActionExecutionResult? GetLatest(Guid actionId)
        {
            lock (_sync)
            {
                return _byAction.TryGetValue(actionId, out var results) && results.Count > 0
                    ? results[results.Count - 1]
                    : null;
            }
        }

        public IReadOnlyList<ActionExecutionResult> GetAllChronologically()
        {
            lock (_sync)
            {
                return _byAction.Values
                    .SelectMany(x => x)
                    .OrderBy(x => x.CompletedAtUtc)
                    .ToArray();
            }
        }
    }
}
