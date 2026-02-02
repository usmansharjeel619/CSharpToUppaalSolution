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
            foreach (var node in cfg.Nodes)
            {
                var location = MapNodeToLocation(node);
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
                Name = GetLocationName(node),
                IsUrgent = node.Type == NodeType.Return,
                IsCommitted = false
            };

            // Add labels for special nodes
            if (node.Type == NodeType.Condition || node.Type == NodeType.Loop)
            {
                location.Labels["comment"] = $"/* {node.Code} */";
            }

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

            // Add edge label as comment if it exists and is not true/false
            if (!string.IsNullOrEmpty(edge.Label) && 
                edge.Label.ToLower() != "true" && 
                edge.Label.ToLower() != "false")
            {
                transition.Comments.Add($"// Branch: {edge.Label}");
            }

            // Add code as comment
            if (!string.IsNullOrEmpty(fromNode?.Code))
            {
                transition.Comments.Add($"// C#: {fromNode.Code}");
            }

            return transition;
        }

        private string GetLocationName(CfgNode node)
        {
            return node.Type switch
            {
                NodeType.Entry => "Entry",
                NodeType.Exit => "Exit",
                NodeType.Condition => "Condition",
                NodeType.Loop => "Loop",
                NodeType.Return => "Return",
                NodeType.Declaration => "Declaration",
                NodeType.Merge => "Merge",
                _ => node.Label
            };
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