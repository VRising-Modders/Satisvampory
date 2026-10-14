// SPDX-License-Identifier: AGPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using Satisvampory.Engine.Lists;
using Satisvampory.Engine.Model;

namespace Satisvampory.Engine.Loops
{
    public enum LoopMode
    {
        CycleOnce,
        EvenDistribution,
        Off,
    }

    /// <summary>A strongly connected set of containers (spec 05).</summary>
    public sealed class Loop
    {
        public Loop(IReadOnlyList<ContainerId> members, IReadOnlyList<Edge> edges)
        {
            Members = members;
            Edges = edges;
            Key = string.Join(",", members.Select(m => m.Value).OrderBy(g => g).Select(g => g.ToString("N")));
            Closing = edges.OrderByDescending(e => e.Sequence).First();
        }

        /// <summary>Members in a stable order (by id).</summary>
        public IReadOnlyList<ContainerId> Members { get; }

        /// <summary>Edges with both ends inside the loop.</summary>
        public IReadOnlyList<Edge> Edges { get; }

        /// <summary>The newest edge; the one not run when the mode is Off.</summary>
        public Edge Closing { get; }

        /// <summary>Stable key for persisting the mode: the sorted member ids.</summary>
        public string Key { get; }
    }

    public static class LoopFinder
    {
        /// <summary>Tarjan's strongly connected components; every component with a cycle is a loop.</summary>
        public static List<Loop> Find(IReadOnlyList<Edge> edges)
        {
            var adj = new Dictionary<ContainerId, List<ContainerId>>();
            foreach (var e in edges)
            {
                if (!adj.TryGetValue(e.From, out var l)) adj[e.From] = l = new List<ContainerId>();
                l.Add(e.To);
                if (!adj.ContainsKey(e.To)) adj[e.To] = new List<ContainerId>();
            }

            var index = 0;
            var indices = new Dictionary<ContainerId, int>();
            var low = new Dictionary<ContainerId, int>();
            var onStack = new HashSet<ContainerId>();
            var stack = new Stack<ContainerId>();
            var components = new List<List<ContainerId>>();

            // Iterative to survive large castles without deep recursion.
            foreach (var root in adj.Keys.OrderBy(k => k.Value))
            {
                if (indices.ContainsKey(root)) continue;
                var work = new Stack<(ContainerId Node, int Child)>();
                work.Push((root, 0));
                while (work.Count > 0)
                {
                    var (v, child) = work.Pop();
                    if (child == 0)
                    {
                        indices[v] = low[v] = index++;
                        stack.Push(v);
                        onStack.Add(v);
                    }

                    var next = adj[v];
                    if (child < next.Count)
                    {
                        work.Push((v, child + 1));
                        var w = next[child];
                        if (!indices.ContainsKey(w)) work.Push((w, 0));
                        else if (onStack.Contains(w)) low[v] = Math.Min(low[v], indices[w]);
                        continue;
                    }

                    // All children done: propagate low to the parent, maybe close a component.
                    if (work.Count > 0)
                    {
                        var parent = work.Peek().Node;
                        low[parent] = Math.Min(low[parent], low[v]);
                    }
                    if (low[v] == indices[v])
                    {
                        var comp = new List<ContainerId>();
                        ContainerId w;
                        do
                        {
                            w = stack.Pop();
                            onStack.Remove(w);
                            comp.Add(w);
                        } while (w != v);
                        components.Add(comp);
                    }
                }
            }

            var loops = new List<Loop>();
            foreach (var comp in components)
            {
                var set = new HashSet<ContainerId>(comp);
                var inner = edges.Where(e => set.Contains(e.From) && set.Contains(e.To)).ToList();
                if (comp.Count > 1 || inner.Any(e => e.From == e.To))
                    loops.Add(new Loop(comp.OrderBy(c => c.Value).ToList(), inner));
            }
            return loops;
        }
    }
}
