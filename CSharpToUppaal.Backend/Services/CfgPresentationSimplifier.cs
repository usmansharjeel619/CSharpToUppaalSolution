using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.Backend.Services;

/// <summary>
/// Builds a display-only CFG projection. Consecutive declarations on one
/// unbranched path are represented by one node, without changing the CFG used
/// for semantic translation to UPPAAL.
/// </summary>
public static class CfgPresentationSimplifier
{
    public static ControlFlowGraph Simplify(ControlFlowGraph source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var nodesById = source.Nodes.ToDictionary(node => node.Id);
        var incoming = source.Nodes.ToDictionary(
            node => node.Id,
            node => source.Edges.Where(edge => edge.ToNodeId == node.Id).ToList());
        var outgoing = source.Nodes.ToDictionary(
            node => node.Id,
            node => source.Edges.Where(edge => edge.FromNodeId == node.Id).ToList());

        var groups = new List<List<CfgNode>>();
        var groupedIds = new HashSet<string>();

        foreach (var node in source.Nodes.Where(node => node.Type == NodeType.Declaration))
        {
            if (groupedIds.Contains(node.Id) || HasDeclarationPredecessor(node, incoming, outgoing, nodesById))
            {
                continue;
            }

            var group = new List<CfgNode> { node };
            var current = node;

            while (outgoing[current.Id].Count == 1)
            {
                var edge = outgoing[current.Id][0];
                if (!string.IsNullOrWhiteSpace(edge.Label) ||
                    !nodesById.TryGetValue(edge.ToNodeId, out var next) ||
                    next.Type != NodeType.Declaration ||
                    incoming[next.Id].Count != 1)
                {
                    break;
                }

                group.Add(next);
                current = next;
            }

            if (group.Count > 1)
            {
                groups.Add(group);
                foreach (var declaration in group)
                {
                    groupedIds.Add(declaration.Id);
                }
            }
        }

        if (groups.Count == 0)
        {
            return Clone(source);
        }

        var representativeById = source.Nodes.ToDictionary(node => node.Id, node => node.Id);
        var displayedNodes = new List<CfgNode>();

        foreach (var group in groups)
        {
            var representative = group[0];
            foreach (var declaration in group)
            {
                representativeById[declaration.Id] = representative.Id;
            }
        }

        foreach (var node in source.Nodes)
        {
            if (groupedIds.Contains(node.Id) && representativeById[node.Id] != node.Id)
            {
                continue;
            }

            var group = groups.FirstOrDefault(candidate => candidate[0].Id == node.Id);
            if (group != null)
            {
                displayedNodes.Add(new CfgNode
                {
                    Id = node.Id,
                    Label = group.Count == 2 ? "Declarations" : $"Declarations ({group.Count})",
                    Type = NodeType.Declaration,
                    Code = string.Join(Environment.NewLine, group.Select(declaration => declaration.Code)),
                    Properties = new Dictionary<string, object>(node.Properties)
                    {
                        ["declarationCount"] = group.Count
                    }
                });
            }
            else
            {
                displayedNodes.Add(CloneNode(node));
            }
        }

        var displayedEdges = source.Edges
            .Select(edge => new CfgEdge
            {
                FromNodeId = representativeById[edge.FromNodeId],
                ToNodeId = representativeById[edge.ToNodeId],
                Label = edge.Label
            })
            .Where(edge => edge.FromNodeId != edge.ToNodeId)
            .GroupBy(edge => (edge.FromNodeId, edge.ToNodeId, edge.Label))
            .Select(group => group.First())
            .ToList();

        return new ControlFlowGraph
        {
            MethodName = source.MethodName,
            ReturnType = source.ReturnType,
            Nodes = displayedNodes,
            Edges = displayedEdges,
            EntryNodeId = representativeById[source.EntryNodeId],
            ExitNodeId = representativeById[source.ExitNodeId],
            Variables = new Dictionary<string, string>(source.Variables)
        };
    }

    private static bool HasDeclarationPredecessor(
        CfgNode node,
        IReadOnlyDictionary<string, List<CfgEdge>> incoming,
        IReadOnlyDictionary<string, List<CfgEdge>> outgoing,
        IReadOnlyDictionary<string, CfgNode> nodesById)
    {
        if (incoming[node.Id].Count != 1)
        {
            return false;
        }

        var edge = incoming[node.Id][0];
        return string.IsNullOrWhiteSpace(edge.Label) &&
               nodesById.TryGetValue(edge.FromNodeId, out var predecessor) &&
               predecessor.Type == NodeType.Declaration &&
               outgoing[predecessor.Id].Count == 1;
    }

    private static ControlFlowGraph Clone(ControlFlowGraph source) => new()
    {
        MethodName = source.MethodName,
        ReturnType = source.ReturnType,
        Nodes = source.Nodes.Select(CloneNode).ToList(),
        Edges = source.Edges.Select(edge => new CfgEdge
        {
            FromNodeId = edge.FromNodeId,
            ToNodeId = edge.ToNodeId,
            Label = edge.Label
        }).ToList(),
        EntryNodeId = source.EntryNodeId,
        ExitNodeId = source.ExitNodeId,
        Variables = new Dictionary<string, string>(source.Variables)
    };

    private static CfgNode CloneNode(CfgNode node) => new()
    {
        Id = node.Id,
        Label = node.Label,
        Type = node.Type,
        Code = node.Code,
        Properties = new Dictionary<string, object>(node.Properties)
    };
}
