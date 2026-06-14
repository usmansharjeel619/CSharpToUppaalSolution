using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Parsers;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

#nullable disable
namespace CSharpToUppaal.Backend.Generators
{
    public interface ICfgGeneratorService
    {
        Task<ControlFlowGraph> GenerateCfgFromMethodAsync(MethodInfo method);
        Task<ControlFlowGraph> GenerateCfgFromCodeAsync(string code);
        Task<List<ControlFlowGraph>> GenerateCfgsForFileAsync(string filePath);
    }

    public class CfgGeneratorService : ICfgGeneratorService
    {
        private readonly ICSharpParserService _parser;

        public CfgGeneratorService(ICSharpParserService parser)
        {
            _parser = parser;
        }

        public async Task<ControlFlowGraph> GenerateCfgFromMethodAsync(MethodInfo method)
        {
            try
            {
                Console.WriteLine($"Generating CFG for method: {method.Name}");

                var cfg = new ControlFlowGraph
                {
                    MethodName = method.Name,
                    ReturnType = method.ReturnType
                };

                if (string.IsNullOrEmpty(method.Body))
                {
                    Console.WriteLine($"Warning: Method {method.Name} has no body");
                    throw new InvalidOperationException($"Method {method.Name} has no body to generate CFG from");
                }

                // Parse method body - it's just a block statement, so parse it directly
                var tree = CSharpSyntaxTree.ParseText(method.Body);
                var root = await tree.GetRootAsync();

                // Try to get BlockSyntax directly
                var blockNode = root.DescendantNodes()
                    .OfType<BlockSyntax>()
                    .FirstOrDefault();

                BlockSyntax body;
                
                if (blockNode != null)
                {
                    // Direct block found
                    body = blockNode;
                }
                else
                {
                    // Maybe it's a complete method declaration, try that
                    var methodNode = root.DescendantNodes()
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault();

                    if (methodNode?.Body != null)
                    {
                        body = methodNode.Body;
                    }
                    else
                    {
                        Console.WriteLine($"Warning: Could not find method body syntax for {method.Name}");
                        throw new InvalidOperationException($"Could not parse method body for {method.Name}");
                    }
                }

                // Create entry node
                var entryNode = new CfgNode
                {
                    Label = "Entry",
                    Type = NodeType.Entry,
                    Code = $"Start {method.Name}"
                };
                cfg.Nodes.Add(entryNode);
                cfg.EntryNodeId = entryNode.Id;

                // Process method body
                if (body != null && body.Statements.Any())
                {
                    var lastNodeId = await ProcessBlockAsync(body, cfg, entryNode.Id);

                    // Create exit node
                    var exitNode = new CfgNode
                    {
                        Label = "Exit",
                        Type = NodeType.Exit,
                        Code = $"Return from {method.Name}"
                    };
                    cfg.Nodes.Add(exitNode);
                    cfg.ExitNodeId = exitNode.Id;

                    if (!string.IsNullOrEmpty(lastNodeId))
                    {
                        AddEdge(cfg, lastNodeId, exitNode.Id);
                    }
                    else
                    {
                        AddEdge(cfg, entryNode.Id, exitNode.Id);
                    }
                }
                else
                {
                    // Empty method body - connect entry directly to exit
                    var exitNode = new CfgNode
                    {
                        Label = "Exit",
                        Type = NodeType.Exit,
                        Code = $"Return from {method.Name}"
                    };
                    cfg.Nodes.Add(exitNode);
                    cfg.ExitNodeId = exitNode.Id;
                    AddEdge(cfg, entryNode.Id, exitNode.Id);
                }

                // Extract variables from parameters
                foreach (var param in method.Parameters)
                {
                    cfg.Variables[param.Name] = param.Type;
                }

                Console.WriteLine($"Generated CFG for {method.Name} with {cfg.Nodes.Count} nodes");

                return cfg;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error generating CFG for method: {method.Name}: {ex.Message}");
                throw new CfgGenerationException($"Failed to generate CFG for method {method.Name}", ex);
            }
        }

        public async Task<ControlFlowGraph> GenerateCfgFromCodeAsync(string code)
        {
            var parseResult = await _parser.ParseSourceCodeAsync(code);
            if (!parseResult.Methods.Any())
            {
                throw new InvalidOperationException("No methods found in code");
            }

            var firstMethod = parseResult.Methods.First();
            return await GenerateCfgFromMethodAsync(firstMethod);
        }

