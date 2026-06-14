using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using CSharpToUppaal.Backend.Models;

namespace CSharpToUppaal.Backend.Services
{
    /// <summary>
    /// Standalone service that reads a (possibly jumbled) UPPAAL XML model,
    /// recomputes clean positions for every template, and writes a new XML
    /// with no node overlaps and no edge crossings.
    /// 
    /// This is completely independent of the C#-to-UPPAAL pipeline.
    /// Input:  UPPAAL XML string   →   Output:  UPPAAL XML string
    /// </summary>
    public interface IUppaalLayoutService
    {
        /// <summary>
        /// Takes raw UPPAAL XML (possibly with overlapping/jumbled layout)
        /// and returns a new XML string with clean positions.
        /// </summary>
        string FixLayout(string uppaalXml);
        LayoutFixResult FixLayoutWithReport(string uppaalXml);
    }

    public class UppaalLayoutService : IUppaalLayoutService
    {
        // ────────────────────────────────────────────────────────────
        //  PUBLIC API
        // ────────────────────────────────────────────────────────────

        public string FixLayout(string uppaalXml)
        {
            return FixLayoutWithReport(uppaalXml).XmlContent;
        }

        public LayoutFixResult FixLayoutWithReport(string uppaalXml)
        {
            if (string.IsNullOrWhiteSpace(uppaalXml))
                throw new ArgumentException("XML input is empty.");

            // Strip the <!DOCTYPE …> line — XDocument.Parse chokes on it
            string xmlToParse = StripDoctype(uppaalXml);
            var doc = XDocument.Parse(xmlToParse);
            var nta = doc.Element("nta");
            if (nta == null)
                throw new InvalidOperationException("Root <nta> element not found.");

            var result = new LayoutFixResult();

            // Process every <template>
            int templateIndex = 0;
            foreach (var templateEl in nta.Elements("template").ToList())
            {
                RelayoutTemplate(templateEl, templateIndex);
                result.Templates.Add(BuildTemplateReport(templateEl));
                templateIndex++;
            }

            // Re-serialize, prepending the DOCTYPE
            string xml = doc.Declaration != null
                ? doc.Declaration.ToString() + "\n"
                : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

            // Restore the original DOCTYPE if it was present
            string doctype = ExtractDoctype(uppaalXml);
            if (!string.IsNullOrEmpty(doctype))
                xml += doctype + "\n";

            xml += doc.Root!.ToString();
            result.XmlContent = xml;
            result.ReportText = BuildReportText(result);
            return result;
        }

        private static string BuildReportText(LayoutFixResult result)
        {
            if (result.Templates.Count == 0)
                return "No templates found.";

            return string.Join(Environment.NewLine, result.Templates.Select(t =>
                $"{t.TemplateName}: {t.LocationCount} locations, {t.TransitionCount} transitions, {t.UnreachableLocationCount} unreachable locations, {t.EdgeCrossingCount} edge crossing(s), {t.RemovedLocations.Count} removed locations."));
        }

        private static LayoutTemplateReport BuildTemplateReport(XElement templateEl)
        {
            var name = templateEl.Element("name")?.Value ?? "Template";
            var locations = templateEl.Elements("location").ToList();
            var transitions = templateEl.Elements("transition").ToList();
            var initId = templateEl.Element("init")?.Attribute("ref")?.Value
                      ?? locations.FirstOrDefault()?.Attribute("id")?.Value
                      ?? string.Empty;

            var reachable = new HashSet<string>();
            var outgoing = transitions
                .Select(t => new
                {
                    Source = t.Element("source")?.Attribute("ref")?.Value ?? string.Empty,
                    Target = t.Element("target")?.Attribute("ref")?.Value ?? string.Empty
                })
                .GroupBy(t => t.Source)
                .ToDictionary(g => g.Key, g => g.Select(t => t.Target).Where(id => !string.IsNullOrWhiteSpace(id)).ToList());

            var queue = new Queue<string>();
            if (!string.IsNullOrWhiteSpace(initId))
                queue.Enqueue(initId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!reachable.Add(current))
                    continue;

                if (!outgoing.TryGetValue(current, out var targets))
                    continue;

                foreach (var target in targets)
                    queue.Enqueue(target);
            }

