using System.Xml.Linq;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using Xunit;

namespace CSharpToUppaal.Tests;

public class SemanticPipelineTests
{
    private const string BankCode = """
        using System;

        namespace BankSystem
        {
            public class Account
            {
                public static void Main()
                {
                    int deposits = 500;
                    int withdrawals = 200;
                    int balance = GetBalance(deposits, withdrawals);
                }

                public static int GetBalance(int deposits, int withdrawals)
                {
                    int balance = deposits - withdrawals;
                    if (balance < 0)
                    {
                        balance = 0;
                    }
                    return balance;
                }
            }
        }
        """;

    [Fact]
    public void CfgPresentationSimplifier_MergesOnlyLinearDeclarationRuns()
    {
        var entry = new CfgNode { Id = "entry", Label = "Entry", Type = NodeType.Entry };
        var first = new CfgNode { Id = "first", Label = "Declaration", Type = NodeType.Declaration, Code = "int a = 1;" };
        var second = new CfgNode { Id = "second", Label = "Declaration", Type = NodeType.Declaration, Code = "int b = 2;" };
        var condition = new CfgNode { Id = "condition", Label = "If Condition", Type = NodeType.Condition };
        var branchDeclaration = new CfgNode { Id = "branch", Label = "Declaration", Type = NodeType.Declaration, Code = "int c = 3;" };
        var exit = new CfgNode { Id = "exit", Label = "Exit", Type = NodeType.Exit };
        var source = new ControlFlowGraph
        {
            MethodName = "Example",
            EntryNodeId = entry.Id,
            ExitNodeId = exit.Id,
            Nodes = [entry, first, second, condition, branchDeclaration, exit],
            Edges =
            [
                new CfgEdge { FromNodeId = entry.Id, ToNodeId = first.Id },
                new CfgEdge { FromNodeId = first.Id, ToNodeId = second.Id },
                new CfgEdge { FromNodeId = second.Id, ToNodeId = condition.Id },
                new CfgEdge { FromNodeId = condition.Id, ToNodeId = branchDeclaration.Id, Label = "true" },
                new CfgEdge { FromNodeId = condition.Id, ToNodeId = exit.Id, Label = "false" },
                new CfgEdge { FromNodeId = branchDeclaration.Id, ToNodeId = exit.Id }
            ]
        };

        var display = CfgPresentationSimplifier.Simplify(source);

        var merged = Assert.Single(display.Nodes, node => node.Id == first.Id);
        Assert.Equal("Declarations", merged.Label);
        Assert.Contains("int a = 1;", merged.Code);
        Assert.Contains("int b = 2;", merged.Code);
        Assert.DoesNotContain(display.Nodes, node => node.Id == second.Id);
        Assert.Contains(display.Nodes, node => node.Id == branchDeclaration.Id);
        Assert.Contains(display.Edges, edge => edge.FromNodeId == first.Id && edge.ToNodeId == condition.Id);
        Assert.Equal(2, source.Nodes.Count(node => node.Type == NodeType.Declaration && node.Id is "first" or "second"));
    }

    [Fact]
    public async Task SemanticAnalyzerDiscoversFunctionsAndCallGraph()
    {
        var analyzer = new CSharpSemanticAnalyzer();
        var result = await analyzer.AnalyzeSourceCodeAsync(BankCode);

        Assert.Contains(result.Functions, f => f.Name == "Main");
        var getBalance = Assert.Single(result.Functions, f => f.Name == "GetBalance");
        var main = Assert.Single(result.Functions, f => f.Name == "Main");
        Assert.Contains(getBalance.Id, main.DirectCallIds);
    }