        public async Task<List<ControlFlowGraph>> GenerateCfgsForFileAsync(string filePath)
        {
            var parseResult = await _parser.ParseSourceFileAsync(filePath);
            if (!parseResult.Success)
            {
                throw new InvalidOperationException($"Failed to parse file: {string.Join(", ", parseResult.Errors.Select(e => e.Message))}");
            }

            var cfgs = new List<ControlFlowGraph>();
            foreach (var method in parseResult.Methods)
            {
                var cfg = await GenerateCfgFromMethodAsync(method);
                cfgs.Add(cfg);
            }

            return cfgs;
        }

        private HashSet<string> _knownMethodNames = new();

        /// <summary>
        /// Sets the known method names so that method calls to sibling methods
        /// can be detected and represented as MethodCall CFG nodes.
        /// </summary>
        public void SetKnownMethodNames(IEnumerable<string> names)
        {
            _knownMethodNames = new HashSet<string>(names);
        }

        private async Task<string> ProcessBlockAsync(BlockSyntax block, ControlFlowGraph cfg, string previousNodeId)
        {
            string currentNodeId = previousNodeId;

            foreach (var statement in block.Statements)
            {
                currentNodeId = await ProcessStatementAsync(statement, cfg, currentNodeId);
            }

            return currentNodeId;
        }

        private async Task<string> ProcessStatementAsync(StatementSyntax statement, ControlFlowGraph cfg, string previousNodeId)
        {
            return statement switch
            {
                IfStatementSyntax ifStmt => await ProcessIfStatementAsync(ifStmt, cfg, previousNodeId),
                WhileStatementSyntax whileStmt => await ProcessWhileStatementAsync(whileStmt, cfg, previousNodeId),
                ForStatementSyntax forStmt => await ProcessForStatementAsync(forStmt, cfg, previousNodeId),
                DoStatementSyntax doStmt => await ProcessDoWhileStatementAsync(doStmt, cfg, previousNodeId),
                ReturnStatementSyntax returnStmt => ProcessReturnStatement(returnStmt, cfg, previousNodeId),
                ExpressionStatementSyntax exprStmt => ProcessExpressionStatement(exprStmt, cfg, previousNodeId),
                LocalDeclarationStatementSyntax localDecl => ProcessLocalDeclaration(localDecl, cfg, previousNodeId),
                BlockSyntax block => await ProcessBlockAsync(block, cfg, previousNodeId),
                _ => ProcessGenericStatement(statement, cfg, previousNodeId)
            };
        }

        private async Task<string> ProcessIfStatementAsync(IfStatementSyntax ifStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            // Create condition node
            var conditionNode = new CfgNode
            {
                Label = "If Condition",
                Type = NodeType.Condition,
                Code = ifStmt.Condition.ToString(),
                Properties = { ["condition"] = ifStmt.Condition.ToString() }
            };
            cfg.Nodes.Add(conditionNode);
            AddEdge(cfg, previousNodeId, conditionNode.Id);

            // Process true branch
            string trueExitId = null;
            if (ifStmt.Statement != null)
            {
                // First add the edge from condition to the true branch with "true" label
                var trueBranchFirst = await ProcessStatementAsync(ifStmt.Statement, cfg, conditionNode.Id);
                trueExitId = trueBranchFirst;
                
                // Label the edge from condition to the first node of the true branch
                var trueEdge = cfg.Edges.FirstOrDefault(e => e.FromNodeId == conditionNode.Id && e.ToNodeId != conditionNode.Id && string.IsNullOrEmpty(e.Label));
                if (trueEdge != null)
                    trueEdge.Label = "true";
            }

            // Process false branch
            string falseExitId = null;
            if (ifStmt.Else != null)
            {
                falseExitId = await ProcessStatementAsync(ifStmt.Else.Statement, cfg, conditionNode.Id);
                
                // Label the edge from condition to the first node of the false branch
                var falseEdge = cfg.Edges.LastOrDefault(e => e.FromNodeId == conditionNode.Id && string.IsNullOrEmpty(e.Label));
                if (falseEdge != null)
                    falseEdge.Label = "false";
            }

            // Create merge node
            var mergeNode = new CfgNode
            {
                Label = "Merge",
                Type = NodeType.Merge
            };
            cfg.Nodes.Add(mergeNode);

            // Connect branches
            if (!string.IsNullOrEmpty(trueExitId))
                AddEdge(cfg, trueExitId, mergeNode.Id);
            if (!string.IsNullOrEmpty(falseExitId))
                AddEdge(cfg, falseExitId, mergeNode.Id);
            if (string.IsNullOrEmpty(falseExitId))
                AddEdge(cfg, conditionNode.Id, mergeNode.Id, "false");

            return mergeNode.Id;
        }