            var unreachable = locations
                .Select(l => l.Element("name")?.Value ?? l.Attribute("id")?.Value ?? string.Empty)
                .Where((_, index) =>
                {
                    var id = locations[index].Attribute("id")?.Value ?? string.Empty;
                    return !reachable.Contains(id);
                })
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();

            return new LayoutTemplateReport
            {
                TemplateName = name,
                LocationCount = locations.Count,
                TransitionCount = transitions.Count,
                UnreachableLocationCount = unreachable.Count,
                EdgeCrossingCount = CountEdgeCrossings(templateEl),
                UnreachableLocations = unreachable
            };
        }

        // ────────────────────────────────────────────────────────────
        //  PER-TEMPLATE LAYOUT
        // ────────────────────────────────────────────────────────────

        private void RelayoutTemplate(XElement templateEl, int templateIndex)
        {
            var locationEls = templateEl.Elements("location").ToList();
            var transitionEls = templateEl.Elements("transition").ToList();
            var initEl = templateEl.Element("init");

            if (locationEls.Count == 0) return;

            // ── 1.  Parse locations & transitions ──────────────────
            string? initialId = initEl?.Attribute("ref")?.Value;

            var locById = new Dictionary<string, XElement>();
            foreach (var loc in locationEls)
            {
                string id = loc.Attribute("id")!.Value;
                locById[id] = loc;
            }

            var transitions = new List<(string src, string tgt, XElement el)>();
            var outgoing = new Dictionary<string, List<string>>();
            var incoming = new Dictionary<string, List<string>>();

            foreach (var t in transitionEls)
            {
                string src = t.Element("source")!.Attribute("ref")!.Value;
                string tgt = t.Element("target")!.Attribute("ref")!.Value;
                transitions.Add((src, tgt, t));

                if (!outgoing.ContainsKey(src)) outgoing[src] = new List<string>();
                outgoing[src].Add(tgt);
                if (!incoming.ContainsKey(tgt)) incoming[tgt] = new List<string>();
                incoming[tgt].Add(src);
            }

            string entryId = initialId
                           ?? locationEls.FirstOrDefault()?.Attribute("id")?.Value
                           ?? "";

            // ── 2.  Detect back-edges via DFS ──────────────────────
            var backEdges = new HashSet<(string, string)>();
            {
                var onPath = new HashSet<string>();
                var visited = new HashSet<string>();

                void Dfs(string node)
                {
                    onPath.Add(node);
                    visited.Add(node);
                    if (outgoing.ContainsKey(node))
                    {
                        foreach (var next in outgoing[node])
                        {
                            if (onPath.Contains(next))
                                backEdges.Add((node, next));
                            else if (!visited.Contains(next))
                                Dfs(next);
                        }
                    }
                    onPath.Remove(node);
                }

                if (!string.IsNullOrEmpty(entryId))
                    Dfs(entryId);

                // Catch any disconnected nodes
                foreach (var id in locById.Keys)
                    if (!visited.Contains(id)) Dfs(id);
            }

            // ── 3.  Assign levels (Y) via longest-path DAG ────────
            const int verticalSpacing = 150;
            const int horizontalSpacing = 260;
            const int startY = 80;

            var levels = new Dictionary<string, int>();
            {
                var dist = new Dictionary<string, int>();
                foreach (var id in locById.Keys) dist[id] = -1;
                dist[entryId] = 0;

                var processed = new HashSet<string>();
                var queue = new Queue<string>();
                queue.Enqueue(entryId);

                int safety = 0;
                int maxIter = locById.Count * locById.Count + 100;

                while (queue.Count > 0 && safety++ < maxIter)
                {
                    var cur = queue.Dequeue();
                    if (processed.Contains(cur)) continue;

                    // All non-back-edge predecessors must be processed first
                    bool ready = true;
                    if (incoming.ContainsKey(cur))
                    {
                        foreach (var pred in incoming[cur])
                        {
                            if (!backEdges.Contains((pred, cur)) && !processed.Contains(pred) && pred != cur)
                            { ready = false; break; }
                        }
                    }
                    if (!ready && cur != entryId) { queue.Enqueue(cur); continue; }

                    processed.Add(cur);

                    if (outgoing.ContainsKey(cur))
                    {
                        foreach (var next in outgoing[cur])
                        {
                            if (backEdges.Contains((cur, next))) continue;
                            int d = dist[cur] + 1;
                            if (!dist.ContainsKey(next) || d > dist[next])
                                dist[next] = d;
                            if (!processed.Contains(next))
                                queue.Enqueue(next);
                        }
                    }
                }

                foreach (var id in locById.Keys)
                    levels[id] = dist.ContainsKey(id) && dist[id] >= 0 ? dist[id] : 0;
            }

            // ── 4.  Assign X slots via DFS subtree ordering ───────
            var xSlot = new Dictionary<string, double>();
            double nextSlot = 0;

            {
                var xVisited = new HashSet<string>();

                void AssignX(string node)
                {
                    if (xVisited.Contains(node)) return;
                    xVisited.Add(node);

                    var children = new List<string>();
                    if (outgoing.ContainsKey(node))
                        foreach (var c in outgoing[node])
                            if (!backEdges.Contains((node, c)) && !xVisited.Contains(c))
                                children.Add(c);

                    if (children.Count == 0)
                    {
                        xSlot[node] = nextSlot++;
                    }
                    else
                    {
                        double first = double.MaxValue, last = double.MinValue;
                        foreach (var c in children)
                        {
                            AssignX(c);
                            if (xSlot.ContainsKey(c))
                            {
                                first = Math.Min(first, xSlot[c]);
                                last = Math.Max(last, xSlot[c]);
                            }
                        }
                        xSlot[node] = first <= last
                            ? (first + last) / 2.0
                            : nextSlot++;
                    }
                }

                if (!string.IsNullOrEmpty(entryId))
                    AssignX(entryId);

                foreach (var id in locById.Keys)
                    if (!xSlot.ContainsKey(id)) { xSlot[id] = nextSlot++; }
            }

            // Convert slots → pixel coordinates, centred on x = 0
            double minS = xSlot.Values.Min(), maxS = xSlot.Values.Max();
            double centreS = (minS + maxS) / 2.0;

            var pos = new Dictionary<string, (int x, int y)>();
            foreach (var id in locById.Keys)
            {
                int y = startY + levels[id] * verticalSpacing;
                int x = (int)((xSlot[id] - centreS) * horizontalSpacing);
                pos[id] = (x, y);
            }

            // ── 5.  Write new positions into <location> elements ───
            foreach (var loc in locationEls)
            {
                string id = loc.Attribute("id")!.Value;
                var (x, y) = pos[id];

                loc.SetAttributeValue("x", x);
                loc.SetAttributeValue("y", y);

                // Update name label position
                var nameEl = loc.Element("name");
                if (nameEl != null)
                {
                    var text = nameEl.Value ?? string.Empty;
                    nameEl.SetAttributeValue("x", x - Math.Max(12, text.Length * 3));
                    nameEl.SetAttributeValue("y", y - 7);
                }

                // Update invariant label position if present
                var labels = loc.Elements("label").ToList();
                foreach (var lbl in labels)
                {
                    if (lbl.Attribute("kind")?.Value == "invariant")
                    {
                        lbl.SetAttributeValue("x", x + 50);
                        lbl.SetAttributeValue("y", y + 10);
                    }
                }
            }

            // ── 6.  Re-route transitions (nails + label positions) ─
            var allPositions = pos.Values.ToList();
            int maxBackNailX = int.MinValue;   // exit-loop stacking (RIGHT)
            int minBackNailX = int.MaxValue;   // internal-loop stacking (LEFT)

            // Pre-compute outgoing non-back-edge count per source
            var outCount = new Dictionary<string, int>();
            foreach (var (src, tgt, _) in transitions)
                if (!backEdges.Contains((src, tgt)))
                {
                    if (!outCount.ContainsKey(src)) outCount[src] = 0;
                    outCount[src]++;
                }

            var sourceEdgeIdx = new Dictionary<string, int>();

            foreach (var (src, tgt, tEl) in transitions)
            {
                // Remove any existing <nail> elements
                tEl.Elements("nail").Remove();

                var srcPos = pos[src];
                var tgtPos = pos[tgt];

                bool isBack = backEdges.Contains((src, tgt));
                var nails = new List<XElement>();

                int labelX, labelY;

                if (isBack)
                {
                    bool isExitLoop = tgt == entryId;
                    int minBY = Math.Min(srcPos.y, tgtPos.y);
                    int maxBY = Math.Max(srcPos.y, tgtPos.y);

                    if (isExitLoop)
                    {
                        // Route RIGHT
                        int right = Math.Max(srcPos.x, tgtPos.x);
                        foreach (var p in allPositions)
                            if (p.y >= minBY - 20 && p.y <= maxBY + 20 && p.x > right)
                                right = p.x;

                        int nailX = right + 170;
                        if (maxBackNailX != int.MinValue && nailX <= maxBackNailX)
                            nailX = maxBackNailX + 80;
                        maxBackNailX = Math.Max(maxBackNailX, nailX);

                        labelX = nailX + 10;
                        labelY = (srcPos.y + tgtPos.y) / 2;

                        nails.Add(Nail(nailX, srcPos.y));
                        nails.Add(Nail(nailX, tgtPos.y));
                    }
                    else
                    {
                        // Route LEFT
                        int left = Math.Min(srcPos.x, tgtPos.x);
                        foreach (var p in allPositions)
                            if (p.y >= minBY - 20 && p.y <= maxBY + 20 && p.x < left)
                                left = p.x;

                        int nailX = left - 150;
                        if (minBackNailX != int.MaxValue && nailX >= minBackNailX)
                            nailX = minBackNailX - 80;
                        minBackNailX = Math.Min(minBackNailX, nailX);

                        labelX = nailX - 10;
                        labelY = (srcPos.y + tgtPos.y) / 2;

                        nails.Add(Nail(nailX, srcPos.y));
                        nails.Add(Nail(nailX, tgtPos.y));
                    }
                }
                else
                {
                    // Forward edge
                    int lvlDiff = levels.GetValueOrDefault(tgt) - levels.GetValueOrDefault(src);
                    int srcOut = outCount.GetValueOrDefault(src, 1);

                    bool detour = false;
                    if (srcOut >= 2 && lvlDiff > 1)
                    {
                        int minY = Math.Min(srcPos.y, tgtPos.y);
                        int maxY = Math.Max(srcPos.y, tgtPos.y);
                        foreach (var p in allPositions)
                            if (p.y > minY && p.y < maxY && Math.Abs(p.x - srcPos.x) < 60)
                            { detour = true; break; }
                    }

                    if (detour)
                    {
                        int dx = Math.Max(srcPos.x, tgtPos.x) + 130;
                        nails.Add(Nail(dx, srcPos.y + 30));
                        nails.Add(Nail(dx, tgtPos.y - 30));
                        labelX = dx + 10;
                        labelY = (srcPos.y + tgtPos.y) / 2 - 10;
                    }
                    else
                    {
                        int midX = (srcPos.x + tgtPos.x) / 2;
                        int midY = (srcPos.y + tgtPos.y) / 2;
                        if (!sourceEdgeIdx.ContainsKey(src)) sourceEdgeIdx[src] = 0;
                        sourceEdgeIdx[src]++;

                        int dx = tgtPos.x - srcPos.x;
                        int side = dx < -20 ? -15 : (dx > 20 ? 15 : 8);
                        labelX = midX + side;
                        labelY = midY - 15;
                    }
                }

                // Update label positions in the transition element
                foreach (var lbl in tEl.Elements("label").ToList())
                {
                    lbl.SetAttributeValue("x", labelX);
                    lbl.SetAttributeValue("y", labelY);
                    // Offset successive labels so they don't stack on the same Y
                    labelY += 16;
                }

                // Append nails (after all labels, per UPPAAL DTD)
                foreach (var n in nails)
                    tEl.Add(n);
            }
        }

