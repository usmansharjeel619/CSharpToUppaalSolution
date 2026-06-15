using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Generators;
using CSharpToUppaal.Backend.Mappers;
using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Project = CSharpToUppaal.Backend.Models.Project;

namespace CSharpToUppaal.Backend.Services
{
    public interface IUppaalGeneratorService
    {
        Task<UppaalModel> GenerateModelFromCodeAsync(string code, string modelName);
        Task<UppaalModel> GenerateModelFromRequestAsync(ModelGenerationRequest request);
        Task<UppaalModel> GenerateModelFromCfgAsync(ControlFlowGraph cfg, string modelName);
        Task<string> GenerateUppaalXmlAsync(UppaalModel model);
        Task<UppaalModel> GenerateModelFromProjectAsync(Project project);
    }

    public class UppaalGeneratorService : IUppaalGeneratorService
    {
        private readonly ICfgGeneratorService? _cfgGenerator;
        private readonly ICSharpSemanticAnalyzer _semanticAnalyzer;

        public UppaalGeneratorService(ICfgGeneratorService? cfgGenerator = null, ICSharpSemanticAnalyzer? semanticAnalyzer = null)
        {
            _cfgGenerator = cfgGenerator;
            _semanticAnalyzer = semanticAnalyzer ?? new CSharpSemanticAnalyzer();
        }

        public async Task<UppaalModel> GenerateModelFromCodeAsync(string code, string modelName)
        {
            return await GenerateModelFromRequestAsync(new ModelGenerationRequest
            {
                ProjectName = modelName,
                SourceCode = code,
                FileName = "Source.cs"
            }).ConfigureAwait(false);
        }

        public async Task<UppaalModel> GenerateModelFromProjectAsync(Project project)
        {
            var sourceCode = string.Join(Environment.NewLine + Environment.NewLine, project.SourceFiles.Select(sf => sf.Content));
            var selections = project.SourceFiles
                .SelectMany(sf => sf.Methods)
                .Select(m => new FunctionSelection
                {
                    FunctionId = string.Empty,
                    IsSelected = false,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                })
                .ToList();

            return await GenerateModelFromRequestAsync(new ModelGenerationRequest
            {
                ProjectName = $"{project.Name}_Model",
                SourceCode = sourceCode,
                FileName = project.SourceFiles.FirstOrDefault()?.FilePath ?? "Source.cs",
                FunctionSelections = selections
            }).ConfigureAwait(false);
        }

        public async Task<UppaalModel> GenerateModelFromRequestAsync(ModelGenerationRequest request)
        {
            try
            {
                var analysis = await _semanticAnalyzer
                    .AnalyzeSourceCodeAsync(request.SourceCode, request.FileName)
                    .ConfigureAwait(false);

                var selections = NormalizeSelections(analysis.Functions, request.FunctionSelections);
                var included = analysis.ResolveClosure(selections);
                var requirementQueries = await InterpretRequirementsAsync(request, analysis).ConfigureAwait(false);

                var builder = new SemanticUppaalBuilder(analysis, selections, included, request.DomainOverrides);
                var model = builder.Build(request.ProjectName, requirementQueries.Concat(request.UserQueries).ToList());

                model.GenerationReport.Functions = analysis.Functions;
                model.GenerationReport.IncludedFunctions = included.ToList();
                model.GenerationReport.Assumptions.InsertRange(0, analysis.Assumptions);
                foreach (var diagnostic in analysis.Diagnostics)
                {
                    model.GenerationReport.Assumptions.Add(new TranslationAssumption
                    {
                        Severity = AssumptionSeverity.Warning,
                        Category = "Compilation",
                        Message = diagnostic,
                        IsUserEditable = false
                    });
                }

                var layout = new UppaalLayoutService().FixLayoutWithReport(model.XmlContent);
                model.XmlContent = layout.XmlContent;
                model.GenerationReport.Layout = layout;

                var compatibility = new UppaalCompatibilityValidator().Validate(model.XmlContent);
                model.GenerationReport.Compatibility = compatibility;
                foreach (var issue in compatibility.Issues.Where(i => i.Severity != UppaalCompatibilitySeverity.Info))
                {
                    model.GenerationReport.Assumptions.Add(new TranslationAssumption
                    {
                        Severity = issue.Severity == UppaalCompatibilitySeverity.Error ? AssumptionSeverity.Error : AssumptionSeverity.Warning,
                        Category = $"UPPAAL4.1.18/{issue.Category}",
                        SymbolName = issue.Position,
                        Message = issue.Message,
                        IsUserEditable = false
                    });
                }

                model.Status = compatibility.IsReady ? ModelGenerationStatus.Success : ModelGenerationStatus.ValidationError;
                model.StatusMessage = $"Generated {model.Templates.Count} root process(es), {included.Count} included function(s), {compatibility.ErrorCount} readiness error(s), {compatibility.WarningCount} readiness warning(s).";
                model.GenerationReport.Summary = model.StatusMessage;
                return model;
            }
            catch (Exception ex)
            {
                return new UppaalModel
                {
                    Name = request.ProjectName,
                    Status = ModelGenerationStatus.GenerationError,
                    StatusMessage = $"Failed to generate UPPAAL model: {ex.Message}",
                    GenerationReport = new GenerationReport
                    {
                        Assumptions =
                        {
                            new TranslationAssumption
                            {
                                Severity = AssumptionSeverity.Error,
                                Category = "Generation",
                                Message = ex.Message
                            }
                        }
                    }
                };
            }
        }

        public async Task<UppaalModel> GenerateModelFromCfgAsync(ControlFlowGraph cfg, string modelName)
        {
            if (_cfgGenerator == null)
                throw new InvalidOperationException("CFG generator is not configured.");

            var mapper = new CfgToUppaalMapper();
            var template = mapper.Map(cfg);
            var model = new UppaalModel
            {
                Name = modelName,
                Templates = new List<UppaalTemplate> { template },
                Status = ModelGenerationStatus.Success
            };
            model.XmlContent = await GenerateUppaalXmlAsync(model).ConfigureAwait(false);
            return model;
        }

        public Task<string> GenerateUppaalXmlAsync(UppaalModel model)
        {
            var queries = model.GenerationReport.Queries.Count > 0
                ? model.GenerationReport.Queries
                : new List<GeneratedQuery>
                {
                    new GeneratedQuery
                    {
                        Name = "NoDeadlock",
                        Formula = "A[] not deadlock",
                        Comment = "Verify that the system is deadlock-free",
                        Source = "auto"
                    }
                };

            return Task.FromResult(SerializeModel(model.Name, string.Empty, model.Templates, queries));
        }