        private async Task<string> ProcessWhileStatementAsync(WhileStatementSyntax whileStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            // Create condition node
            var conditionNode = new CfgNode
            {
                Label = "While Condition",
                Type = NodeType.Loop,
                Code = whileStmt.Condition.ToString(),
                Properties = { ["condition"] = whileStmt.Condition.ToString() }
            };
            cfg.Nodes.Add(conditionNode);
            AddEdge(cfg, previousNodeId, conditionNode.Id);

            // Process loop body
            string bodyExitId = null;
            if (whileStmt.Statement != null)
            {
                bodyExitId = await ProcessStatementAsync(whileStmt.Statement, cfg, conditionNode.Id);
                
                // Label the edge from condition to the first node of the body as "true"
                var bodyEdge = cfg.Edges.FirstOrDefault(e => e.FromNodeId == conditionNode.Id && string.IsNullOrEmpty(e.Label));
                if (bodyEdge != null)
                    bodyEdge.Label = "true";
            }

            // Create loop exit node
            var exitNode = new CfgNode
            {
                Label = "Loop Exit",
                Type = NodeType.Statement
            };
            cfg.Nodes.Add(exitNode);

            // Connect condition to exit (false condition)
            AddEdge(cfg, conditionNode.Id, exitNode.Id, "false");

            // Connect body back to condition (loop back)
            if (!string.IsNullOrEmpty(bodyExitId))
            {
                AddEdge(cfg, bodyExitId, conditionNode.Id);
            }

            return exitNode.Id;
        }

        private async Task<string> ProcessForStatementAsync(ForStatementSyntax forStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            // Process initializers
            string currentId = previousNodeId;
            if (forStmt.Declaration != null || forStmt.Initializers.Any())
            {
                var initNode = new CfgNode
                {
                    Label = "For Initializer",
                    Type = NodeType.Declaration,
                    Code = forStmt.Declaration?.ToString() ??
                          string.Join(", ", forStmt.Initializers)
                };
                cfg.Nodes.Add(initNode);
                AddEdge(cfg, currentId, initNode.Id);
                currentId = initNode.Id;

                // Extract for-loop variable declarations for UPPAAL
                if (forStmt.Declaration != null)
                {
                    string typeName = forStmt.Declaration.Type.ToString();
                    foreach (var variable in forStmt.Declaration.Variables)
                    {
                        cfg.Variables[variable.Identifier.Text] = typeName;
                    }
                }
            }

            // Create condition node
            var conditionNode = new CfgNode
            {
                Label = "For Condition",
                Type = NodeType.Loop,
                Code = forStmt.Condition?.ToString() ?? "true",
                Properties = { ["condition"] = forStmt.Condition?.ToString() ?? "true" }
            };
            cfg.Nodes.Add(conditionNode);
            AddEdge(cfg, currentId, conditionNode.Id);

            // Process body
            string bodyExitId = null;
            if (forStmt.Statement != null)
            {
                bodyExitId = await ProcessStatementAsync(forStmt.Statement, cfg, conditionNode.Id);
                
                // Label the edge from condition to the first node of the body as "true"
                var bodyEdge = cfg.Edges.FirstOrDefault(e => e.FromNodeId == conditionNode.Id && string.IsNullOrEmpty(e.Label));
                if (bodyEdge != null)
                    bodyEdge.Label = "true";
            }

            // Process incrementors
            string afterIncrementId = null;
            if (forStmt.Incrementors.Any())
            {
                var incrementNode = new CfgNode
                {
                    Label = "For Increment",
                    Type = NodeType.Statement,
                    Code = string.Join(", ", forStmt.Incrementors)
                };
                cfg.Nodes.Add(incrementNode);

                if (!string.IsNullOrEmpty(bodyExitId))
                    AddEdge(cfg, bodyExitId, incrementNode.Id);

                afterIncrementId = incrementNode.Id;
            }
            else
            {
                afterIncrementId = bodyExitId;
            }

            // Connect back to condition (loop back, no label needed - it's unconditional)
            if (!string.IsNullOrEmpty(afterIncrementId))
                AddEdge(cfg, afterIncrementId, conditionNode.Id);

            // Create exit node
            var exitNode = new CfgNode
            {
                Label = "For Exit",
                Type = NodeType.Statement
            };
            cfg.Nodes.Add(exitNode);
            AddEdge(cfg, conditionNode.Id, exitNode.Id, "false");

            return exitNode.Id;
        }