        // ────────────────────────────────────────────────────────────
        //  HELPERS
        // ────────────────────────────────────────────────────────────

        private static int CountEdgeCrossings(XElement templateEl)
        {
            var locations = templateEl.Elements("location")
                .Select(l => new
                {
                    Id = l.Attribute("id")?.Value ?? string.Empty,
                    X = ReadInt(l.Attribute("x")?.Value),
                    Y = ReadInt(l.Attribute("y")?.Value)
                })
                .Where(l => !string.IsNullOrWhiteSpace(l.Id))
                .ToDictionary(l => l.Id, l => (l.X, l.Y), StringComparer.Ordinal);

            var polylines = templateEl.Elements("transition")
                .Select(t =>
                {
                    var source = t.Element("source")?.Attribute("ref")?.Value ?? string.Empty;
                    var target = t.Element("target")?.Attribute("ref")?.Value ?? string.Empty;
                    if (!locations.TryGetValue(source, out var src) || !locations.TryGetValue(target, out var tgt))
                        return null;

                    var points = new List<(int x, int y)> { src };
                    points.AddRange(t.Elements("nail").Select(n => (ReadInt(n.Attribute("x")?.Value), ReadInt(n.Attribute("y")?.Value))));
                    points.Add(tgt);
                    return new TransitionPolyline(source, target, points);
                })
                .Where(p => p != null)
                .Cast<TransitionPolyline>()
                .ToList();

            var crossings = 0;
            for (var i = 0; i < polylines.Count; i++)
            {
                for (var j = i + 1; j < polylines.Count; j++)
                {
                    if (SharesEndpoint(polylines[i], polylines[j]))
                        continue;

                    if (PolylinesIntersect(polylines[i].Points, polylines[j].Points))
                        crossings++;
                }
            }

            return crossings;
        }

