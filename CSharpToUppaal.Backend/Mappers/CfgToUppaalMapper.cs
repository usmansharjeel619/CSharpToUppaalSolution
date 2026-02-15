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

            // Declare variables
            foreach (var variable in cfg.Variables)
            {
                declarations.AppendLine($"{variable.Value} {variable.Key};");
            }

            // Add clock for timing
            declarations.AppendLine($"clock t_{cfg.MethodName};");

            return declarations.ToString();
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
                // Use the edge label directly if it's "true" or "false"
                if (!string.IsNullOrEmpty(edge.Label))
                {
                    // Get the condition from the node
                    string condition = fromNode.Code;
                    
                    // Clean up the condition
                    if (condition.StartsWith("if") || condition.StartsWith("while"))
                    {
                        // Extract condition from "if (condition)" or "while (condition)"
                        int startParen = condition.IndexOf('(');
                        int endParen = condition.LastIndexOf(')');
                        if (startParen >= 0 && endParen > startParen)
                        {
                            condition = condition.Substring(startParen + 1, endParen - startParen - 1).Trim();
                        }
                    }
                    
                    // Convert to UPPAAL format and negate if false branch
                    bool isFalseBranch = edge.Label.ToLower() == "false" || edge.Label.ToLower() == "else";
                    transition.Guard = ConvertConditionToUppaal(condition, isFalseBranch);
                }
                else if (fromNode.Properties.TryGetValue("condition", out var condition))
                {
                    transition.Guard = ConvertConditionToUppaal(condition.ToString(), false);
                }
            }

            // Add updates for assignments
            if (fromNode?.Type == NodeType.Assignment && !string.IsNullOrEmpty(fromNode.Code))
            {
                transition.Update = ExtractAssignment(fromNode.Code);
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

        private string SanitizeLocationName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Node";

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
                return "Node";

            // Limit length
            if (sanitized.Length > 30)
                sanitized = sanitized.Substring(0, 30);

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
            // Convert C# operators to UPPAAL operators
            var uppaalCondition = csharpCondition
                .Replace("==", "==")
                .Replace("!=", "!=")
                .Replace("<", "<")
                .Replace(">", ">")
                .Replace("<=", "<=")
                .Replace(">=", ">=")
                .Replace("&&", "&&")
                .Replace("||", "||")
                .Replace("!", "!");

            if (isFalseBranch)
            {
                return $"!({uppaalCondition})";
            }

            return uppaalCondition;
        }

        private string ExtractAssignment(string csharpCode)
        {
            if (csharpCode.Contains("=") && !csharpCode.Contains("=="))
            {
                var parts = csharpCode.Split('=', 2);
                if (parts.Length == 2)
                {
                    var left = parts[0].Trim();
                    var right = parts[1].Trim().TrimEnd(';');

                    // Remove type declaration if present
                    var lastSpaceIndex = left.LastIndexOf(' ');
                    if (lastSpaceIndex > 0)
                    {
                        left = left.Substring(lastSpaceIndex + 1);
                    }

                    return $"{left} = {right}";
                }
            }
            return string.Empty;
        }
    }
}