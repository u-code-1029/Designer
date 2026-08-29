using System;
using DrillFlow.Desktop.Models;
using DrillFlow.Desktop.Services;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopWorkflowValidationPolicyTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Constructor_UsesPersistedValidationPreference(bool persistedValue)
    {
        var policy = new WorkflowValidationPolicy(
            new StubSettingsStore(persistedValue));

        Assert.Equal(persistedValue, policy.ValidateOnEveryChange);
    }

    [Fact]
    public void Apply_RaisesChangedOnlyWhenValueChanges()
    {
        var policy = new WorkflowValidationPolicy(new StubSettingsStore(true));
        var changeCount = 0;
        policy.Changed += (_, _) => changeCount++;

        policy.Apply(true);
        policy.Apply(false);
        policy.Apply(false);

        Assert.False(policy.ValidateOnEveryChange);
        Assert.Equal(1, changeCount);
    }

    private sealed class StubSettingsStore : IUserSettingsStore
    {
        private readonly bool _persistedValue;

        public StubSettingsStore(bool persistedValue)
        {
            _persistedValue = persistedValue;
        }

        public UserPreferences Load() => new()
        {
            ValidateWorkflowOnEveryChange = _persistedValue
        };

        public void Save(UserPreferences preferences)
        {
            throw new NotSupportedException();
        }
    }
}
