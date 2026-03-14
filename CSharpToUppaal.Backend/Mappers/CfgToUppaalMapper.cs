using System;
using System.Collections.Generic;
using System.Linq;
using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.Backend.Mappers
{
    public interface ICfgToUppaalMapper
    {
        UppaalTemplate Map(ControlFlowGraph cfg);
    }

    public class CfgToUppaalMapper : ICfgToUppaalMapper
    {
        public UppaalTemplate Map(ControlFlowGraph cfg)
        {
            var template = new UppaalTemplate
            {
                Name = cfg.MethodName
            };

            // Generate declarations
            template.Declaration = GenerateDeclarations(cfg);

            // Map CFG nodes to UPPAAL locations
            var locationMap = new Dictionary<string, string>();
            var usedNames = new Dictionary<string, int>();
            foreach (var node in cfg.Nodes)
            {
                var location = MapNodeToLocation(node);

                // Ensure unique location names (UPPAAL 4.1 requires unique names per template)
                if (usedNames.ContainsKey(location.Name))
                {
                    usedNames[location.Name]++;
                    location.Name = $"{location.Name}_{usedNames[location.Name]}";
                }
                else
                {
                    usedNames[location.Name] = 0;
                }

                template.Locations.Add(location);
                locationMap[node.Id] = location.Id;
            }

            // Map CFG edges to UPPAAL transitions
            foreach (var edge in cfg.Edges)
            {
                if (locationMap.ContainsKey(edge.FromNodeId) && locationMap.ContainsKey(edge.ToNodeId))
                {
                    var fromNode = cfg.Nodes.FirstOrDefault(n => n.Id == edge.FromNodeId);
                    var transition = MapEdgeToTransition(edge, fromNode, locationMap);
                    template.Transitions.Add(transition);
                }
            }

            // Set initial location
            var entryLocation = template.Locations.FirstOrDefault(l => l.Name == "Entry");
            if (entryLocation != null)
            {
                entryLocation.IsInitial = true;
            }

            return template;
        }

        private string GenerateDeclarations(ControlFlowGraph cfg)
        {
            var declarations = new System.Text.StringBuilder();

            declarations.AppendLine($"// Function: {cfg.MethodName}");
            declarations.AppendLine($"// Return type: {cfg.ReturnType}");
            declarations.AppendLine();

            // Declare variables with UPPAAL-compatible types
            foreach (var variable in cfg.Variables)
            {
                string uppaalType = MapCSharpTypeToUppaal(variable.Value);
                if (!string.IsNullOrEmpty(uppaalType))
                {
                    declarations.AppendLine($"{uppaalType} {SanitizeVariableName(variable.Key)};");
                }
            }

            return declarations.ToString();
        }

        private string MapCSharpTypeToUppaal(string csharpType)
        {
            // Map C# types to UPPAAL types
            switch (csharpType?.ToLower())
            {
                case "int":
                case "int32":
                case "int16":
                case "int64":
                case "short":
                case "long":
                case "byte":
                case "sbyte":
                case "uint":
                case "ushort":
                case "ulong":
                    return "int";
                case "bool":
                case "boolean":
                    return "bool";
                case "double":
                case "float":
                case "decimal":
                    // UPPAAL doesn't support floating point; use int as approximation
                    return "int";
                case "char":
                    return "int"; // represent as int
                case "string":
                    // UPPAAL doesn't support strings; skip or use int
                    return "int";
                default:
                    return "int"; // default to int for unknown types
            }
        }

        private string SanitizeVariableName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "var";
            var sanitized = new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]) && sanitized[0] != '_')
                sanitized = "_" + sanitized;
            if (string.IsNullOrEmpty(sanitized))
                return "var";
            return sanitized;
        }

        private UppaalLocation MapNodeToLocation(CfgNode node)
        {
            var location = new UppaalLocation
            {
                Id = $"loc_{node.Id.Replace("-", "_")}",
                Name = GetLocationName(node, node.Code),
                IsUrgent = node.Type == NodeType.Return,
                IsCommitted = false
            };


            return location;
        }

        private UppaalTransition MapEdgeToTransition(CfgEdge edge, CfgNode fromNode, Dictionary<string, string> locationMap)
        {
            var transition = new UppaalTransition
            {
                Source = locationMap[edge.FromNodeId],
                Target = locationMap[edge.ToNodeId]
            };

            // Add guard for conditions based on edge label
            if (fromNode?.Type == NodeType.Condition || fromNode?.Type == NodeType.Loop)
            {
                if (!string.IsNullOrEmpty(edge.Label))
                {
                    // Get the condition from the node
                    string condition = fromNode.Code;
                    
                    // Clean up the condition - extract from if/while/for
                    if (condition.StartsWith("if") || condition.StartsWith("while"))
                    {
                        int startParen = condition.IndexOf('(');
                        int endParen = condition.LastIndexOf(')');
                        if (startParen >= 0 && endParen > startParen)
                        {
                            condition = condition.Substring(startParen + 1, endParen - startParen - 1).Trim();
                        }
                    }
                    
                    // Determine if this is the false/negated branch
                    bool isFalseBranch = edge.Label.ToLower() == "false" || edge.Label.ToLower() == "else";
                    transition.Guard = ConvertConditionToUppaal(condition, isFalseBranch);
                }
                // Edges without a label from a condition node get no guard (unconditional, e.g., loop-back)
            }

            // Add updates for assignments, declarations with initializers, and expression statements
            // But NOT for MethodCall nodes — those are handled by sync channel expansion
            if ((fromNode?.Type == NodeType.Assignment || fromNode?.Type == NodeType.Declaration || fromNode?.Type == NodeType.Statement) 
                && !string.IsNullOrEmpty(fromNode.Code))
            {
                var update = ExtractAssignment(fromNode.Code);
                if (!string.IsNullOrEmpty(update))
                    transition.Update = update;
            }

            // Don't add comments - we're removing them from edges

            return transition;
        }

        private string GetLocationName(CfgNode node, string code)
        {
            string name;
            // Create meaningful names from the node type and code
            switch (node.Type)
            {
                case NodeType.Entry:
                    name = "Entry";
                    break;
                case NodeType.Exit:
                    name = "Exit";
                    break;
                case NodeType.Return:
                    name = string.IsNullOrEmpty(code) ? "Return" : ShortenCode(code);
                    break;
                case NodeType.Condition:
                    name = ShortenCode(code);
                    break;
                case NodeType.Loop:
                    name = ShortenCode(code);
                    break;
                case NodeType.Assignment:
                    name = ShortenCode(code);
                    break;
                case NodeType.Declaration:
                    name = ShortenCode(code);
                    break;
                case NodeType.MethodCall:
                    // Use the label from CFG which is "Call <MethodName>"
                    name = string.IsNullOrEmpty(node.Label) ? "MethodCall" : node.Label;
                    break;
                case NodeType.Merge:
                    name = "Merge";
                    break;
                default:
                    name = string.IsNullOrEmpty(node.Label) ? node.Type.ToString() : node.Label;
                    break;
            }

            // Sanitize name for UPPAAL 4.1 compatibility:
            // Location names must be valid identifiers (alphanumeric + underscore, no spaces or special chars)
            return SanitizeLocationName(name);
        }

        private static readonly HashSet<string> UppaalReservedWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Official UPPAAL reserved keywords
            "chan", "clock", "double", "bool", "int", "commit", "const", "urgent",
            "broadcast", "init", "process", "state", "invariant", "location", "guard",
            "sync", "assign", "system", "trans", "deadlock", "and", "or", "not", "imply",
            "true", "false", "for", "forall", "exists", "while", "do", "if", "else",
            "return", "typedef", "struct", "rate", "before_update", "after_update",
            "meta", "priority", "progress", "scalar", "select", "void", "default",
            "string", "minE", "maxE", "Pr",
            // Reserved for future use
            "switch", "case", "continue", "break", "enum"
        };

        private string SanitizeLocationName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "L_Node";

            // Replace spaces and hyphens with underscores
            var sanitized = name.Replace(' ', '_').Replace('-', '_');

            // Remove any characters that are not alphanumeric or underscore
            sanitized = new string(sanitized.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

            // Ensure it starts with a letter or underscore
            if (sanitized.Length > 0 && !char.IsLetter(sanitized[0]) && sanitized[0] != '_')
            {
                sanitized = "_" + sanitized;
            }

            if (string.IsNullOrEmpty(sanitized))
                return "L_Node";

            // Limit length
            if (sanitized.Length > 30)
                sanitized = sanitized.Substring(0, 30);

            // Avoid UPPAAL reserved words as location names
            if (UppaalReservedWords.Contains(sanitized))
                sanitized = "L_" + sanitized;

            return sanitized;
        }

        private string ShortenCode(string code)
        {
            if (string.IsNullOrEmpty(code))
                return "Node";

            // Remove common prefixes and clean up
            code = code.Trim();
            
            // Remove "for" keyword and just show parts
            if (code.StartsWith("for (") || code.StartsWith("for("))
            {
                // For "for (init; cond; incr)", extract only the relevant part based on context
                // If it's the whole for statement, just show the condition
                int firstSemi = code.IndexOf(';');
                int lastSemi = code.LastIndexOf(';');
                if (firstSemi > 0 && lastSemi > firstSemi)
                {
                    // Extract condition part
                    string condition = code.Substring(firstSemi + 1, lastSemi - firstSemi - 1).Trim();
                    if (!string.IsNullOrEmpty(condition))
                        code = condition;
                }
            }
            
            // For labels that start with "For ", remove it
            if (code.StartsWith("For "))
                code = code.Substring(4);
                
            // Remove statement prefixes
            if (code.StartsWith("if (") || code.StartsWith("if("))
                code = code.Substring(code.IndexOf('('));
            if (code.StartsWith("while (") || code.StartsWith("while("))
                code = code.Substring(code.IndexOf('('));

            // Limit length
            if (code.Length > 35)
                code = code.Substring(0, 32) + "...";

            return code;
        }

        private string ConvertConditionToUppaal(string csharpCondition, bool isFalseBranch)
        {
            // Clean up whitespace
            var uppaalCondition = csharpCondition.Trim();

            if (isFalseBranch)
            {
                // Try to negate simple conditions directly for cleaner UPPAAL expressions
                return NegateCondition(uppaalCondition);
            }

            return uppaalCondition;
        }

        private string NegateCondition(string condition)
        {
            condition = condition.Trim();

            // Simple comparison negation (no && or ||)
            if (!condition.Contains("&&") && !condition.Contains("||"))
            {
                if (condition.Contains("<="))
                    return condition.Replace("<=", ">");
                if (condition.Contains(">="))
                    return condition.Replace(">=", "<");
                if (condition.Contains("!="))
                    return condition.Replace("!=", "==");
                if (condition.Contains("=="))
                    return condition.Replace("==", "!=");
                // Be careful with < and > — don't match <= or >=
                if (condition.Contains("<") && !condition.Contains("<="))
                    return condition.Replace("<", ">=");
                if (condition.Contains(">") && !condition.Contains(">="))
                    return condition.Replace(">", "<=");
            }

            // For complex expressions with && or ||, use not() wrapper
            // UPPAAL supports 'not' keyword as well as '!'
            return $"not({condition})";
        }

        private string ExtractAssignment(string csharpCode)
        {
            // Remove trailing semicolons and trim
            csharpCode = csharpCode.Trim().TrimEnd(';').Trim();

            // Handle postfix increment/decrement: "i++" → "i = i + 1", "i--" → "i = i - 1"
            if (csharpCode.EndsWith("++"))
            {
                var varName = csharpCode.Substring(0, csharpCode.Length - 2).Trim();
                return $"{varName} = {varName} + 1";
            }
            if (csharpCode.EndsWith("--"))
            {
                var varName = csharpCode.Substring(0, csharpCode.Length - 2).Trim();
                return $"{varName} = {varName} - 1";
            }

            // Handle prefix increment/decrement: "++i" → "i = i + 1", "--i" → "i = i - 1"
            if (csharpCode.StartsWith("++"))
            {
                var varName = csharpCode.Substring(2).Trim();
                return $"{varName} = {varName} + 1";
            }
            if (csharpCode.StartsWith("--"))
            {
                var varName = csharpCode.Substring(2).Trim();
                return $"{varName} = {varName} - 1";
            }

            // Handle compound assignments FIRST (before simple =) since += contains =
            var compoundOps = new[] { "+=", "-=", "*=", "/=", "%=" };
            foreach (var op in compoundOps)
            {
                int opIdx = csharpCode.IndexOf(op);
                if (opIdx >= 0)
                {
                    var left = csharpCode.Substring(0, opIdx).Trim();
                    var right = csharpCode.Substring(opIdx + op.Length).Trim();
                    // Remove type if present
                    var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (leftTokens.Length >= 2)
                        left = leftTokens[leftTokens.Length - 1];
                    
                    string mathOp = op.Substring(0, 1);
                    return $"{left} = {left} {mathOp} {right}";
                }
            }

            // Handle simple assignments: "int balance = deposits - withdrawals" or "result = balance + amount"
            // Exclude conditions that contain comparison operators
            if (csharpCode.Contains("=") && !csharpCode.Contains("==") && !csharpCode.Contains("!=")
                && !csharpCode.Contains("<=") && !csharpCode.Contains(">="))
            {
                var parts = csharpCode.Split('=', 2);
                if (parts.Length == 2)
                {
                    var left = parts[0].Trim();
                    var right = parts[1].Trim();

                    // Remove type declaration if present (e.g., "int balance" → "balance")
                    var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (leftTokens.Length >= 2)
                    {
                        // Last token is the variable name
                        left = leftTokens[leftTokens.Length - 1];
                    }

                    return $"{left} = {right}";
                }
            }

            return string.Empty;
        }
    }
}