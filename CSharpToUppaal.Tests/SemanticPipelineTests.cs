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
    public async Task GeneratedModelIsReadyForExportAndKeepsFinalIdleLoop()
    {
        var generator = new UppaalGeneratorService();
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "BankModel",
            SourceCode = BankCode
        });

        Assert.Equal(ModelGenerationStatus.Success, model.Status);
        Assert.True(model.GenerationReport.Compatibility.IsReady);
        Assert.Empty(model.GenerationReport.Compatibility.Issues.Where(i => i.Severity == UppaalCompatibilitySeverity.Error));
        Assert.All(model.GenerationReport.Layout.Templates, t => Assert.Equal(0, t.EdgeCrossingCount));

        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        AssertLocationNamesAreUnique(doc);

        var doneIds = doc.Descendants("location")
            .Where(l => l.Element("name")?.Value == "Done")
            .Select(l => l.Attribute("id")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet();

        Assert.NotEmpty(doneIds);
        Assert.Contains(doc.Descendants("transition"), t =>
        {
            var source = t.Element("source")?.Attribute("ref")?.Value;
            var target = t.Element("target")?.Attribute("ref")?.Value;
            return source != null && source == target && doneIds.Contains(source) && !t.Elements("label").Any();
        });
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
    public async Task LayoutPlacesLocationsTopDownAndCentersNames()
    {
        var generator = new UppaalGeneratorService();
        var model = await generator.GenerateModelFromRequestAsync(new ModelGenerationRequest
        {
            ProjectName = "LayoutModel",
            SourceCode = BankCode
        });

        var doc = XDocument.Parse(RemoveDoctype(model.XmlContent));
        var template = Assert.Single(doc.Descendants("template"));
        var locations = template.Elements("location").ToList();
        var entry = Assert.Single(locations, l => l.Element("name")?.Value == "Entry");
        var done = Assert.Single(locations, l => l.Element("name")?.Value == "Done");

        Assert.True(ReadInt(entry.Attribute("y")?.Value) < ReadInt(done.Attribute("y")?.Value));
        foreach (var location in locations)
        {
            var name = location.Element("name");
            Assert.NotNull(name);
            Assert.Equal(ReadInt(location.Attribute("y")?.Value) - 7, ReadInt(name!.Attribute("y")?.Value));
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