        private async Task<string> ProcessDoWhileStatementAsync(DoStatementSyntax doStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            // Process body first
            string bodyExitId = await ProcessStatementAsync(doStmt.Statement, cfg, previousNodeId);

            // Create condition node
            var conditionNode = new CfgNode
            {
                Label = "DoWhile Condition",
                Type = NodeType.Loop,
                Code = doStmt.Condition.ToString(),
                Properties = { ["condition"] = doStmt.Condition.ToString() }
            };
            cfg.Nodes.Add(conditionNode);
            AddEdge(cfg, bodyExitId, conditionNode.Id);

            // Create exit node
            var exitNode = new CfgNode
            {
                Label = "DoWhile Exit",
                Type = NodeType.Statement
            };
            cfg.Nodes.Add(exitNode);

            // Connect condition to exit (false condition)
            AddEdge(cfg, conditionNode.Id, exitNode.Id, "false");

            // Connect condition back to body start (true condition)
            var bodyStartNode = cfg.Nodes.FirstOrDefault(n => n.Id == bodyExitId);
            if (bodyStartNode != null)
            {
                AddEdge(cfg, conditionNode.Id, bodyStartNode.Id, "true");
            }

            return exitNode.Id;
        }

        private string ProcessReturnStatement(ReturnStatementSyntax returnStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            var returnNode = new CfgNode
            {
                Label = "Return",
                Type = NodeType.Return,
                Code = returnStmt.ToString()
            };
            cfg.Nodes.Add(returnNode);
            AddEdge(cfg, previousNodeId, returnNode.Id);
            return returnNode.Id;
        }

        private string ProcessExpressionStatement(ExpressionStatementSyntax exprStmt, ControlFlowGraph cfg, string previousNodeId)
        {
            // Check if this expression is a method call to a known sibling method
            if (exprStmt.Expression is InvocationExpressionSyntax invocation)
            {
                string calledName = null;
                if (invocation.Expression is IdentifierNameSyntax id)
                    calledName = id.Identifier.Text;
                else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                    calledName = memberAccess.Name.Identifier.Text;

                if (calledName != null && _knownMethodNames.Contains(calledName))
                {
                    var node = new CfgNode
                    {
                        Label = $"Call {calledName}",
                        Type = NodeType.MethodCall,
                        Code = exprStmt.Expression.ToString()
                    };
                    node.Properties["calledMethod"] = calledName;
                    // Store argument expressions for shared variable passing
                    var args = invocation.ArgumentList.Arguments;
                    for (int i = 0; i < args.Count; i++)
                    {
                        node.Properties[$"arg{i}"] = args[i].Expression.ToString();
                    }
                    node.Properties["argCount"] = args.Count;
                    cfg.Nodes.Add(node);
                    AddEdge(cfg, previousNodeId, node.Id);
                    return node.Id;
                }
            }

            // Check if this is an assignment whose RHS is a method call to a known sibling method
            if (exprStmt.Expression is AssignmentExpressionSyntax assignment &&
                assignment.Right is InvocationExpressionSyntax rhsInvocation)
            {
                string calledName = null;
                if (rhsInvocation.Expression is IdentifierNameSyntax rhsId)
                    calledName = rhsId.Identifier.Text;
                else if (rhsInvocation.Expression is MemberAccessExpressionSyntax rhsMemberAccess)
                    calledName = rhsMemberAccess.Name.Identifier.Text;

                if (calledName != null && _knownMethodNames.Contains(calledName))
                {
                    var node = new CfgNode
                    {
                        Label = $"Call {calledName}",
                        Type = NodeType.MethodCall,
                        Code = exprStmt.Expression.ToString()
                    };
                    node.Properties["calledMethod"] = calledName;
                    node.Properties["assignTarget"] = assignment.Left.ToString();
                    var args = rhsInvocation.ArgumentList.Arguments;
                    for (int i = 0; i < args.Count; i++)
                    {
                        node.Properties[$"arg{i}"] = args[i].Expression.ToString();
                    }
                    node.Properties["argCount"] = args.Count;
                    cfg.Nodes.Add(node);
                    AddEdge(cfg, previousNodeId, node.Id);
                    return node.Id;
                }
            }

            var exprNode = new CfgNode
            {
                Label = "Expression",
                Type = NodeType.Statement,
                Code = exprStmt.Expression.ToString()
            };
            cfg.Nodes.Add(exprNode);
            AddEdge(cfg, previousNodeId, exprNode.Id);
            return exprNode.Id;
        }

