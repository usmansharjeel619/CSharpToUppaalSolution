using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Services;
using CSharpToUppaal.Backend.Verification;
using Xunit;

namespace CSharpToUppaal.Tests;

public class RequirementsAndDomainsTests
{
    public const string Bank = """
        class Account {
          static void Main() { int deposits = 500; int withdrawals = 200; int balance = GetBalance(deposits, withdrawals); int updated = ProcessTransaction(balance, 50, 1); int total = CalculateInterest(updated, 5, 3); }
          static int GetBalance(int deposits, int withdrawals) { int balance = deposits - withdrawals; if (balance < 0) balance = 0; return balance; }
          static int ProcessTransaction(int balance, int amount, int txType) { int result = balance; if (txType == 1) result = balance + amount; else if (txType == 2 && balance >= amount) result = balance - amount; return result; }
          static int CalculateInterest(int principal, int rate, int years) { int amount = principal; for (int i = 0; i < years; i++) { int interest = amount * rate / 100; amount = amount + interest; } return amount; }
        }
        """;
    private static async Task<UppaalModel> Generate(string code, ModelGenerationRequest? request = null)
    {
        request ??= new(); request.SourceCode = code;
        var model = await new UppaalGeneratorService().GenerateModelFromRequestAsync(request);
        Assert.True(model.Status == ModelGenerationStatus.Success, model.StatusMessage + "\n" + string.Join("\n", model.GenerationReport.Assumptions.Select(a => a.Message)));
        return model;
    }