    [Fact]
    public async Task GeneratorSupportsNoMainRootSelectionAndQueries()
    {
        const string code = """
            public class Calculator
            {
                public int Compute(int x)
                {
                    int y = x + 1;
                    return y;
                }
            }
            """;

        var analyzer = new CSharpSemanticAnalyzer();
        var analysis = await analyzer.AnalyzeSourceCodeAsync(code);
        var compute = Assert.Single(analysis.Functions, f => f.Name == "Compute");

        var generator = new UppaalGeneratorService(semanticAnalyzer: analyzer);
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "NoMainModel",
            SourceCode = code,
            FunctionSelections =
            {
                new FunctionSelection
                {
                    FunctionId = compute.Id,
                    IsSelected = true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                }
            }
        });

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.Contains("A[] not deadlock", model.XmlContent);
        Assert.Contains("E&lt;&gt;", model.XmlContent);
        Assert.DoesNotContain("shared_result", model.XmlContent);
        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        AssertLocationNamesAreUnique(doc);
    }

    [Fact]
    public async Task UnknownFunctionReturnBecomesBoundedNondeterministicSelection()
    {
        const string code = """
            public class C
            {
                public int Compute(int x)
                {
                    int y = External(x);
                    return y;
                }
            }
            """;

        var analyzer = new CSharpSemanticAnalyzer();
        var analysis = await analyzer.AnalyzeSourceCodeAsync(code);
        var compute = Assert.Single(analysis.Functions, f => f.Name == "Compute");

        var generator = new UppaalGeneratorService(semanticAnalyzer: analyzer);
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "UnknownModel",
            SourceCode = code,
            FunctionSelections =
            {
                new FunctionSelection
                {
                    FunctionId = compute.Id,
                    IsSelected = true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                }
            }
        });

        Assert.Contains("select", model.XmlContent);
        Assert.Contains("int[-10,10]", model.XmlContent);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.Contains(model.GenerationReport.Assumptions, a => a.Category == "UnknownFunction");
    }

    [Fact]
    public async Task RequirementRulesGenerateExecutableQuery()
    {
        var service = new RequirementTranslationService();
        var interpretations = await service.InterpretAsync(
            "Compute eventually completes",
            new RequirementTranslationContext
            {
                Functions =
                {
                    new FunctionDescriptor
                    {
                        Name = "Compute",
                        DisplayName = "Calculator.Compute"
                    }
                }
            },
            new OllamaRequirementSettings { Enabled = false });

        var query = Assert.Single(interpretations.SelectMany(i => i.GeneratedQueries));
        Assert.Equal("E<> P_Calculator_Compute.Done", query.Formula);
    }

    [Fact]
    public async Task RequirementRulesTranslateNaturalLanguageComparisonsToUppaalSyntax()
    {
        var service = new RequirementTranslationService();
        var interpretations = await service.InterpretAsync(
            "deposits must be greater than 0",
            new RequirementTranslationContext
            {
                Variables = { "deposits" },
                VariableReferences = { ["deposits"] = "P_Account_Main.deposits" }
            },
            new OllamaRequirementSettings { Enabled = false });

        var query = Assert.Single(interpretations.SelectMany(interpretation => interpretation.GeneratedQueries));
        Assert.Equal("A[] P_Account_Main.deposits > 0", query.Formula);
        Assert.DoesNotContain("must", query.Formula, StringComparison.OrdinalIgnoreCase);
        Assert.True(query.IsValidated);
    }

    [Theory]
    [InlineData("the balance must remain positive", "A[] P_Account_CalculateBalance.balance > 0")]
    [InlineData("balance should stay non-negative", "A[] P_Account_CalculateBalance.balance >= 0")]
    [InlineData("balance must be negative", "A[] P_Account_CalculateBalance.balance < 0")]
    public async Task RequirementRulesTranslateQualitativeNumericRequirements(string requirement, string expectedFormula)
    {
        var service = new RequirementTranslationService();
        var interpretations = await service.InterpretAsync(
            requirement,
            new RequirementTranslationContext
            {
                Variables = { "balance" },
                VariableReferences = { ["balance"] = "P_Account_CalculateBalance.balance" }
            },
            new OllamaRequirementSettings { Enabled = false });

        var query = Assert.Single(interpretations.SelectMany(interpretation => interpretation.GeneratedQueries));
        Assert.Equal(expectedFormula, query.Formula);
    }

    [Fact]
    public async Task GeneratedModelIsReadyForExportAndUsesTemplateChannelsForCalls()
    {
        var generator = new UppaalGeneratorService();
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "BankModel",
            SourceCode = BankCode
        });

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.DoesNotContain(model.GenerationReport.Compatibility.Issues,
            i => i.Severity == UppaalCompatibilitySeverity.Error);
        Assert.All(model.GenerationReport.Layout.Templates, t => Assert.Equal(0, t.EdgeCrossingCount));

        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        AssertLocationNamesAreUnique(doc);

        Assert.Equal(3, doc.Descendants("template").Count()); // GetBalance, Main, Driver
        Assert.Contains("chan call_Account_GetBalance, done_Account_GetBalance;", model.XmlContent);
        Assert.Contains("call_Account_GetBalance!", model.XmlContent);
        Assert.Contains("done_Account_GetBalance?", model.XmlContent);
        Assert.Contains("int result_Account_GetBalance = 0;", model.XmlContent);
        Assert.DoesNotContain("int[-10,10] result_Account_GetBalance", model.XmlContent);
        Assert.DoesNotContain("fn_Account_GetBalance(", model.XmlContent);

        var mainTemplate = doc.Descendants("template")
            .Single(t => t.Element("name")?.Value == "P_Account_Main");
        Assert.Contains(mainTemplate.Descendants("label"), l =>
            l.Attribute("kind")?.Value == "synchronisation" && l.Value == "call_Account_GetBalance!");
        Assert.Contains("E&lt;&gt; Driver.DriverDone", model.XmlContent);
    }

    [Fact]
    public async Task NestedTemplateCallInValueExpressionUsesCallWaitAndTemporary()
    {
        const string code = """
            public class ExpressionCallCase
            {
                public static void Main()
                {
                    int value = AddOne(2) + 1;
                }

                public static int AddOne(int value)
                {
                    return value + 1;
                }
            }
            """;

        var model = await new UppaalGeneratorService().GenerateModelFromCodeAsync(code, "ExpressionCallModel");

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.Contains("call_ExpressionCallCase_AddOne!", model.XmlContent);
        Assert.Contains("calltmp_1", model.XmlContent);
        Assert.DoesNotContain(model.GenerationReport.Assumptions, a => a.Category == "TemplateCallExpression");
    }

    [Fact]
    public async Task RecursiveCallsAreRejectedBeforeGeneratingADeadlockingModel()
    {
        const string code = """
            public class RecursiveCase
            {
                public static void Main()
                {
                    int value = Recurse(2);
                }

                public static int Recurse(int n)
                {
                    if (n == 0) return 0;
                    return Recurse(n - 1);
                }
            }
            """;

        var model = await new UppaalGeneratorService().GenerateModelFromCodeAsync(code, "RecursiveModel");

        Assert.Equal(ModelGenerationStatus.GenerationError, model.Status);
        Assert.Contains("Recursive C# calls are not supported", model.StatusMessage);
    }

    [Fact]
    public async Task TemplateCallsInConditionsAndArgumentsUseTheChannelProtocol()
    {
        const string code = """
            public class ConditionCallCase
            {
                public static void Main()
                {
                    if (IsPositive(AddOne(0))) { }
                }

                public static int AddOne(int value) => value + 1;
                public static bool IsPositive(int value) => value > 0;
            }
            """;

        var model = await new UppaalGeneratorService().GenerateModelFromCodeAsync(code, "ConditionCallModel");

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.Contains("call_ConditionCallCase_AddOne!", model.XmlContent);
        Assert.Contains("call_ConditionCallCase_IsPositive!", model.XmlContent);
    }

    [Fact]
    public async Task NoMainDriverStartsOnlyCallGraphRoots()
    {
        const string code = """
            public class Chain
            {
                public static int Entry() => Helper();
                public static int Helper() => 1;
            }
            """;

        var model = await new UppaalGeneratorService().GenerateModelFromCodeAsync(code, "ChainModel");
        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        var driver = doc.Descendants("template").Single(t => t.Element("name")?.Value == "Driver");
        var syncs = driver.Descendants("label")
            .Where(label => label.Attribute("kind")?.Value == "synchronisation")
            .Select(label => label.Value)
            .ToList();

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.Contains("call_Chain_Entry!", syncs);
        Assert.DoesNotContain("call_Chain_Helper!", syncs);
    }

    [Fact]
    public void CompatibilityValidatorRejectsAQueryWithAnUnknownLocation()
    {
        const string xml = """
            <nta>
              <declaration></declaration>
              <template>
                <name>P</name>
                <location id="id0"><name>Start</name></location>
                <init ref="id0" />
              </template>
              <system>system P;</system>
              <queries><query><formula>E&lt;&gt; P.Missing</formula><comment></comment></query></queries>
            </nta>
            """;

        var result = new UppaalCompatibilityValidator().Validate(xml);

        Assert.False(result.IsReady);
        Assert.Contains(result.Issues, i => i.Severity == UppaalCompatibilitySeverity.Error && i.Category == "Query");
    }

    [Fact]
    public void CompatibilityValidatorReportsDuplicateLocationNames()
    {
        const string xml = """
            <nta>
              <declaration></declaration>
              <template>
                <name>P</name>
                <location id="id0"><name>A</name></location>
                <location id="id1"><name>A</name></location>
                <init ref="id0" />
              </template>
              <system>system P;</system>
              <queries><query><formula>A[] not deadlock</formula><comment></comment></query></queries>
            </nta>
            """;

        var result = new UppaalCompatibilityValidator().Validate(xml);

        Assert.False(result.IsReady);
        Assert.Contains(result.Issues, i => i.Severity == UppaalCompatibilitySeverity.Error && i.Category == "Location");
    }

    [Fact]
    public async Task LayoutPlacesLocationsTopDownAndPlacesNamesToTheRight()
    {
        var generator = new UppaalGeneratorService();
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "LayoutModel",
            SourceCode = BankCode
        });

        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        var template = doc.Descendants("template")
            .Single(t => t.Element("name")?.Value == "P_Account_Main");
        var locations = template.Elements("location").ToList();
        var entry = Assert.Single(locations, l => l.Element("name")?.Value == "Entry");
        var done = Assert.Single(locations, l => l.Element("name")?.Value == "Done");

        Assert.True(ReadInt(entry.Attribute("y")?.Value) < ReadInt(done.Attribute("y")?.Value));
        foreach (var location in locations)
        {
            var name = location.Element("name");
            Assert.NotNull(name);
            Assert.Equal(ReadInt(location.Attribute("x")?.Value) + 22, ReadInt(name!.Attribute("x")?.Value));
            Assert.Equal(ReadInt(location.Attribute("y")?.Value) - 7, ReadInt(name.Attribute("y")?.Value));
        }
    }

    private static string RemoveDoctype(string xml)
    {
        return string.Join("\n", xml.Split('\n').Where(l => !l.TrimStart().StartsWith("<!DOCTYPE", StringComparison.Ordinal)));
    }

    private static void AssertLocationNamesAreUnique(XDocument doc)
    {
        foreach (var template in doc.Descendants("template"))
        {
            var duplicateNames = template.Elements("location")
                .Select(l => l.Element("name")?.Value)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .GroupBy(n => n, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            Assert.Empty(duplicateNames);
        }
    }

    private static int ReadInt(string? value)
    {
        return int.TryParse(value, out var parsed) ? parsed : 0;
    }
}