        private string ProcessLocalDeclaration(LocalDeclarationStatementSyntax localDecl, ControlFlowGraph cfg, string previousNodeId)
        {
            // Check if the initializer is a method call to a known sibling method
            var declaration = localDecl.Declaration;
            string typeName = declaration.Type.ToString();
            
            foreach (var variable in declaration.Variables)
            {
                cfg.Variables[variable.Identifier.Text] = typeName;

                if (variable.Initializer?.Value is InvocationExpressionSyntax invocation)
                {
                    string calledName = null;
                    if (invocation.Expression is IdentifierNameSyntax id)
                        calledName = id.Identifier.Text;
                    else if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                        calledName = memberAccess.Name.Identifier.Text;

                    if (calledName != null && _knownMethodNames.Contains(calledName))
                    {
                        var callNode = new CfgNode
                        {
                            Label = $"Call {calledName}",
                            Type = NodeType.MethodCall,
                            Code = $"{variable.Identifier.Text} = {invocation}"
                        };
                        callNode.Properties["calledMethod"] = calledName;
                        callNode.Properties["assignTarget"] = variable.Identifier.Text;
                        var args = invocation.ArgumentList.Arguments;
                        for (int i = 0; i < args.Count; i++)
                        {
                            callNode.Properties[$"arg{i}"] = args[i].Expression.ToString();
                        }
                        callNode.Properties["argCount"] = args.Count;
                        cfg.Nodes.Add(callNode);
                        AddEdge(cfg, previousNodeId, callNode.Id);
                        previousNodeId = callNode.Id;
                        continue; // skip the normal declaration node for this variable
                    }
                }
            }

            // If all variables were method calls, we've already handled them
            // Only create a declaration node if there are non-method-call initializers
            bool hasNonCallVars = false;
            foreach (var variable in declaration.Variables)
            {
                if (variable.Initializer?.Value is InvocationExpressionSyntax inv2)
                {
                    string cn = null;
                    if (inv2.Expression is IdentifierNameSyntax id2) cn = id2.Identifier.Text;
                    else if (inv2.Expression is MemberAccessExpressionSyntax ma2) cn = ma2.Name.Identifier.Text;
                    if (cn != null && _knownMethodNames.Contains(cn)) continue;
                }
                hasNonCallVars = true;
                break;
            }

            if (hasNonCallVars)
            {
                var node = new CfgNode
                {
                    Label = "Declaration",
                    Type = NodeType.Declaration,
                    Code = localDecl.ToString()
                };
                cfg.Nodes.Add(node);
                AddEdge(cfg, previousNodeId, node.Id);
                return node.Id;
            }

            return previousNodeId;
        }

        private string ProcessGenericStatement(StatementSyntax statement, ControlFlowGraph cfg, string previousNodeId)
        {
            var node = new CfgNode
            {
                Label = statement.GetType().Name.Replace("StatementSyntax", ""),
                Type = NodeType.Statement,
                Code = statement.ToString()
            };
            cfg.Nodes.Add(node);
            AddEdge(cfg, previousNodeId, node.Id);
            return node.Id;
        }

        private void AddEdge(ControlFlowGraph cfg, string fromId, string toId, string label = "")
        {
            var edge = new CfgEdge
            {
                FromNodeId = fromId,
                ToNodeId = toId,
                Label = label
            };
            cfg.Edges.Add(edge);
        }
    }

    public class CfgGenerationException : Exception
    {
        public CfgGenerationException(string message) : base(message) { }
        public CfgGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