        private async Task<List<GeneratedQuery>> InterpretRequirementsAsync(ModelGenerationRequest request, CSharpSemanticAnalysisResult analysis)
        {
            var queries = new List<GeneratedQuery>();
            if (string.IsNullOrWhiteSpace(request.RequirementsText))
                return queries;

            var service = new RequirementTranslationService();
            var context = new RequirementTranslationContext
            {
                Functions = analysis.Functions,
                Variables = analysis.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>().Select(v => v.Identifier.Text).Distinct().ToList()
            };

            var interpretations = await service
                .InterpretAsync(request.RequirementsText, context, request.RequirementSettings)
                .ConfigureAwait(false);

            queries.AddRange(interpretations.SelectMany(i => i.GeneratedQueries));
            return queries;
        }

        private static List<FunctionSelection> NormalizeSelections(IReadOnlyList<FunctionDescriptor> functions, List<FunctionSelection> requested)
        {
            var result = new List<FunctionSelection>();
            var knownIds = functions.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var selection in requested.Where(s => !string.IsNullOrWhiteSpace(s.FunctionId) && knownIds.Contains(s.FunctionId)))
            {
                result.Add(selection);
            }

            if (result.Any(s => s.IsSelected))
                return result;

            var main = functions.FirstOrDefault(f => f.Name == "Main");
            if (main != null)
            {
                result.Add(new FunctionSelection
                {
                    FunctionId = main.Id,
                    IsSelected = true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                });
                return result;
            }

            foreach (var function in functions)
            {
                result.Add(new FunctionSelection
                {
                    FunctionId = function.Id,
                    IsSelected = true,
                    Mode = FunctionModelingMode.ExplicitAutomaton
                });
            }

            return result;
        }

        private static string SerializeModel(string modelName, string globalDeclaration, List<UppaalTemplate> templates, List<GeneratedQuery> queries)
        {
            var templateElements = templates.Select(TemplateToXml).ToList();
            var systemNames = templates.Select(t => t.Name).Distinct(StringComparer.Ordinal).ToList();
            var systemText = systemNames.Count == 0 ? string.Empty : $"system {string.Join(", ", systemNames)};";

            var nta = new XElement("nta",
                new XElement("declaration", new XText(globalDeclaration)),
                templateElements,
                new XElement("system", new XText(systemText)),
                new XElement("queries", queries.Select(q =>
                    new XElement("query",
                        new XElement("formula", q.Formula),
                        new XElement("comment", string.IsNullOrWhiteSpace(q.Comment) ? q.Name : q.Comment)))));

            var xdoc = new XDocument(
                new XDocumentType("nta", "-//Uppaal Team//DTD Flat System 1.1//EN", "http://www.it.uu.se/research/group/darts/uppaal/flat-1_1.dtd", null),
                nta);

            return xdoc.ToString();
        }

        private static XElement TemplateToXml(UppaalTemplate template)
        {
            return new XElement("template",
                new XElement("name", template.Name),
                new XElement("declaration", new XText(template.Declaration ?? string.Empty)),
                template.Locations.Select(LocationToXml),
                new XElement("init", new XAttribute("ref", template.Locations.First(l => l.IsInitial).Id)),
                template.Transitions.Select(TransitionToXml));
        }

        private static XElement LocationToXml(UppaalLocation location)
        {
            var element = new XElement("location",
                new XAttribute("id", location.Id),
                new XAttribute("x", location.Labels.TryGetValue("_x", out var x) ? x : "0"),
                new XAttribute("y", location.Labels.TryGetValue("_y", out var y) ? y : "0"),
                new XElement("name",
                    new XAttribute("x", location.Labels.TryGetValue("_nameX", out var nx) ? nx : "0"),
                    new XAttribute("y", location.Labels.TryGetValue("_nameY", out var ny) ? ny : "0"),
                    location.Name));

            if (location.IsUrgent)
                element.Add(new XElement("urgent"));
            if (location.IsCommitted)
                element.Add(new XElement("committed"));

            foreach (var label in location.Labels.Where(l => !l.Key.StartsWith("_", StringComparison.Ordinal)))
            {
                element.Add(new XElement("label",
                    new XAttribute("kind", label.Key),
                    new XAttribute("x", location.Labels.TryGetValue("_labelX", out var lx) ? lx : "0"),
                    new XAttribute("y", location.Labels.TryGetValue("_labelY", out var ly) ? ly : "0"),
                    label.Value));
            }

            return element;
        }

        private static XElement TransitionToXml(UppaalTransition transition)
        {
            var element = new XElement("transition",
                new XElement("source", new XAttribute("ref", transition.Source)),
                new XElement("target", new XAttribute("ref", transition.Target)));

            var y = 0;
            AddTransitionLabel(element, "select", transition.Select, y++);
            AddTransitionLabel(element, "guard", transition.Guard, y++);
            AddTransitionLabel(element, "synchronisation", transition.Synchronization, y++);
            AddTransitionLabel(element, "assignment", transition.Update, y);

            foreach (var nail in transition.Nails)
            {
                element.Add(new XElement("nail",
                    new XAttribute("x", nail.X),
                    new XAttribute("y", nail.Y)));
            }

            return element;
        }

