using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

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
    }

    public class UppaalLayoutService : IUppaalLayoutService
    {
        // ────────────────────────────────────────────────────────────
        //  PUBLIC API
        // ────────────────────────────────────────────────────────────

        public string FixLayout(string uppaalXml)
        {
            if (string.IsNullOrWhiteSpace(uppaalXml))
                throw new ArgumentException("XML input is empty.");

            // Strip the <!DOCTYPE …> line — XDocument.Parse chokes on it
            string xmlToParse = StripDoctype(uppaalXml);
            var doc = XDocument.Parse(xmlToParse);
            var nta = doc.Element("nta");
            if (nta == null)
                throw new InvalidOperationException("Root <nta> element not found.");

            // Process every <template>
            int templateIndex = 0;
            foreach (var templateEl in nta.Elements("template").ToList())
            {
                RelayoutTemplate(templateEl, templateIndex);
                templateIndex++;
            }

            // Re-serialize, prepending the DOCTYPE
            string result = doc.Declaration != null
                ? doc.Declaration.ToString() + "\n"
                : "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";

            // Restore the original DOCTYPE if it was present
            string doctype = ExtractDoctype(uppaalXml);
            if (!string.IsNullOrEmpty(doctype))
                result += doctype + "\n";

            result += doc.Root!.ToString();
            return result;
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
            const int verticalSpacing = 100;
            const int horizontalSpacing = 170;
            const int startY = -200;

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
                    nameEl.SetAttributeValue("x", x - 45);
                    nameEl.SetAttributeValue("y", y - 35);
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
