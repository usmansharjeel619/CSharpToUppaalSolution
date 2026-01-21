using CSharpToUppaal.Backend.Models;
using CSharpToUppaal.Backend.Parsers;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

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
                trueExitId = await ProcessStatementAsync(ifStmt.Statement, cfg, conditionNode.Id);
            }

            // Process false branch
            string falseExitId = null;
            if (ifStmt.Else != null)
            {
                falseExitId = await ProcessStatementAsync(ifStmt.Else.Statement, cfg, conditionNode.Id);
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

            // Connect body back to condition (true condition)
            if (!string.IsNullOrEmpty(bodyExitId))
            {
                AddEdge(cfg, bodyExitId, conditionNode.Id, "true");
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

            // Connect back to condition
            if (!string.IsNullOrEmpty(afterIncrementId))
                AddEdge(cfg, afterIncrementId, conditionNode.Id, "loop");

            // Create exit node
            var exitNode = new CfgNode
            {
                Label = "For Exit",
                Type = NodeType.Statement
            };
            cfg.Nodes.Add(exitNode);
            AddEdge(cfg, conditionNode.Id, exitNode.Id, "exit");

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
            var node = new CfgNode
            {
                Label = "Expression",
                Type = NodeType.Statement,
                Code = exprStmt.Expression.ToString()
            };
            cfg.Nodes.Add(node);
            AddEdge(cfg, previousNodeId, node.Id);
            return node.Id;
        }

        private string ProcessLocalDeclaration(LocalDeclarationStatementSyntax localDecl, ControlFlowGraph cfg, string previousNodeId)
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