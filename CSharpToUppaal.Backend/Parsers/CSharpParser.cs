using CSharpToUppaal.Backend.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CSharpToUppaal.Backend.Parsers
{
    public interface ICSharpParserService
    {
        Task<ParseResult> ParseSourceFileAsync(string filePath);
        Task<ParseResult> ParseSourceCodeAsync(string code, string language = "C#");
        Task<MethodInfo> AnalyzeMethodAsync(MethodDeclarationSyntax method);
        Task<ComplexityMetrics> CalculateComplexityAsync(string code);
    }

    public class CSharpParser : ICSharpParserService
    {
        public async Task<ParseResult> ParseSourceFileAsync(string filePath)
        {
            try
            {
                Console.WriteLine($"Parsing source file: {filePath}");

                var code = await File.ReadAllTextAsync(filePath);
                return await ParseSourceCodeAsync(code);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing source file: {filePath}: {ex.Message}");
                return new ParseResult
                {
                    FilePath = filePath,
                    Success = false,
                    Errors = new List<ParseError>
                    {
                        new ParseError
                        {
                            Message = ex.Message,
                            LineNumber = 0,
                            Severity = ParseErrorSeverity.Error
                        }
                    }
                };
            }
        }

        public async Task<ParseResult> ParseSourceCodeAsync(string code, string language = "C#")
        {
            try
            {
                Console.WriteLine($"Parsing source code, length: {code?.Length ?? 0}");

                var parseResult = new ParseResult
                {
                    Success = true,
                    Errors = new List<ParseError>(),
                    Methods = new List<MethodInfo>(),
                    Classes = new List<ClassInfo>()
                };

                if (string.IsNullOrWhiteSpace(code))
                {
                    parseResult.Success = false;
                    parseResult.Errors.Add(new ParseError
                    {
                        Message = "Empty source code",
                        Severity = ParseErrorSeverity.Error
                    });
                    return parseResult;
                }

                var tree = CSharpSyntaxTree.ParseText(code);
                var root = await tree.GetRootAsync();

                // Create a simple compilation to get semantic model
                var compilation = CSharpCompilation.Create("TempAssembly")
                    .AddReferences(
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location)
                    )
                    .AddSyntaxTrees(tree);

                var semanticModel = compilation.GetSemanticModel(tree);

                // Extract classes
                ExtractClasses(root, semanticModel, parseResult);

                // Extract methods
                ExtractMethods(root, semanticModel, parseResult);

                Console.WriteLine($"Parsed {parseResult.Methods.Count} methods and {parseResult.Classes.Count} classes");

                return parseResult;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing source code: {ex.Message}");
                return new ParseResult
                {
                    Success = false,
                    Errors = new List<ParseError>
                    {
                        new ParseError
                        {
                            Message = ex.Message,
                            LineNumber = 0,
                            Severity = ParseErrorSeverity.Error
                        }
                    }
                };
            }
        }

        private void ExtractClasses(SyntaxNode root, SemanticModel semanticModel, ParseResult parseResult)
        {
            var classDeclarations = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(classDecl);
                if (symbol == null) continue;

                var classInfo = new ClassInfo
                {
                    Name = symbol.Name,
                    FullName = symbol.ToDisplayString(),
                    IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
                    IsAbstract = symbol.IsAbstract,
                    IsSealed = symbol.IsSealed,
                    BaseTypes = symbol.BaseType?.ToDisplayString(),
                    Location = classDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                };

                // Extract properties
                var properties = classDecl.DescendantNodes()
                    .OfType<PropertyDeclarationSyntax>();
                foreach (var prop in properties)
                {
                    var propSymbol = semanticModel.GetDeclaredSymbol(prop);
                    if (propSymbol != null)
                    {
                        classInfo.Properties.Add(new PropertyInfo
                        {
                            Name = propSymbol.Name,
                            Type = propSymbol.Type.ToDisplayString(),
                            HasGetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "get") == true,
                            HasSetter = prop.AccessorList?.Accessors.Any(a => a.Keyword.Text == "set") == true
                        });
                    }
                }

                parseResult.Classes.Add(classInfo);
            }
        }

        private void ExtractMethods(SyntaxNode root, SemanticModel semanticModel, ParseResult parseResult)
        {
            var methodDeclarations = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>();

            foreach (var methodDecl in methodDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(methodDecl);
                if (symbol == null) continue;

                var methodInfo = new MethodInfo
                {
                    Name = symbol.Name,
                    ReturnType = symbol.ReturnType.ToDisplayString(),
                    IsPublic = symbol.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = symbol.IsStatic,
                    IsAsync = methodDecl.Modifiers.Any(m => m.Text == "async"),
                    LineNumber = methodDecl.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    Body = methodDecl.Body?.ToString() ?? methodDecl.ExpressionBody?.ToString()
                };

                // Extract parameters
                foreach (var param in symbol.Parameters)
                {
                    methodInfo.Parameters.Add(new ParameterInfo
                    {
                        Name = param.Name,
                        Type = param.Type.ToDisplayString(),
                        HasDefaultValue = param.HasExplicitDefaultValue,
                        DefaultValue = param.ExplicitDefaultValue?.ToString()
                    });
                }

                // Calculate complexity metrics
                methodInfo.Complexity = CalculateComplexity(methodDecl);
                methodInfo.LinesOfCode = CalculateLinesOfCode(methodDecl);

                parseResult.Methods.Add(methodInfo);
            }
        }

        public async Task<MethodInfo> AnalyzeMethodAsync(MethodDeclarationSyntax method)
        {
            return await Task.Run(() =>
            {
                var methodInfo = new MethodInfo
                {
                    Name = method.Identifier.Text,
                    ReturnType = method.ReturnType.ToString(),
                    IsPublic = method.Modifiers.Any(m => m.Text == "public"),
                    IsStatic = method.Modifiers.Any(m => m.Text == "static"),
                    IsAsync = method.Modifiers.Any(m => m.Text == "async"),
                    Body = method.Body?.ToString() ?? method.ExpressionBody?.ToString(),
                    LinesOfCode = CalculateLinesOfCode(method)
                };

                methodInfo.Complexity = CalculateComplexity(method);

                return methodInfo;
            });
        }

        private ComplexityMetrics CalculateComplexity(MethodDeclarationSyntax method)
        {
            var visitor = new ComplexityVisitor();
            visitor.Visit(method);
            return visitor.GetMetrics();
        }

        private int CalculateLinesOfCode(MethodDeclarationSyntax method)
        {
            var body = method.Body;
            if (body == null)
            {
                var exprBody = method.ExpressionBody;
                return exprBody != null ? 1 : 0;
            }

            var lines = body.ToString().Split('\n');
            return lines.Count(line => !string.IsNullOrWhiteSpace(line));
        }

        public async Task<ComplexityMetrics> CalculateComplexityAsync(string code)
        {
            return await Task.Run(() =>
            {
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();

                var methodDeclarations = root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>();

                var totalMetrics = new ComplexityMetrics();

                foreach (var method in methodDeclarations)
                {
                    var metrics = CalculateComplexity(method);
                    totalMetrics.CyclomaticComplexity += metrics.CyclomaticComplexity;
                    totalMetrics.LinesOfCode += metrics.LinesOfCode;
                    totalMetrics.ParameterCount += metrics.ParameterCount;
                    totalMetrics.NestingDepth = Math.Max(totalMetrics.NestingDepth, metrics.NestingDepth);
                    totalMetrics.MethodCalls += metrics.MethodCalls;
                }

                return totalMetrics;
            });
        }

        private class ComplexityVisitor : CSharpSyntaxWalker
        {
            private int _cyclomaticComplexity = 1;
            private int _nestingDepth = 0;
            private int _maxNestingDepth = 0;
            private int _methodCalls = 0;
            private int _parameterCount = 0;

            public override void VisitIfStatement(IfStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitIfStatement(node);
                _nestingDepth--;
            }

            public override void VisitWhileStatement(WhileStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitWhileStatement(node);
                _nestingDepth--;
            }

            public override void VisitForStatement(ForStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitForStatement(node);
                _nestingDepth--;
            }

            public override void VisitForEachStatement(ForEachStatementSyntax node)
            {
                _cyclomaticComplexity++;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitForEachStatement(node);
                _nestingDepth--;
            }

            public override void VisitSwitchStatement(SwitchStatementSyntax node)
            {
                var sectionsCount = node.Sections.Count;
                _cyclomaticComplexity += sectionsCount;
                _nestingDepth++;
                _maxNestingDepth = Math.Max(_maxNestingDepth, _nestingDepth);
                base.VisitSwitchStatement(node);
                _nestingDepth--;
            }

            public override void VisitConditionalExpression(ConditionalExpressionSyntax node)
            {
                _cyclomaticComplexity++;
                base.VisitConditionalExpression(node);
            }

            public override void VisitBinaryExpression(BinaryExpressionSyntax node)
            {
                if (node.OperatorToken.Text == "&&" || node.OperatorToken.Text == "||")
                {
                    _cyclomaticComplexity++;
                }
                base.VisitBinaryExpression(node);
            }

            public override void VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                _methodCalls++;
                base.VisitInvocationExpression(node);
            }

            public override void VisitParameter(ParameterSyntax node)
            {
                _parameterCount++;
                base.VisitParameter(node);
            }

            public ComplexityMetrics GetMetrics()
            {
                return new ComplexityMetrics
                {
                    CyclomaticComplexity = _cyclomaticComplexity,
                    NestingDepth = _maxNestingDepth,
                    MethodCalls = _methodCalls,
                    ParameterCount = _parameterCount
                };
            }
        }
    }
}