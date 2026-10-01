using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Core.Workflows;
using DrillFlow.Desktop.Services;
using DrillFlow.Desktop.ViewModels;
using Xunit;

namespace DrillFlow.Tests;

public sealed class DesktopWorkflowActionValidationStateTests
{
    [Fact]
    public void Localization_DoesNotKeepDiscardedActionTreesAlive()
    {
        var localization = new StubLocalizationService();
        var discarded = CreateDiscardedTree(localization);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.All(discarded, reference => Assert.False(reference.IsAlive));
        GC.KeepAlive(localization);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] CreateDiscardedTree(ILocalizationService localization)
    {
        var model = new ConditionalNode { Key = "conditional_1" };
        model.Branches.Add(new ConditionalBranch
        {
            Kind = ConditionalBranchKind.If,
            Condition = ParameterBinding.Literal("true"),
            Body = { new DelayNode { Key = "delay_1" } }
        });
        var action = new WorkflowActionViewModel(model, localization, new UnusedImageDecoder());
        var branch = action.Branches.Last();
        var child = Assert.Single(branch.Children);
        return new[]
        {
            new WeakReference(action),
            new WeakReference(branch),
            new WeakReference(branch.Condition),
            new WeakReference(child),
            new WeakReference(Assert.Single(child.Parameters))
        };
    }

    [Fact]
    public void ValidationErrors_AreExposedForCardStylingAndCanBeCleared()
    {
        var action = new WorkflowActionViewModel(
            new StageNode { Key = "stage_1" },
            new StubLocalizationService(),
            new UnusedImageDecoder());

        action.SetValidationErrors(new[] { " First error ", "First error", "Second error" });

        Assert.True(action.HasValidationError);
        Assert.Equal("First error" + Environment.NewLine + "Second error", action.ValidationErrorText);

        action.SetValidationErrors(Array.Empty<string>());

        Assert.False(action.HasValidationError);
        Assert.Empty(action.ValidationErrorText);
    }

    private sealed class StubLocalizationService : ILocalizationService
    {
#pragma warning disable CS0067
        public event EventHandler? LanguageChanged;
#pragma warning restore CS0067

        public string SelectedLanguage => "en-US";

        public string EffectiveLanguage => "en-US";

        public string this[string key] => key;

        public void Initialize()
        {
        }

        public void ApplyLanguage(string language, bool persist = true)
        {
        }
    }

    private sealed class UnusedImageDecoder : ILiveImageDecoder
    {
        public Task<LiveImageDecodeResult> DecodeAsync(
            byte[] encodedImage,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("This test does not decode images.");
        }
    }
}
