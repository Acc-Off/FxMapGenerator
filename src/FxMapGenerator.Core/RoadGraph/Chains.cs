using FxMapGenerator.Core.Geometry;

namespace FxMapGenerator.Core.RoadGraph;

/// <summary>What a chain of path links is, from its nodes and links.</summary>
/// <param name="Length">Along the links (m), hubs included.</param>
/// <param name="Highway">Share of the chain's nodes with the highway flag (likewise unpaved and switched off).</param>
/// <param name="LanesForward">Mean lanes along the chain's direction per link (back: against it).</param>
/// <param name="Street">The street hash most of the named nodes carry (the first met of equals), 0 when none is named. A node
/// the road edits add votes too when it has no name (for none): a road added from a named node stays unnamed.</param>
/// <param name="OneWay">At least 70 % of the links have lanes in one direction only.</param>
public sealed record ChainAttributes(double Length, double Highway, double Unpaved, double SwitchedOff, double LanesForward, double LanesBack,
    uint Street, bool Named, bool OneWay);

/// <summary>
/// Map roads out of the path graph: at every node the links are paired by the straightest continuation, then the
/// pairs are walked into chains. A chain is one road through a junction instead of a lane-centre path into it.
/// </summary>
public static class Chains
{
    /// <summary>
    /// Pairs at every node with two or more connections: through a plain node of two unless it turns back (hairpin,
    /// turn over 150 degrees); at a node of three or more only within <paramref name="maxTurn"/> degrees, not between two
    /// different named streets, and never where the one-way directions do not continue. Candidates go straightest first
    /// (a pair whose directions do not continue counts 20 degrees more), then by node keys; a node's link is used once.
    /// Chains start at a link end without a partner; the links left over are loops.
    /// </summary>
    public static List<List<int>> Build(PathNet net, double maxTurn = 35.0)
    {
        var partner = new Dictionary<(int Node, int From), int>();
        var cands = new List<(double Turn, int M1, int M2)>();
        for (int n = 0; n < net.Nodes.Count; n++)
        {
            var nbrs = net.Adjacent[n];
            if (nbrs.Count < 2) continue;
            var p = net.P(n);
            var dirs = new (double X, double Y)[nbrs.Count];
            for (int i = 0; i < nbrs.Count; i++)
            {
                var m = net.P(nbrs[i]);
                double d = Num.Hypot(m.X - p.X, m.Y - p.Y);
                if (d == 0) d = 1.0;
                dirs[i] = ((m.X - p.X) / d, (m.Y - p.Y) / d);
            }
            cands.Clear();
            for (int i = 0; i < nbrs.Count; i++)
                for (int j = i + 1; j < nbrs.Count; j++)
                {
                    int m1 = nbrs[i], m2 = nbrs[j];
                    double cos = dirs[i].X * dirs[j].X + dirs[i].Y * dirs[j].Y;
                    double ang = Num.Degrees(Math.Acos(Math.Max(-1.0, Math.Min(1.0, cos))));      // 180 = straight through
                    double turn = 180.0 - ang;
                    uint s1 = net.Nodes[m1].Street, s2 = net.Nodes[m2].Street;
                    if (nbrs.Count == 2)
                    {
                        if (turn > 150) continue;
                    }
                    else if (turn > maxTurn || (s1 != 0 && s2 != 0 && s1 != s2)) continue;
                    // traffic arriving from m1 should be able to leave to m2, and back
                    int in1 = net.LanesFrom(m1, n).Forward, out1 = net.LanesFrom(n, m1).Forward;
                    int in2 = net.LanesFrom(m2, n).Forward, out2 = net.LanesFrom(n, m2).Forward;
                    bool consistent = (in1 > 0) == (out2 > 0) && (out1 > 0) == (in2 > 0);
                    if (!consistent && nbrs.Count >= 3) continue;       // at a hub the carriageways meet in one point: never cross over
                    cands.Add((consistent ? turn : turn + 20, m1, m2));
                }
            cands.Sort((a, b) =>
            {
                int c = a.Turn.CompareTo(b.Turn);
                if (c != 0) return c;
                c = Compare(net, a.M1, b.M1);
                return c != 0 ? c : Compare(net, a.M2, b.M2);
            });
            var used = new HashSet<int>();
            foreach (var (_, m1, m2) in cands)
            {
                if (used.Contains(m1) || used.Contains(m2)) continue;
                partner[(n, m1)] = m2;
                partner[(n, m2)] = m1;
                used.Add(m1);
                used.Add(m2);
            }
        }

        var seen = new HashSet<int>();
        var chains = new List<List<int>>();
        List<int> Walk(int a, int b)
        {
            var seq = new List<int> { a, b };
            seen.Add(net.LinkOf(a, b));
            while (partner.TryGetValue((b, a), out var c))
            {
                int k = net.LinkOf(b, c);
                if (!seen.Add(k)) break;
                seq.Add(c);
                (a, b) = (b, c);
            }
            return seq;
        }
        for (int i = 0; i < net.Links.Count; i++)
        {
            if (seen.Contains(i)) continue;
            var (a, b) = (net.Links[i].A, net.Links[i].B);
            if (!partner.ContainsKey((a, b))) chains.Add(Walk(a, b));
            else if (!partner.ContainsKey((b, a))) chains.Add(Walk(b, a));
        }
        for (int i = 0; i < net.Links.Count; i++)
            if (!seen.Contains(i)) chains.Add(Walk(net.Links[i].A, net.Links[i].B));
        return chains;
    }