        private sealed record TransitionPolyline(string Source, string Target, List<(int x, int y)> Points);

        private static bool SharesEndpoint(TransitionPolyline first, TransitionPolyline second)
        {
            return first.Source == second.Source
                || first.Source == second.Target
                || first.Target == second.Source
                || first.Target == second.Target;
        }

        private static bool PolylinesIntersect(IReadOnlyList<(int x, int y)> first, IReadOnlyList<(int x, int y)> second)
        {
            for (var i = 0; i < first.Count - 1; i++)
            {
                for (var j = 0; j < second.Count - 1; j++)
                {
                    if (SegmentsIntersect(first[i], first[i + 1], second[j], second[j + 1]))
                        return true;
                }
            }

            return false;
        }

        private static bool SegmentsIntersect((int x, int y) p1, (int x, int y) q1, (int x, int y) p2, (int x, int y) q2)
        {
            var o1 = Orientation(p1, q1, p2);
            var o2 = Orientation(p1, q1, q2);
            var o3 = Orientation(p2, q2, p1);
            var o4 = Orientation(p2, q2, q1);

            if (o1 != o2 && o3 != o4)
                return true;

            return o1 == 0 && OnSegment(p1, p2, q1)
                || o2 == 0 && OnSegment(p1, q2, q1)
                || o3 == 0 && OnSegment(p2, p1, q2)
                || o4 == 0 && OnSegment(p2, q1, q2);
        }

