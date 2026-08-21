using System.Diagnostics;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using Xunit;

namespace CSharpToUppaal.Tests;

public class WorkspaceLoadingTests
{
    [Fact]
    public async Task ProjectLoader_ResolvesProjectReferenceAndGeneratesChannelCall()
    {
        var fixture = CreateFixture();
        try
        {
            await RestoreAsync(fixture.AppProject);
            var loader = new MsBuildWorkspaceLoader();
            var discovery = await loader.DiscoverSolutionAsync(fixture.SolutionPath);
            var discoveredApp = Assert.Single(discovery.Projects, project => project.Name == "Fixture.App");
            Assert.True(discoveredApp.IsExecutable);
            using var context = await loader.LoadSolutionAsync(fixture.SolutionPath);
            var app = Assert.Single(context.Projects, project => project.Name == "Fixture.App");
            Assert.False(context.RequiresRestore);

            var analyzer = new CSharpSemanticAnalyzer();
            var analysis = await analyzer.AnalyzeProjectAsync(app.RoslynProject!);
            var main = Assert.Single(analysis.Functions, function => function.Name == "Main");
            Assert.Contains(main.UnresolvedCalls, call => call.Contains("Fixture.Service.Calculator.Calculate", StringComparison.Ordinal));
            Assert.DoesNotContain(analysis.Diagnostics, diagnostic => diagnostic.Contains("CS0246", StringComparison.Ordinal));

            var model = await new UppaalGeneratorService(semanticAnalyzer: analyzer).GenerateModelFromAnalysisAsync(analysis,
                new ModelGenerationRequest
                {
                    ProjectName = "FixtureModel",
                    ExternalStubAssumptionsConfirmed = true,
                    FunctionSelections = [new FunctionSelection { FunctionId = main.Id, IsSelected = true, Mode = FunctionModelingMode.ExplicitAutomaton }]
                });

            Assert.Equal(ModelGenerationStatus.Success, model.Status);
            Assert.Contains(model.GenerationReport.Assumptions, assumption => assumption.Category == "ExternalStub");
        }
        finally
        {
            if (Directory.Exists(fixture.Root)) Directory.Delete(fixture.Root, recursive: true);
        }
    }

    [Fact]
    public async Task ExternalCallsRequireReviewBeforeTheyBecomeBoundedStubs()
    {
        const string code = "public class C { public int Compute(int x) { return External(x); } }";
        var analyzer = new CSharpSemanticAnalyzer();
        var analysis = await analyzer.AnalyzeSourceCodeAsync(code);
        var function = Assert.Single(analysis.Functions);
        var generator = new UppaalGeneratorService(semanticAnalyzer: analyzer);

        var blocked = await generator.GenerateModelFromAnalysisAsync(analysis, new ModelGenerationRequest
        {
            ProjectName = "ExternalBlocked",
            FunctionSelections = [new FunctionSelection { FunctionId = function.Id, IsSelected = true }]
        });
        Assert.Equal(ModelGenerationStatus.ValidationError, blocked.Status);
        Assert.Contains(blocked.GenerationReport.Assumptions, assumption => assumption.Category == "ExternalStubReview");

        var generated = await generator.GenerateModelFromAnalysisAsync(analysis, new ModelGenerationRequest
        {
            ProjectName = "ExternalConfirmed",
            ExternalStubAssumptionsConfirmed = true,
            FunctionSelections = [new FunctionSelection { FunctionId = function.Id, IsSelected = true }]
        });
        Assert.Equal(ModelGenerationStatus.Success, generated.Status);
        Assert.Contains(generated.GenerationReport.Assumptions, assumption => assumption.Category == "ExternalStub");
    }

    [Fact]
    public async Task SyntaxErrorsRemainGenerationBlocking()
    {
        var analyzer = new CSharpSemanticAnalyzer();
        var analysis = await analyzer.AnalyzeSourceCodeAsync("public class Broken { public void M( { }");
        var model = await new UppaalGeneratorService(semanticAnalyzer: analyzer).GenerateModelFromAnalysisAsync(analysis, new ModelGenerationRequest { ProjectName = "Broken" });

        Assert.True(analysis.HasSyntaxErrors);
        Assert.Equal(ModelGenerationStatus.ValidationError, model.Status);
        Assert.Contains(model.GenerationReport.Assumptions, assumption => assumption.Category == "Syntax");
    }

    private static async Task RestoreAsync(string projectPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"restore \"{projectPath}\" --nologo",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
    }

    private static (string Root, string AppProject, string SolutionPath) CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "CSharpToUppaalWorkspaceTests", Guid.NewGuid().ToString("N"));
        var app = Path.Combine(root, "App");
        var service = Path.Combine(root, "Service");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(service);
        File.WriteAllText(Path.Combine(service, "Fixture.Service.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(service, "Calculator.cs"), "namespace Fixture.Service; public static class Calculator { public static int Calculate(int x) => x + 1; }");
        var appProject = Path.Combine(app, "Fixture.App.csproj");
        File.WriteAllText(appProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"..\\Service\\Fixture.Service.csproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(app, "Program.cs"), "using Fixture.Service; public static class Program { public static void Main() { int answer = Calculator.Calculate(1); } }");
        var solutionPath = Path.Combine(root, "Fixture.sln");
        File.WriteAllText(solutionPath, """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Fixture.App", "App\Fixture.App.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Fixture.Service", "Service\Fixture.Service.csproj", "{22222222-2222-2222-2222-222222222222}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            	EndGlobalSection
            	GlobalSection(ProjectConfigurationPlatforms) = postSolution
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
            		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
            	EndGlobalSection
            EndGlobal
            """);
        return (root, appProject, solutionPath);
    }
}