    [Fact]
    public async Task BankRangesIncludeCallsLoopExitAndReturnValues()
    {
        var model = await Generate(Bank);
        var domains = model.GenerationReport.Domains;
        Assert.Contains(domains, d => d.Name == "Account.GetBalance.deposits" && d.SourceMin == 500 && d.SourceMax == 500);
        Assert.Contains(domains, d => d.Name == "Account.CalculateInterest.ret" && d.SourceMin == 404 && d.SourceMax == 404);
        Assert.Contains(domains, d => d.Name == "Account.CalculateInterest.i" && d.SourceMin == 0 && d.SourceMax == 3);
        Assert.All(domains, d => Assert.NotEmpty(d.SymbolId));
        Assert.Contains(model.GenerationReport.Queries, q => q.Category == RequirementKind.Safety && q.Evidence.Length > 0);
        Assert.Contains(model.GenerationReport.Queries, q => q.Category == RequirementKind.Sanity);
        Assert.DoesNotContain(model.GenerationReport.Queries, q => q.Name == "Reach_Driver_AllDone");
        Assert.All(model.GenerationReport.Queries, q => Assert.True(q.IsValidated, q.ValidationDiagnostics));
        Assert.All(model.GenerationReport.Queries, q => Assert.Equal("Not run", q.VerificationStatus));
    }
    [Fact]
    public async Task AssignmentAndBranchRangesDoNotUseComparisonAsGlobalBound()
    {
        var m = await Generate("class C { static void Main() { int x = -20; if(x < 0) x = 100; x = x + 2; } }");
        var x = Assert.Single(m.GenerationReport.Domains, d => d.Name == "C.Main.x");
        Assert.Equal(-20, x.SourceMin); Assert.Equal(102, x.SourceMax);
    }
    [Fact]
    public async Task UnknownInputsAndOverflowStayExplicitlyAssumed()
    {
        var m = await Generate("class C { static int F(int x) { if(x < 5) x = 10; return x; } }");
        Assert.Contains(m.GenerationReport.Domains, d => d.Name == "C.F.x" && d.SourceMin == null && d.InferenceStatus == "Assumed");
        m = await Generate("class C { static void Main() { int x = 2147483647; x = x + 1; } }");
        Assert.Contains(m.GenerationReport.Domains, d => d.Name == "C.Main.x" && d.SourceMin == null);
    }
    [Fact]
    public async Task ShadowedLocalsHaveDifferentIdsAndEmittedNames()
    {
        var m = await Generate("class C { static void Main() { { int x = 1; x++; } { int x = 100; x++; } } }");
        var locals = m.GenerationReport.Domains.Where(d => d.Name == "C.Main.x").ToList();
        Assert.Equal(2, locals.Count);
        Assert.Equal(2, locals.Select(d => d.SymbolId).Distinct().Count());
        Assert.Equal(2, locals.Select(d => d.EmittedName).Distinct().Count());
        Assert.Contains(locals, d => d.SourceMax == 2);
        Assert.Contains(locals, d => d.SourceMax == 101);
    }
    [Fact]
    public async Task SingleEntryScopesModelAndRequirementContext()
    {
        const string code = "class C { static void Main() { F(); G(); } static int F() { return H(); } static int H() { return 7; } static int G() { return 9; } }";
        var analysis = await new CSharpSemanticAnalyzer().AnalyzeSourceCodeAsync(code);
        var f = analysis.Functions.Single(f => f.Name == "F");
        var g = analysis.Functions.Single(f => f.Name == "G");
        var request = new ModelGenerationRequest { SingleFunctionMode = true, SelectedEntryFunctionId = f.Id,
            Requirements = { new RequirementEntry { Text = "G eventually completes", FunctionId = g.Id } } };
        var m = await Generate(code, request);
        Assert.Equal(new[] { "F", "H" }, m.GenerationReport.IncludedFunctions.Select(f => f.Name).Order());
        Assert.DoesNotContain(m.GenerationReport.Domains, d => d.Name.Contains(".G."));
        Assert.StartsWith("Inactive", Assert.Single(m.GenerationReport.Interpretations).Status);
        Assert.DoesNotContain(QueryValidationService.FromModel(m).SymbolTypes.Keys, k => k.Contains("P_C_G"));
    }
    [Theory]
    [InlineData("A[] P_C_Main.x > 0 and invented == 1")]
    [InlineData("E<> P_C_Main.Nonexistent")]
    [InlineData("A[] P_C_Main.flag > 0")]
    [InlineData("A[] P_C_Main.x = 4")]
    [InlineData("A[] P_C_Main.x == true")]
    [InlineData("A[] P_C_Main.x > 0; evil()")]
    public async Task ValidatorRejectsUnknownSymbolsTypesAndSyntax(string query)
    {
        var m = await Generate("class C { static void Main() { int x = 1; bool flag = true; } }");
        Assert.False(QueryValidationService.Validate(query, QueryValidationService.FromModel(m), out var reason));
        Assert.NotEmpty(reason);
    }
    [Fact]
    public async Task EveryGuidedPatternWorksOffline()
    {
        var m = await Generate("class C { static void Main() { int x = 1; } }");
        var context = QueryValidationService.FromModel(m);
        var function = context.Functions.Single();
        var entries = new List<RequirementEntry>
        {
            new() { GuidedKind = RequirementKind.Safety, Variable = "P_C_Main.x", Operator = ">=", Value = "0", Text = "Invariant" },
            new() { GuidedKind = RequirementKind.Safety, Variable = "P_C_Main.x", Operator = "<", Value = "0", Forbidden = true, Text = "Forbidden" },
            new() { GuidedKind = RequirementKind.Reachability, Variable = "P_C_Main.Done", Text = "Possible" },
            new() { GuidedKind = RequirementKind.Liveness, Variable = "P_C_Main.Done", Text = "Guaranteed" },
            new() { GuidedKind = RequirementKind.LeadsTo, Variable = "P_C_Main.Entry", Target = "P_C_Main.Done", Text = "Response" },
            new() { GuidedKind = RequirementKind.DeadlockFreedom, Text = "Health" },
            new() { GuidedKind = RequirementKind.Safety, Variable = "P_C_Main.x", Operator = ">", Value = "0", FunctionId = function.Id, Scope = "On completion", Text = "Postcondition" }
        };
        var results = await new RequirementTranslationService().InterpretEntriesAsync(entries, context, new());
        Assert.All(results, r => Assert.Single(r.GeneratedQueries));
        Assert.Equal(7, results.SelectMany(r => r.GeneratedQueries).Select(q => q.Name).Distinct().Count());
        Assert.Contains("not P_C_Main.Done", results.Last().GeneratedQueries.Single().Formula);
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => response(request);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OllamaIsolatedRequestsEnforceIdentityAndFallback(bool wrongId)
    {
        var ids = new List<string>();
        var http = new HttpClient(new Handler(async request =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Contains("id", body["format"]!["properties"]!["requirements"]!["items"]!["required"]!.ToJsonString());
            var prompt = body["messages"]![1]!["content"]!.GetValue<string>();
            var id = System.Text.RegularExpressions.Regex.Match(prompt, @"id=([^:]+):").Groups[1].Value;
            ids.Add(id);
            var content = new JsonObject { ["requirements"] = new JsonArray(new JsonObject { ["id"] = wrongId ? "bad" : id, ["text"] = "safe", ["kind"] = "Safety", ["formula"] = "A[] x > 0", ["confidence"] = 1 }) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["message"] = new JsonObject { ["content"] = content.ToJsonString() } }.ToJsonString(), Encoding.UTF8, "application/json") };
        }));
        var entries = new[] { new RequirementEntry { Text = "x must be positive" }, new RequirementEntry { Text = "x must be positive" } };
        var results = await new RequirementTranslationService(http).InterpretEntriesAsync(entries, new RequirementTranslationContext { Variables = { "x" } }, new() { Enabled = true });
        Assert.Equal(entries.Select(e => e.Id), ids);
        Assert.Equal(entries.Select(e => e.Id), results.Select(r => r.RequirementId));
        Assert.All(results, r => Assert.Equal(wrongId ? "rules" : "ollama", Assert.Single(r.GeneratedQueries).Source));
    }
    [Fact]
    public async Task RepeatedGenerationDoesNotDuplicateQueriesAndRejectsManualUnknowns()
    {
        var request = new ModelGenerationRequest { Requirements = { new() { Text = "The model must be deadlock free." } } };
        var first = await Generate(Bank, request);
        request.UserQueries = first.GenerationReport.Queries;
        var second = await Generate(Bank, request);
        Assert.Equal(first.GenerationReport.Queries.Count, second.GenerationReport.Queries.Count);
        Assert.Throws<InvalidOperationException>(() => QueryValidationService.ApplyQueries(second.XmlContent, new[] { new GeneratedQuery { Formula = "A[] unknown > 0" } }));
    }
    [Fact]
    public async Task SourceAssertionsGenerateLocationGuardedSafetyChecks()
    {
        var m = await Generate("class C { static void Main() { int x = 1; System.Diagnostics.Debug.Assert(x > 0); } }");
        Assert.Contains(m.GenerationReport.Queries, q => q.Name.Contains("Assertion") && q.Formula.Contains("P_C_Main.x > 0") && q.Evidence.Contains(":"));
    }
    [Fact]
    public async Task VerifytaReportsUnavailableOrChecksBankAndCounterexample()
    {
        var m = await Generate(Bank);
        var verifier = new UppaalVerifier(Environment.GetEnvironmentVariable("VERIFYTA_PATH"));
        var results = await verifier.VerifyPropertiesAsync(m.XmlContent, new() { "E<> Driver.DriverDone", "A[] P_Account_Main.total < 0" });
        if (results.All(r => r.Result == "NotConfigured")) return;
        Assert.True(results[0].IsVerified, results[0].Result + results[0].ErrorMessage);
        Assert.False(results[1].IsVerified);
        Assert.Contains("NOT satisfied", results[1].Result, StringComparison.OrdinalIgnoreCase);
    }
}