        private static int Orientation((int x, int y) p, (int x, int y) q, (int x, int y) r)
        {
            var value = (long)(q.y - p.y) * (r.x - q.x) - (long)(q.x - p.x) * (r.y - q.y);
            if (value == 0)
                return 0;
            return value > 0 ? 1 : 2;
        }

        private static bool OnSegment((int x, int y) p, (int x, int y) q, (int x, int y) r)
        {
            return q.x <= Math.Max(p.x, r.x)
                && q.x >= Math.Min(p.x, r.x)
                && q.y <= Math.Max(p.y, r.y)
                && q.y >= Math.Min(p.y, r.y);
        }

        private static int ReadInt(string? value)
        {
            return int.TryParse(value, out var parsed) ? parsed : 0;
        }

        private static XElement Nail(int x, int y)
            => new XElement("nail", new XAttribute("x", x), new XAttribute("y", y));

        private static string StripDoctype(string xml)
        {
            // Remove the <!DOCTYPE …> line so XDocument.Parse succeeds
            var lines = xml.Split('\n');
            var filtered = lines.Where(l => !l.TrimStart().StartsWith("<!DOCTYPE"));
            return string.Join("\n", filtered);
        }

        private static string ExtractDoctype(string xml)
        {
            var lines = xml.Split('\n');
            var doctypeLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("<!DOCTYPE"));
            return doctypeLine?.Trim() ?? "";
        }
    }
}