    static int Compare(PathNet net, int a, int b) => a == b ? 0 : net.KeyBefore(a, b) ? -1 : 1;

    public static ChainAttributes Attributes(PathNet net, IReadOnlyList<int> seq)
    {
        int n = seq.Count - 1;
        int lf = 0, lb = 0, oneWay = 0;
        for (int i = 0; i < n; i++)
        {
            var (f, b) = net.LanesFrom(seq[i], seq[i + 1]);
            lf += f;
            lb += b;
            if ((f == 0) != (b == 0)) oneWay++;
        }
        int hw = 0, off = 0, dis = 0;
        var streets = new List<(uint Street, int Count)>();
        foreach (var m in seq)
        {
            var v = net.Nodes[m];
            if (v.Highway) hw++;
            if (v.Unpaved) off++;
            if (v.SwitchedOff) dis++;
            if (v.Street == 0 && !v.Added) continue;
            int at = streets.FindIndex(s => s.Street == v.Street);
            if (at < 0) streets.Add((v.Street, 1));
            else streets[at] = (v.Street, streets[at].Count + 1);
        }
        double len = 0;
        for (int i = 0; i < n; i++)
        {
            P2 p = net.P(seq[i]), q = net.P(seq[i + 1]);
            len += Num.Hypot(q.X - p.X, q.Y - p.Y);
        }
        uint street = 0;
        int best = 0;
        foreach (var (s, c) in streets)
            if (c > best) (street, best) = (s, c);
        int links = Math.Max(n, 1);
        return new ChainAttributes(len, (double)hw / seq.Count, (double)off / seq.Count, (double)dis / seq.Count,
            (double)lf / links, (double)lb / links, street, street != 0, oneWay >= 0.7 * links);
    }

    /// <summary>The chain without its hub nodes in between: a road goes straight from its entry to its exit node.</summary>
    public static List<int> Straighten(PathNet net, IReadOnlyList<int> seq)
    {
        if (seq.Count <= 2) return seq.ToList();
        var o = new List<int> { seq[0] };
        for (int i = 1; i < seq.Count - 1; i++)
            if (!net.IsHub(seq[i])) o.Add(seq[i]);
        o.Add(seq[^1]);
        return o;
    }
}