        private static void AddTransitionLabel(XElement element, string kind, string value, int index)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            element.Add(new XElement("label",
                new XAttribute("kind", kind),
                new XAttribute("x", 20),
                new XAttribute("y", -20 + index * 18),
                value));
        }

        private sealed class SemanticUppaalBuilder
        {
            private readonly CSharpSemanticAnalysisResult _analysis;
            private readonly IReadOnlyList<FunctionDescriptor> _included;
            private readonly Dictionary<string, FunctionSelection> _selectionById;
            private readonly Dictionary<string, FunctionDescriptor> _functionById;
            private readonly Dictionary<string, MethodDeclarationSyntax> _methodById;
            private readonly Dictionary<string, VariableDomain> _domains = new(StringComparer.Ordinal);
            private readonly Dictionary<string, string> _uppaalFunctionNames = new(StringComparer.Ordinal);
            private readonly HashSet<string> _usedUppaalFunctionNames = new(StringComparer.Ordinal);
            private readonly HashSet<string> _usedTemplateNames = new(StringComparer.Ordinal);
            private readonly List<TranslationAssumption> _assumptions = new();
            private readonly HashSet<string> _generatedFunctionIds = new(StringComparer.Ordinal);
            private readonly List<GeneratedQuery> _queries = new();
            private int _globalId;

            public SemanticUppaalBuilder(
                CSharpSemanticAnalysisResult analysis,
                IReadOnlyList<FunctionSelection> selections,
                IReadOnlyList<FunctionDescriptor> included,
                IReadOnlyList<VariableDomain> domainOverrides)
            {
                _analysis = analysis;
                _included = included;
                _selectionById = selections
                    .GroupBy(s => s.FunctionId, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                _functionById = analysis.Functions.ToDictionary(f => f.Id, StringComparer.Ordinal);
                _methodById = analysis.MethodDeclarationsById;

                foreach (var function in analysis.Functions)
                {
                    _uppaalFunctionNames[function.Id] = MakeUniqueIdentifier($"fn_{function.DisplayName}", _usedUppaalFunctionNames);
                }

                foreach (var domain in domainOverrides)
                {
                    if (!string.IsNullOrWhiteSpace(domain.Name))
                        _domains[domain.Name] = domain;
                }
            }

            public UppaalModel Build(string modelName, List<GeneratedQuery> requirementQueries)
            {
                var model = new UppaalModel
                {
                    Name = UniqueIdentifier(modelName),
                    Status = ModelGenerationStatus.InProgress
                };

                var globalDeclaration = new StringBuilder();
                globalDeclaration.AppendLine("// Generated by CSharpToUppaal semantic pipeline");
                globalDeclaration.AppendLine("// Unknowns are represented as finite bounded choices and listed in assumptions.");
                globalDeclaration.AppendLine();

                foreach (var function in GetDependencyFirstFunctions())
                {
                    AddFunctionAssumptions(function);
                    GenerateGlobalFunction(function, globalDeclaration);
                }

                var roots = _included.Where(IsRoot).ToList();
                if (roots.Count == 0 && _included.Count > 0)
                    roots.Add(_included[0]);

                foreach (var root in roots)
                {
                    var template = BuildRootTemplate(root);
                    model.Templates.Add(template);
                    _queries.Add(new GeneratedQuery
                    {
                        Name = $"Reach_{template.Name}_Done",
                        Formula = $"E<> {template.Name}.Done",
                        Comment = $"Reachability: {template.Name} can finish.",
                        Source = "auto"
                    });
                }

                _queries.Insert(0, new GeneratedQuery
                {
                    Name = "NoDeadlock",
                    Formula = "A[] not deadlock",
                    Comment = "Verify that the system is deadlock-free.",
                    Source = "auto"
                });
                _queries.AddRange(requirementQueries.Where(q => !string.IsNullOrWhiteSpace(q.Formula)));

                model.GenerationReport.Assumptions = _assumptions;
                model.GenerationReport.Domains = _domains.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
                model.GenerationReport.Queries = _queries;
                model.XmlContent = SerializeModel(model.Name, globalDeclaration.ToString(), model.Templates, _queries);
                return model;
            }

            private List<FunctionDescriptor> GetDependencyFirstFunctions()
            {
                var includedIds = _included.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
                var ordered = new List<FunctionDescriptor>();
                var visiting = new HashSet<string>(StringComparer.Ordinal);
                var visited = new HashSet<string>(StringComparer.Ordinal);

                void Visit(FunctionDescriptor function)
                {
                    if (!visited.Add(function.Id))
                        return;

                    if (!visiting.Add(function.Id))
                        return;

                    foreach (var dependencyId in function.DirectCallIds.Where(includedIds.Contains))
                    {
                        if (_functionById.TryGetValue(dependencyId, out var dependency))
                            Visit(dependency);
                    }

                    visiting.Remove(function.Id);
                    ordered.Add(function);
                }

                foreach (var function in _included.OrderBy(f => f.LineNumber).ThenBy(f => f.Signature, StringComparer.Ordinal))
                    Visit(function);

                return ordered;
            }

            private bool IsRoot(FunctionDescriptor function)
            {
                return _selectionById.TryGetValue(function.Id, out var selection) && selection.IsSelected;
            }

            private FunctionModelingMode ModeOf(FunctionDescriptor function)
            {
                return _selectionById.TryGetValue(function.Id, out var selection)
                    ? selection.Mode
                    : FunctionModelingMode.CodeBlock;
            }

            private void AddFunctionAssumptions(FunctionDescriptor function)
            {
                if (function.IsAsync)
                {
                    _assumptions.Add(new TranslationAssumption
                    {
                        Severity = AssumptionSeverity.Warning,
                        Category = "UnsupportedConstruct",
                        SymbolName = function.Signature,
                        Message = "Async behavior is abstracted as synchronous execution."
                    });
                }

                foreach (var unresolved in function.UnresolvedCalls)
                {
                    _assumptions.Add(new TranslationAssumption
                    {
                        Severity = AssumptionSeverity.Warning,
                        Category = "UnknownFunction",
                        SymbolName = unresolved,
                        Location = function.Signature,
                        Message = $"Unknown call '{unresolved}' is represented by a bounded nondeterministic/default return.",
                        IsUserEditable = true
                    });
                }
            }

            private void GenerateGlobalFunction(FunctionDescriptor function, StringBuilder declarations)
            {
                if (_generatedFunctionIds.Contains(function.Id))
                    return;

                _generatedFunctionIds.Add(function.Id);

                if (ModeOf(function) == FunctionModelingMode.Stub || !_methodById.ContainsKey(function.Id))
                {
                    declarations.AppendLine(GenerateStubFunction(function));
                    declarations.AppendLine();
                    return;
                }

                var method = _methodById[function.Id];
                var functionName = _uppaalFunctionNames[function.Id];
                var returnType = MapType(function.ReturnType);
                var parameters = string.Join(", ", function.Parameters.Select(p => $"{MapType(p.Type)} {Sanitize(p.Name)}"));

                declarations.AppendLine($"{returnType} {functionName}({parameters})");
                declarations.AppendLine("{");

                var localDeclarations = CollectLocalVariables(method)
                    .Where(v => function.Parameters.All(p => !p.Name.Equals(v.name, StringComparison.Ordinal)))
                    .GroupBy(v => Sanitize(v.name), StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();

                foreach (var local in localDeclarations)
                    declarations.AppendLine($"  {MapType(local.type)} {Sanitize(local.name)};");

                var writer = new FunctionBodyWriter(this, function);
                if (method.Body != null)
                {
                    foreach (var statement in method.Body.Statements)
                        writer.WriteStatement(declarations, statement, 1);
                }
                else if (method.ExpressionBody != null)
                {
                    declarations.AppendLine($"  return {writer.TranslateExpression(method.ExpressionBody.Expression)};");
                }

                if (!function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase)
                    && !MethodDefinitelyReturns(method))
                {
                    _assumptions.Add(new TranslationAssumption
                    {
                        Severity = AssumptionSeverity.Warning,
                        Category = "Return",
                        SymbolName = function.Signature,
                        Message = "A default return was added because not all translated paths were proven to return.",
                        IsUserEditable = false
                    });
                    declarations.AppendLine($"  return {DefaultValue(function.ReturnType)};");
                }

                declarations.AppendLine("}");
                declarations.AppendLine();
            }

            private static bool MethodDefinitelyReturns(MethodDeclarationSyntax method)
            {
                if (method.ExpressionBody != null)
                    return true;

                return method.Body != null && StatementListDefinitelyReturns(method.Body.Statements);
            }

            private static bool StatementListDefinitelyReturns(SyntaxList<StatementSyntax> statements)
            {
                foreach (var statement in statements)
                {
                    if (StatementDefinitelyReturns(statement))
                        return true;
                }

                return false;
            }

            private static bool StatementDefinitelyReturns(StatementSyntax statement)
            {
                return statement switch
                {
                    ReturnStatementSyntax => true,
                    BlockSyntax block => StatementListDefinitelyReturns(block.Statements),
                    IfStatementSyntax ifStatement => ifStatement.Else != null
                        && StatementDefinitelyReturns(ifStatement.Statement)
                        && StatementDefinitelyReturns(ifStatement.Else.Statement),
                    _ => false
                };
            }

            private string GenerateStubFunction(FunctionDescriptor function)
            {
                var returnType = MapType(function.ReturnType);
                var parameters = string.Join(", ", function.Parameters.Select(p => $"{MapType(p.Type)} {Sanitize(p.Name)}"));
                var body = returnType == "void"
                    ? string.Empty
                    : $"{Environment.NewLine}  return {DefaultValue(function.ReturnType)};{Environment.NewLine}";

                _assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Warning,
                    Category = "Stub",
                    SymbolName = function.Signature,
                    Message = $"Function '{function.Signature}' is generated as a stub.",
                    IsUserEditable = true
                });

                return $"{returnType} {_uppaalFunctionNames[function.Id]}({parameters}){{{body}}}";
            }

            private UppaalTemplate BuildRootTemplate(FunctionDescriptor function)
            {
                var mode = ModeOf(function);
                var template = new TemplateBuilder(MakeUniqueIdentifier($"P_{function.DisplayName}", _usedTemplateNames), () => _globalId++);
                var method = _methodById.TryGetValue(function.Id, out var foundMethod) ? foundMethod : null;

                template.Declarations.AppendLine($"// Root function: {function.Signature}");
                foreach (var parameter in function.Parameters)
                {
                    var domain = GetDomain(function, parameter.Name, parameter.Type, "parameter");
                    template.Declarations.AppendLine($"{domain.ToUppaalDeclType()} {Sanitize(parameter.Name)} = {domain.DefaultValue()};");
                }

                if (!function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase))
                {
                    var retDomain = GetDomain(function, "ret", function.ReturnType, "return");
                    template.Declarations.AppendLine($"{retDomain.ToUppaalDeclType()} ret = {retDomain.DefaultValue()};");
                }

                if (method != null && mode == FunctionModelingMode.ExplicitAutomaton)
                {
                    foreach (var local in CollectLocalVariables(method)
                        .Where(v => function.Parameters.All(p => !p.Name.Equals(v.name, StringComparison.Ordinal)))
                        .GroupBy(v => Sanitize(v.name), StringComparer.Ordinal)
                        .Select(g => g.First()))
                    {
                        var isBool = local.type.Trim().Equals("bool", StringComparison.OrdinalIgnoreCase)
                                  || local.type.Trim().Equals("Boolean", StringComparison.OrdinalIgnoreCase);
                        var defaultVal = isBool ? "false" : "0";
                        template.Declarations.AppendLine($"{MapType(local.type)} {Sanitize(local.name)} = {defaultVal};");
                    }
                }

                var entry = template.AddLocation("Entry", initial: true);
                var done = template.AddLocation("Done");

                var start = entry;
                var initSelects = new List<string>();
                var initUpdates = new List<string>();
                foreach (var parameter in function.Parameters)
                {
                    var domain = GetDomain(function, parameter.Name, parameter.Type, "parameter");
                    var selectName = $"{Sanitize(parameter.Name)}_in";
                    initSelects.Add($"{selectName}:{domain.ToUppaalSelectType()}");
                    initUpdates.Add($"{Sanitize(parameter.Name)} = {selectName}");
                }

                if (initSelects.Count > 0)
                {
                    var inputs = template.AddLocation("Inputs");
                    template.AddTransition(entry, inputs, select: string.Join(", ", initSelects), update: string.Join(", ", initUpdates));
                    start = inputs;
                }

                if (mode == FunctionModelingMode.CodeBlock || mode == FunctionModelingMode.Stub || method == null)
                {
                    var update = BuildRootCodeBlockUpdate(function, mode);
                    template.AddTransition(start, done, select: update.select, update: update.update);
                }
                else
                {
                    var bodyBuilder = new AutomatonBodyBuilder(this, function, template, done);
                    var exits = method.Body != null
                        ? bodyBuilder.BuildStatementList(method.Body.Statements, new List<string> { start })
                        : method.ExpressionBody != null
                            ? bodyBuilder.BuildReturnExpression(method.ExpressionBody.Expression, new List<string> { start })
                            : new List<string> { start };

                    foreach (var exit in exits)
                        template.AddTransition(exit, done);
                }

                template.AddTransition(done, entry);
                return template.ToTemplate();
            }

            private (string select, string update) BuildRootCodeBlockUpdate(FunctionDescriptor function, FunctionModelingMode mode)
            {
                if (mode == FunctionModelingMode.Stub)
                {
                    if (function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase))
                        return (string.Empty, string.Empty);

                    var domain = GetDomain(function, "ret", function.ReturnType, "stub return");
                    return ($"ret_choice:{domain.ToUppaalSelectType()}", $"ret = ret_choice");
                }

                var args = string.Join(", ", function.Parameters.Select(p => Sanitize(p.Name)));
                var call = $"{_uppaalFunctionNames[function.Id]}({args})";
                return function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase)
                    ? (string.Empty, call)
                    : (string.Empty, $"ret = {call}");
            }

            private VariableDomain GetDomain(FunctionDescriptor function, string variableName, string type, string source)
            {
                var qualified = $"{function.DisplayName}.{variableName}";
                if (_domains.TryGetValue(qualified, out var overrideDomain))
                    return overrideDomain;
                if (_domains.TryGetValue(variableName, out overrideDomain))
                    return overrideDomain;

                var isBool = type.Equals("bool", StringComparison.OrdinalIgnoreCase)
                          || type.Equals("Boolean", StringComparison.OrdinalIgnoreCase);
                var domain = new VariableDomain
                {
                    Name = qualified,
                    Type = isBool ? "bool" : "int",
                    IsBoolean = isBool,
                    Min = -10,
                    Max = 10,
                    Source = source
                };
                _domains[qualified] = domain;
                return domain;
            }

            private static List<(string name, string type)> CollectLocalVariables(MethodDeclarationSyntax method)
            {
                var variables = new List<(string name, string type)>();

                foreach (var localDecl in method.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    foreach (var variable in localDecl.Declaration.Variables)
                        variables.Add((variable.Identifier.Text, localDecl.Declaration.Type.ToString()));
                }

                foreach (var forStmt in method.DescendantNodes().OfType<ForStatementSyntax>())
                {
                    if (forStmt.Declaration == null)
                        continue;

                    foreach (var variable in forStmt.Declaration.Variables)
                        variables.Add((variable.Identifier.Text, forStmt.Declaration.Type.ToString()));
                }

                return variables;
            }

            private string TranslateInvocation(InvocationExpressionSyntax invocation, FunctionDescriptor currentFunction, string fallbackType, out string? unknownCallName)
            {
                unknownCallName = null;
                var info = _analysis.SemanticModel.GetSymbolInfo(invocation);
                var symbol = info.Symbol as IMethodSymbol ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

                if (symbol != null)
                {
                    var id = CSharpSemanticAnalyzer.ToFunctionId(symbol);
                    if (_uppaalFunctionNames.TryGetValue(id, out var functionName))
                    {
                        var args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => TranslateExpression(a.Expression, currentFunction, "int")));
                        return $"{functionName}({args})";
                    }

                    unknownCallName = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                }
                else
                {
                    unknownCallName = invocation.Expression.ToString();
                }

                _assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Warning,
                    Category = "UnknownFunction",
                    SymbolName = unknownCallName,
                    Location = currentFunction.Signature,
                    Message = $"Invocation '{unknownCallName}' was abstracted.",
                    IsUserEditable = true
                });

                return DefaultValue(fallbackType);
            }

            private string TranslateExpression(ExpressionSyntax expression, FunctionDescriptor function, string expectedType)
            {
                return expression switch
                {
                    LiteralExpressionSyntax literal => TranslateLiteral(literal, expectedType),
                    IdentifierNameSyntax identifier => Sanitize(identifier.Identifier.Text),
                    ParenthesizedExpressionSyntax parenthesized => $"({TranslateExpression(parenthesized.Expression, function, expectedType)})",
                    BinaryExpressionSyntax binary => $"{TranslateExpression(binary.Left, function, expectedType)} {TranslateOperator(binary.OperatorToken.Text)} {TranslateExpression(binary.Right, function, expectedType)}",
                    PrefixUnaryExpressionSyntax prefix => $"{TranslateOperator(prefix.OperatorToken.Text)}{TranslateExpression(prefix.Operand, function, expectedType)}",
                    PostfixUnaryExpressionSyntax postfix => TranslateExpression(postfix.Operand, function, expectedType),
                    AssignmentExpressionSyntax assignment => $"{TranslateExpression(assignment.Left, function, expectedType)} = {TranslateExpression(assignment.Right, function, expectedType)}",
                    InvocationExpressionSyntax invocation => TranslateInvocation(invocation, function, expectedType, out _),
                    CastExpressionSyntax cast => TranslateExpression(cast.Expression, function, expectedType),
                    MemberAccessExpressionSyntax member => Sanitize(member.Name.Identifier.Text),
                    ConditionalExpressionSyntax conditional => $"({TranslateExpression(conditional.Condition, function, "bool")} ? {TranslateExpression(conditional.WhenTrue, function, expectedType)} : {TranslateExpression(conditional.WhenFalse, function, expectedType)})",
                    _ => UnsupportedExpression(expression, function, expectedType)
                };
            }

            private string UnsupportedExpression(ExpressionSyntax expression, FunctionDescriptor function, string expectedType)
            {
                _assumptions.Add(new TranslationAssumption
                {
                    Severity = AssumptionSeverity.Warning,
                    Category = "UnsupportedExpression",
                    Location = function.Signature,
                    SymbolName = expression.ToString(),
                    Message = $"Expression '{expression}' is outside the supported v1 subset and was replaced by a default value.",
                    IsUserEditable = true
                });

                return DefaultValue(expectedType);
            }

            private static string TranslateLiteral(LiteralExpressionSyntax literal, string expectedType)
            {
                if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
                    return "true";
                if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
                    return "false";
                if (literal.IsKind(SyntaxKind.NullLiteralExpression))
                    return DefaultValue(expectedType);
                if (literal.Token.Value is char c)
                    return ((int)c).ToString();
                if (literal.Token.Value is string)
                    return "0";

                return literal.Token.ValueText;
            }

            private static string TranslateOperator(string op)
            {
                return op switch
                {
                    "!" => "!",
                    "&&" => "&&",
                    "||" => "||",
                    _ => op
                };
            }

            private static string Negate(string condition)
            {
                condition = condition.Trim();
                if (!condition.Contains("&&", StringComparison.Ordinal) && !condition.Contains("||", StringComparison.Ordinal))
                {
                    if (condition.Contains("<=", StringComparison.Ordinal)) return condition.Replace("<=", ">");
                    if (condition.Contains(">=", StringComparison.Ordinal)) return condition.Replace(">=", "<");
                    if (condition.Contains("!=", StringComparison.Ordinal)) return condition.Replace("!=", "==");
                    if (condition.Contains("==", StringComparison.Ordinal)) return condition.Replace("==", "!=");
                    if (condition.Contains("<", StringComparison.Ordinal)) return condition.Replace("<", ">=");
                    if (condition.Contains(">", StringComparison.Ordinal)) return condition.Replace(">", "<=");
                }

                return $"!({condition})";
            }

            private static string MapType(string csharpType)
            {
                var type = csharpType.Trim().ToLowerInvariant();
                return type switch
                {
                    "bool" or "boolean" => "bool",
                    "void" => "void",
                    _ => "int"
                };
            }

            private static string DefaultValue(string csharpType)
            {
                var type = csharpType.Trim().ToLowerInvariant();
                return type is "bool" or "boolean" ? "false" : "0";
            }

            private static string UniqueIdentifier(string raw)
            {
                var sanitized = Sanitize(raw);
                return sanitized.Length > 80 ? sanitized[..80] : sanitized;
            }

            private static string MakeUniqueIdentifier(string raw, HashSet<string> used)
            {
                var baseName = UniqueIdentifier(raw);
                var candidate = baseName;
                var index = 2;
                while (!used.Add(candidate))
                {
                    var suffix = $"_{index:00}";
                    var maxBaseLength = Math.Max(1, 80 - suffix.Length);
                    candidate = $"{(baseName.Length > maxBaseLength ? baseName[..maxBaseLength] : baseName)}{suffix}";
                    index++;
                }

                return candidate;
            }

            private static string Sanitize(string raw)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    return "value";

                var sb = new StringBuilder();
                foreach (var ch in raw)
                    sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');

                var value = sb.ToString().Trim('_');
                if (string.IsNullOrWhiteSpace(value))
                    value = "value";

                if (!char.IsLetter(value[0]) && value[0] != '_')
                    value = "_" + value;

                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "int", "bool", "clock", "chan", "urgent", "committed", "system", "process",
                    "state", "guard", "sync", "assign", "select", "init", "true", "false",
                    "void", "return", "if", "else", "for", "while", "do"
                };
                return reserved.Contains(value) ? $"v_{value}" : value;
            }

            private sealed class TemplateBuilder
            {
                private readonly List<UppaalLocation> _locations = new();
                private readonly List<UppaalTransition> _transitions = new();
                private readonly Dictionary<string, int> _locationNameCounts = new(StringComparer.Ordinal);
                private int _locationIndex;
                private readonly string _name;
                private readonly Func<int> _nextGlobalId;

                public StringBuilder Declarations { get; } = new();

                public TemplateBuilder(string name, Func<int> nextGlobalId)
                {
                    _name = name;
                    _nextGlobalId = nextGlobalId;
                }

                public string AddLocation(string name, bool initial = false, bool urgent = false)
                {
                    var id = $"id{_nextGlobalId()}";
                    var x = (_locationIndex % 4) * 220;
                    var y = (_locationIndex / 4) * 120;
                    _locationIndex++;
                    var locationName = MakeUniqueLocationName(name);

                    _locations.Add(new UppaalLocation
                    {
                        Id = id,
                        Name = locationName,
                        IsInitial = initial,
                        IsUrgent = urgent,
                        Labels =
                        {
                            ["_x"] = x.ToString(),
                            ["_y"] = y.ToString(),
                            ["_nameX"] = ApproximateCenteredNameX(locationName, x).ToString(),
                            ["_nameY"] = (y - 7).ToString()
                        }
                    });
                    return id;
                }

                private string MakeUniqueLocationName(string rawName)
                {
                    var baseName = Sanitize(rawName);
                    _locationNameCounts.TryGetValue(baseName, out var count);
                    count++;
                    _locationNameCounts[baseName] = count;

                    if ((baseName.Equals("Entry", StringComparison.Ordinal) || baseName.Equals("Done", StringComparison.Ordinal)) && count == 1)
                        return baseName;

                    return $"{baseName}_{count:00}";
                }

                private static int ApproximateCenteredNameX(string name, int x)
                {
                    return x - Math.Max(12, name.Length * 3);
                }

                public void AddTransition(string source, string target, string guard = "", string update = "", string select = "")
                {
                    _transitions.Add(new UppaalTransition
                    {
                        Source = source,
                        Target = target,
                        Guard = guard,
                        Update = update,
                        Select = select
                    });
                }

                public UppaalTemplate ToTemplate()
                {
                    return new UppaalTemplate
                    {
                        Name = _name,
                        Declaration = Declarations.ToString(),
                        Locations = _locations,
                        Transitions = _transitions
                    };
                }
            }

            private sealed class AutomatonBodyBuilder
            {
                private readonly SemanticUppaalBuilder _owner;
                private readonly FunctionDescriptor _function;
                private readonly TemplateBuilder _template;
                private readonly string _done;

                public AutomatonBodyBuilder(SemanticUppaalBuilder owner, FunctionDescriptor function, TemplateBuilder template, string done)
                {
                    _owner = owner;
                    _function = function;
                    _template = template;
                    _done = done;
                }

                public List<string> BuildStatementList(IEnumerable<StatementSyntax> statements, List<string> starts)
                {
                    var current = starts;
                    foreach (var statement in statements)
                    {
                        if (current.Count == 0)
                            break;
                        current = BuildStatement(statement, current);
                    }

                    return current;
                }

                public List<string> BuildReturnExpression(ExpressionSyntax expression, List<string> starts)
                {
                    var update = _function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : $"ret = {_owner.TranslateExpression(expression, _function, _function.ReturnType)}";
                    var loc = _template.AddLocation("Return", urgent: true);
                    foreach (var start in starts)
                        _template.AddTransition(start, loc, update: update);
                    _template.AddTransition(loc, _done);
                    return new List<string>();
                }

                private List<string> BuildStatement(StatementSyntax statement, List<string> starts)
                {
                    return statement switch
                    {
                        BlockSyntax block => BuildStatementList(block.Statements, starts),
                        LocalDeclarationStatementSyntax local => BuildLocalDeclaration(local, starts),
                        ExpressionStatementSyntax expr => BuildExpressionStatement(expr, starts),
                        ReturnStatementSyntax ret => BuildReturn(ret, starts),
                        IfStatementSyntax ifStmt => BuildIf(ifStmt, starts),
                        WhileStatementSyntax whileStmt => BuildWhile(whileStmt, starts),
                        ForStatementSyntax forStmt => BuildFor(forStmt, starts),
                        DoStatementSyntax doStmt => BuildDoWhile(doStmt, starts),
                        BreakStatementSyntax => starts,
                        EmptyStatementSyntax => starts,
                        _ => BuildUnsupported(statement, starts)
                    };
                }

                private List<string> BuildExpressionStatement(ExpressionStatementSyntax statement, List<string> starts)
                {
                    var update = BuildExpressionUpdate(statement.Expression, out var select);
                    return BuildSimple(starts, "Stmt", update, select);
                }

                private List<string> BuildReturn(ReturnStatementSyntax statement, List<string> starts)
                {
                    var update = string.Empty;
                    if (statement.Expression != null && !_function.ReturnType.Equals("void", StringComparison.OrdinalIgnoreCase))
                        update = $"ret = {_owner.TranslateExpression(statement.Expression, _function, _function.ReturnType)}";

                    var loc = _template.AddLocation("Return", urgent: true);
                    foreach (var start in starts)
                        _template.AddTransition(start, loc, update: update);
                    _template.AddTransition(loc, _done);
                    return new List<string>();
                }

                private List<string> BuildIf(IfStatementSyntax statement, List<string> starts)
                {
                    var condLoc = _template.AddLocation("If");
                    foreach (var start in starts)
                        _template.AddTransition(start, condLoc);

                    var thenLoc = _template.AddLocation("Then");
                    var elseLoc = _template.AddLocation("Else");
                    var condition = _owner.TranslateExpression(statement.Condition, _function, "bool");
                    _template.AddTransition(condLoc, thenLoc, guard: condition);
                    _template.AddTransition(condLoc, elseLoc, guard: Negate(condition));

                    var exits = new List<string>();
                    exits.AddRange(BuildStatement(statement.Statement, new List<string> { thenLoc }));
                    exits.AddRange(statement.Else != null
                        ? BuildStatement(statement.Else.Statement, new List<string> { elseLoc })
                        : new List<string> { elseLoc });

                    if (exits.Count == 0)
                        return exits;

                    var merge = _template.AddLocation("Merge");
                    foreach (var exit in exits)
                        _template.AddTransition(exit, merge);
                    return new List<string> { merge };
                }

                private List<string> BuildWhile(WhileStatementSyntax statement, List<string> starts)
                {
                    var cond = _template.AddLocation("While");
                    var body = _template.AddLocation("LoopBody");
                    var after = _template.AddLocation("AfterLoop");
                    foreach (var start in starts)
                        _template.AddTransition(start, cond);

                    var condition = _owner.TranslateExpression(statement.Condition, _function, "bool");
                    _template.AddTransition(cond, body, guard: condition);
                    _template.AddTransition(cond, after, guard: Negate(condition));

                    foreach (var exit in BuildStatement(statement.Statement, new List<string> { body }))
                        _template.AddTransition(exit, cond);

                    return new List<string> { after };
                }

                private List<string> BuildFor(ForStatementSyntax statement, List<string> starts)
                {
                    var current = starts;
                    var initUpdate = BuildForInitializerUpdate(statement);
                    if (!string.IsNullOrWhiteSpace(initUpdate))
                        current = BuildSimple(current, "ForInit", initUpdate);

                    var cond = _template.AddLocation("For");
                    var body = _template.AddLocation("ForBody");
                    var after = _template.AddLocation("AfterFor");
                    foreach (var start in current)
                        _template.AddTransition(start, cond);

                    var condition = statement.Condition == null
                        ? "true"
                        : _owner.TranslateExpression(statement.Condition, _function, "bool");
                    _template.AddTransition(cond, body, guard: condition);
                    _template.AddTransition(cond, after, guard: Negate(condition));

                    var bodyExits = BuildStatement(statement.Statement, new List<string> { body });
                    var increment = string.Join(", ", statement.Incrementors.Select(i => BuildExpressionUpdate(i, out _)).Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (!string.IsNullOrWhiteSpace(increment))
                    {
                        var inc = _template.AddLocation("ForUpdate");
                        foreach (var exit in bodyExits)
                            _template.AddTransition(exit, inc, update: increment);
                        _template.AddTransition(inc, cond);
                    }
                    else
                    {
                        foreach (var exit in bodyExits)
                            _template.AddTransition(exit, cond);
                    }

                    return new List<string> { after };
                }

                private List<string> BuildDoWhile(DoStatementSyntax statement, List<string> starts)
                {
                    var body = _template.AddLocation("DoBody");
                    foreach (var start in starts)
                        _template.AddTransition(start, body);

                    var exits = BuildStatement(statement.Statement, new List<string> { body });
                    var cond = _template.AddLocation("DoWhile");
                    var after = _template.AddLocation("AfterDo");
                    foreach (var exit in exits)
                        _template.AddTransition(exit, cond);

                    var condition = _owner.TranslateExpression(statement.Condition, _function, "bool");
                    _template.AddTransition(cond, body, guard: condition);
                    _template.AddTransition(cond, after, guard: Negate(condition));
                    return new List<string> { after };
                }

                private List<string> BuildUnsupported(StatementSyntax statement, List<string> starts)
                {
                    _owner._assumptions.Add(new TranslationAssumption
                    {
                        Severity = AssumptionSeverity.Warning,
                        Category = "UnsupportedStatement",
                        Location = _function.Signature,
                        SymbolName = statement.Kind().ToString(),
                        Message = $"Statement '{statement.Kind()}' is outside the supported v1 subset and was abstracted.",
                        IsUserEditable = true
                    });
                    return BuildSimple(starts, "Unsupported", string.Empty);
                }

                private List<string> BuildSimple(List<string> starts, string name, string update, string select = "")
                {
                    var loc = _template.AddLocation(name);
                    foreach (var start in starts)
                        _template.AddTransition(start, loc, update: update, select: select);
                    return new List<string> { loc };
                }

                private List<string> BuildLocalDeclaration(LocalDeclarationStatementSyntax local, List<string> starts)
                {
                    var update = BuildDeclarationUpdate(local, out var select);
                    return BuildSimple(starts, "Declare", update, select);
                }

                private string BuildDeclarationUpdate(LocalDeclarationStatementSyntax local, out string select)
                {
                    select = string.Empty;
                    var updates = new List<string>();
                    foreach (var variable in local.Declaration.Variables)
                    {
                        if (variable.Initializer == null)
                            continue;

                        var left = Sanitize(variable.Identifier.Text);
                        if (variable.Initializer.Value is InvocationExpressionSyntax invocation)
                        {
                            var translated = _owner.TranslateInvocation(invocation, _function, local.Declaration.Type.ToString(), out var unknown);
                            if (unknown != null)
                            {
                                var domain = _owner.GetDomain(_function, $"{left}_stub", local.Declaration.Type.ToString(), "unknown function");
                                var selected = $"{left}_choice";
                                select = string.IsNullOrWhiteSpace(select)
                                    ? $"{selected}:{domain.ToUppaalSelectType()}"
                                    : $"{select}, {selected}:{domain.ToUppaalSelectType()}";
                                updates.Add($"{left} = {selected}");
                            }
                            else
                            {
                                updates.Add($"{left} = {translated}");
                            }
                        }
                        else
                        {
                            updates.Add($"{left} = {_owner.TranslateExpression(variable.Initializer.Value, _function, local.Declaration.Type.ToString())}");
                        }
                    }
                    return string.Join(", ", updates);
                }

                private string BuildForInitializerUpdate(ForStatementSyntax statement)
                {
                    var updates = new List<string>();
                    if (statement.Declaration != null)
                    {
                        foreach (var variable in statement.Declaration.Variables)
                        {
                            if (variable.Initializer != null)
                                updates.Add($"{Sanitize(variable.Identifier.Text)} = {_owner.TranslateExpression(variable.Initializer.Value, _function, statement.Declaration.Type.ToString())}");
                        }
                    }

                    foreach (var initializer in statement.Initializers)
                        updates.Add(BuildExpressionUpdate(initializer, out _));

                    return string.Join(", ", updates.Where(s => !string.IsNullOrWhiteSpace(s)));
                }

                private string BuildExpressionUpdate(ExpressionSyntax expression, out string select)
                {
                    select = string.Empty;

                    if (expression is PostfixUnaryExpressionSyntax postfix &&
                        (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                    {
                        var variable = _owner.TranslateExpression(postfix.Operand, _function, "int");
                        return postfix.IsKind(SyntaxKind.PostIncrementExpression)
                            ? $"{variable} = {variable} + 1"
                            : $"{variable} = {variable} - 1";
                    }

                    if (expression is PrefixUnaryExpressionSyntax prefix &&
                        (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                    {
                        var variable = _owner.TranslateExpression(prefix.Operand, _function, "int");
                        return prefix.IsKind(SyntaxKind.PreIncrementExpression)
                            ? $"{variable} = {variable} + 1"
                            : $"{variable} = {variable} - 1";
                    }

                    if (expression is AssignmentExpressionSyntax assignment)
                    {
                        var left = _owner.TranslateExpression(assignment.Left, _function, "int");
                        if (assignment.Right is InvocationExpressionSyntax invocation)
                        {
                            var translated = _owner.TranslateInvocation(invocation, _function, "int", out var unknown);
                            if (unknown != null)
                            {
                                var domain = _owner.GetDomain(_function, $"{left}_stub", "int", "unknown function");
                                var selected = $"{left}_choice";
                                select = $"{selected}:{domain.ToUppaalSelectType()}";
                                return $"{left} = {selected}";
                            }

                            return $"{left} = {translated}";
                        }

                        var right = _owner.TranslateExpression(assignment.Right, _function, "int");
                        return assignment.Kind() switch
                        {
                            SyntaxKind.AddAssignmentExpression => $"{left} = {left} + {right}",
                            SyntaxKind.SubtractAssignmentExpression => $"{left} = {left} - {right}",
                            SyntaxKind.MultiplyAssignmentExpression => $"{left} = {left} * {right}",
                            SyntaxKind.DivideAssignmentExpression => $"{left} = {left} / {right}",
                            SyntaxKind.ModuloAssignmentExpression => $"{left} = {left} % {right}",
                            _ => $"{left} = {right}"
                        };
                    }

                    if (expression is InvocationExpressionSyntax call)
                        return _owner.TranslateInvocation(call, _function, "void", out _);

                    return string.Empty;
                }
            }

            private sealed class FunctionBodyWriter
            {
                private readonly SemanticUppaalBuilder _owner;
                private readonly FunctionDescriptor _function;

                public FunctionBodyWriter(SemanticUppaalBuilder owner, FunctionDescriptor function)
                {
                    _owner = owner;
                    _function = function;
                }

                public string TranslateExpression(ExpressionSyntax expression)
                    => _owner.TranslateExpression(expression, _function, "int");

                public void WriteStatement(StringBuilder sb, StatementSyntax statement, int indent)
                {
                    var pad = new string(' ', indent * 2);
                    switch (statement)
                    {
                        case BlockSyntax block:
                            foreach (var child in block.Statements)
                                WriteStatement(sb, child, indent);
                            break;
                        case LocalDeclarationStatementSyntax local:
                            foreach (var variable in local.Declaration.Variables)
                            {
                                if (variable.Initializer != null)
                                {
                                    sb.AppendLine($"{pad}{Sanitize(variable.Identifier.Text)} = {_owner.TranslateExpression(variable.Initializer.Value, _function, local.Declaration.Type.ToString())};");
                                }
                            }
                            break;
                        case ExpressionStatementSyntax expr:
                            var update = ExpressionToStatement(expr.Expression);
                            if (!string.IsNullOrWhiteSpace(update))
                                sb.AppendLine($"{pad}{update};");
                            break;
                        case ReturnStatementSyntax ret:
                            if (ret.Expression != null)
                                sb.AppendLine($"{pad}return {_owner.TranslateExpression(ret.Expression, _function, _function.ReturnType)};");
                            else
                                sb.AppendLine($"{pad}return;");
                            break;
                        case IfStatementSyntax ifStmt:
                            sb.AppendLine($"{pad}if ({_owner.TranslateExpression(ifStmt.Condition, _function, "bool")}) {{");
                            WriteStatement(sb, ifStmt.Statement, indent + 1);
                            sb.AppendLine($"{pad}}}");
                            if (ifStmt.Else != null)
                            {
                                sb.AppendLine($"{pad}else {{");
                                WriteStatement(sb, ifStmt.Else.Statement, indent + 1);
                                sb.AppendLine($"{pad}}}");
                            }
                            break;
                        case WhileStatementSyntax whileStmt:
                            sb.AppendLine($"{pad}while ({_owner.TranslateExpression(whileStmt.Condition, _function, "bool")}) {{");
                            WriteStatement(sb, whileStmt.Statement, indent + 1);
                            sb.AppendLine($"{pad}}}");
                            break;
                        case ForStatementSyntax forStmt:
                            var init = forStmt.Declaration != null
                                ? string.Join(", ", forStmt.Declaration.Variables.Select(v => v.Initializer == null ? string.Empty : $"{Sanitize(v.Identifier.Text)} = {_owner.TranslateExpression(v.Initializer.Value, _function, forStmt.Declaration.Type.ToString())}"))
                                : string.Join(", ", forStmt.Initializers.Select(ExpressionToStatement));
                            var cond = forStmt.Condition == null ? "true" : _owner.TranslateExpression(forStmt.Condition, _function, "bool");
                            var inc = string.Join(", ", forStmt.Incrementors.Select(ExpressionToStatement));
                            sb.AppendLine($"{pad}for ({init}; {cond}; {inc}) {{");
                            WriteStatement(sb, forStmt.Statement, indent + 1);
                            sb.AppendLine($"{pad}}}");
                            break;
                        default:
                            _owner._assumptions.Add(new TranslationAssumption
                            {
                                Severity = AssumptionSeverity.Warning,
                                Category = "CodeBlockUnsupported",
                                Location = _function.Signature,
                                SymbolName = statement.Kind().ToString(),
                                Message = $"Statement '{statement.Kind()}' was not emitted inside UPPAAL function body."
                            });
                            break;
                    }
                }

                private string ExpressionToStatement(ExpressionSyntax expression)
                {
                    if (expression is PostfixUnaryExpressionSyntax postfix)
                    {
                        var variable = _owner.TranslateExpression(postfix.Operand, _function, "int");
                        if (postfix.IsKind(SyntaxKind.PostIncrementExpression)) return $"{variable} = {variable} + 1";
                        if (postfix.IsKind(SyntaxKind.PostDecrementExpression)) return $"{variable} = {variable} - 1";
                    }

                    if (expression is PrefixUnaryExpressionSyntax prefix)
                    {
                        var variable = _owner.TranslateExpression(prefix.Operand, _function, "int");
                        if (prefix.IsKind(SyntaxKind.PreIncrementExpression)) return $"{variable} = {variable} + 1";
                        if (prefix.IsKind(SyntaxKind.PreDecrementExpression)) return $"{variable} = {variable} - 1";
                    }

                    if (expression is AssignmentExpressionSyntax assignment)
                    {
                        var left = _owner.TranslateExpression(assignment.Left, _function, "int");
                        var right = _owner.TranslateExpression(assignment.Right, _function, "int");
                        return assignment.Kind() switch
                        {
                            SyntaxKind.AddAssignmentExpression => $"{left} = {left} + {right}",
                            SyntaxKind.SubtractAssignmentExpression => $"{left} = {left} - {right}",
                            SyntaxKind.MultiplyAssignmentExpression => $"{left} = {left} * {right}",
                            SyntaxKind.DivideAssignmentExpression => $"{left} = {left} / {right}",
                            SyntaxKind.ModuloAssignmentExpression => $"{left} = {left} % {right}",
                            _ => $"{left} = {right}"
                        };
                    }

                    if (expression is InvocationExpressionSyntax invocation)
                        return _owner.TranslateInvocation(invocation, _function, "void", out _);

                    return string.Empty;
                }
            }
        }
    }

    public class UppaalGenerationException : Exception
    {
        public UppaalGenerationException(string message) : base(message) { }
        public UppaalGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
