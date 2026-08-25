using System;
using System.Collections.Generic;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;
using Xunit;

namespace DrillFlow.Tests
{
    public sealed class CoreExpressionEngineTests
    {
        private readonly ExpressionEngine _engine = new ExpressionEngine();

        [Theory]
        [InlineData("1E-3", 0.001)]
        [InlineData("2.56E-4", 0.000256)]
        [InlineData("0.0256E-2", 0.000256)]
        [InlineData(".5 + 2 * 3", 6.5)]
        [InlineData("(1 + 2) * -3", -9)]
        public void EvaluatesScientificNumbersAndArithmetic(string expression, double expected)
        {
            Assert.Equal(expected, _engine.Evaluate(expression).AsNumber(), 12);
        }

        [Fact]
        public void EvaluatesStringsComparisonsAndBooleanPrecedence()
        {
            var result = _engine.Evaluate("('drill' + '_ok' == \"drill_ok\") && 2.4E-3 >= 1E-3 && !false");

            Assert.True(result.AsBoolean());
            Assert.Equal("line\nA", _engine.Evaluate("\"line\\n\\u0041\"").AsString());
        }

        [Fact]
        public void LogicalOperatorsShortCircuit()
        {
            Assert.False(_engine.Evaluate("false && missing.result.value").AsBoolean());
            Assert.True(_engine.Evaluate("true || missing.result.value").AsBoolean());
        }

        [Fact]
        public void ActionContextExposesParametersLatestAndAllIterationResults()
        {
            var action = new MeasureNode { Key = "measure_1" };
            var first = Result(action, 10, 0.001);
            var second = Result(action, 11, 0.002);
            var context = new ExpressionContext().SetAction(
                action,
                new Dictionary<string, object?> { ["thickness"] = 0.0024 },
                new[] { first, second });

            Assert.Equal(0.0024, _engine.Evaluate("=measure_1.parameters.thickness", context).AsNumber(), 12);
            Assert.Equal(0.002, _engine.Evaluate("measure_1.result.measured_distance", context).AsNumber(), 12);
            Assert.Equal(0.001, _engine.Evaluate("measure_1.results[0].measured_distance", context).AsNumber(), 12);
            Assert.Equal(0.002, _engine.Evaluate("measure_1.results.last.measured_distance", context).AsNumber(), 12);
            Assert.Equal(0.002, _engine.Evaluate("measure_1.last.measured_distance", context).AsNumber(), 12);
            Assert.Equal(2d, _engine.Evaluate("measure_1.results.count", context).AsNumber());
            Assert.Equal(11d, _engine.Evaluate("measure_1.result.index", context).AsNumber());
        }

        [Fact]
        public void ObjectStringIndexAccessSupportsDynamicJsonPropertyNames()
        {
            var context = new ExpressionContext().SetVariable(
                "json",
                new Dictionary<string, object?>
                {
                    ["trace-id"] = "abc-123",
                    ["items"] = new object[]
                    {
                        new Dictionary<string, object?> { ["display name"] = "first" }
                    }
                });

            Assert.Equal("abc-123", _engine.Evaluate("json['trace-id']", context).AsString());
            Assert.Equal("first", _engine.Evaluate("json.items[0][\"display name\"]", context).AsString());
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("json['missing']", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("json[0]", context));
        }

        [Fact]
        public void AnalyzeReturnsOnlyRootActionIdentifiers()
        {
            var analysis = _engine.Analyze("measure_1.result.value + move_1.parameters.move_x");

            Assert.Equal(2, analysis.RootIdentifiers.Count);
            Assert.Contains("measure_1", analysis.RootIdentifiers);
            Assert.Contains("move_1", analysis.RootIdentifiers);
            Assert.DoesNotContain("result", analysis.RootIdentifiers);
            Assert.Contains(
                analysis.FirstLevelMemberReferences,
                reference => reference.RootIdentifier == "measure_1" && reference.MemberName == "result");
            Assert.Contains(
                analysis.FirstLevelMemberReferences,
                reference => reference.RootIdentifier == "move_1" && reference.MemberName == "parameters");
            Assert.DoesNotContain(
                analysis.FirstLevelMemberReferences,
                reference => reference.MemberName == "value" || reference.MemberName == "move_x");
        }

        [Fact]
        public void LiteralBindingPreservesUnquotedEnumAndPathStrings()
        {
            Assert.Equal("relative", _engine.Evaluate(new ParameterBinding("relative")).AsString());
            Assert.Equal(@"C:\results\job.csv", _engine.EvaluateLiteral(@"C:\results\job.csv").AsString());
            Assert.Equal(0.001, _engine.Evaluate(new ParameterBinding("1E-3")).AsNumber(), 12);
        }

        [Theory]
        [InlineData("1 +")]
        [InlineData("1E")]
        [InlineData("a = 1")]
        [InlineData("System.IO.File.Delete('x')")]
        [InlineData("thing()")]
        [InlineData("[1]")]
        public void RejectsInvalidOrExecutableLookingSyntax(string expression)
        {
            Assert.Throws<ExpressionSyntaxException>(() => _engine.Evaluate(expression));
        }

        [Fact]
        public void RejectsUnknownMembersBadIndexesAndInvalidMath()
        {
            var context = new ExpressionContext()
                .SetVariable("values", new object[] { 1, 2 });

            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("missing.value", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("values.unknown", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("values[2]", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("values[0.5]", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("1 / 0", context));
            Assert.Throws<ExpressionEvaluationException>(() => _engine.Evaluate("1 + 'x'", context));
        }

        private static ActionExecutionResult Result(WorkflowNode action, int index, double distance)
        {
            return new ActionExecutionResult
            {
                ActionId = action.Id,
                ActionKey = action.Key,
                CorrelationId = index,
                Values = new Dictionary<string, object?>
                {
                    ["command"] = "return",
                    ["measured_distance"] = distance
                }
            };
        }
    }
}
