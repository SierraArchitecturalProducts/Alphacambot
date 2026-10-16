using System;
using System.Collections.Generic;

namespace CambotAlphacam
{
    // Pure-geometry side of alphacam.check_geometry: a path is read from COM
    // into a list of Seg records once, every check runs on that list, and
    // the optional fix produces a new Seg list that the caller rebuilds via
    // Create2DGeometry / ReplaceElements. No COM objects in here.
    internal sealed class Seg
    {
        public bool IsArc;
        public double Sx, Sy, Sz, Ex, Ey, Ez;
        public double Cx, Cy, R;      // arcs only
        public bool CW;               // arcs only
        public double Len;

        public Seg Clone() { return (Seg)MemberwiseClone(); }

        // Signed sweep in radians from start to end around (Cx,Cy); CW < 0.
        public double Sweep()
        {
            if (!IsArc || R <= 0) return 0;
            double a0 = Math.Atan2(Sy - Cy, Sx - Cx);
            double a1 = Math.Atan2(Ey - Cy, Ex - Cx);
            double s = a1 - a0;
            if (CW) { while (s > -1e-9) s -= 2 * Math.PI; }
            else { while (s < 1e-9) s += 2 * Math.PI; }
            // start == end: full circle
            if (Math.Abs(Ex - Sx) < 1e-9 && Math.Abs(Ey - Sy) < 1e-9) s = CW ? -2 * Math.PI : 2 * Math.PI;
            return s;
        }

        public void StartTangent(out double tx, out double ty)
        {
            if (!IsArc) { Dir(Sx, Sy, Ex, Ey, out tx, out ty); return; }
            double a = Math.Atan2(Sy - Cy, Sx - Cx);
            if (CW) { tx = Math.Sin(a); ty = -Math.Cos(a); } else { tx = -Math.Sin(a); ty = Math.Cos(a); }
        }

        public void EndTangent(out double tx, out double ty)
        {
            if (!IsArc) { Dir(Sx, Sy, Ex, Ey, out tx, out ty); return; }
            double a = Math.Atan2(Ey - Cy, Ex - Cx);
            if (CW) { tx = Math.Sin(a); ty = -Math.Cos(a); } else { tx = -Math.Sin(a); ty = Math.Cos(a); }
        }

        // Max distance between the arc and its chord.
        public double Sagitta()
        {
            if (!IsArc || R <= 0) return 0;
            double s = Math.Abs(Sweep());
            if (s >= Math.PI) return R;
            return R * (1 - Math.Cos(s / 2));
        }

        private static void Dir(double x0, double y0, double x1, double y1, out double tx, out double ty)
        {
            double dx = x1 - x0, dy = y1 - y0, l = Math.Sqrt(dx * dx + dy * dy);
            if (l < 1e-12) { tx = 1; ty = 0; return; }
            tx = dx / l; ty = dy / l;
        }
    }

    internal sealed class LintOptions
    {
        public double MaxArcRadius = 100;      // arcs larger than this are NC-hostile (I/J overflow, radius-mismatch alarms)
        public double MinLength = 0.001;       // elements shorter than this are micro moves
        public double GapTol = 1e-4;           // joint gap between consecutive elements
        public double SpikeTurnDeg = 150;      // tangent reversal that marks a hook/spike
        public double SpikeMaxLength = 0.01;   // only short elements count as spikes
        public double NearlyClosedTol = 0.01;  // open path whose ends are this close is "nearly closed"
        public double ChordTol = 0.0005;       // max deviation when a big arc becomes chords
        public double FlattenTol = 0.001;      // arc flattening for the self-intersection test
        public int MaxSegmentsForIntersect = 4000;
        public double ZTol = 1e-6;
    }

    internal sealed class Issue
    {
        public string Type;
        public int Index;        // element index (0-based), -1 for path-level
        public double X, Y;
        public double Value;
        public string Note;
    }

    internal static class GeoLint
    {
        public static List<Issue> Analyse(List<Seg> segs, bool closed, LintOptions o)
        {
            var issues = new List<Issue>();
            int n = segs.Count;
            if (n == 0) return issues;
            bool anyZ = false;
            for (int i = 0; i < n; i++)
            {
                var s = segs[i];
                if (s.IsArc && s.R > o.MaxArcRadius)
                    issues.Add(new Issue { Type = "bigArc", Index = i, X = s.Sx, Y = s.Sy, Value = s.R, Note = "sagitta " + s.Sagitta().ToString("0.######") });
                if (s.Len < o.MinLength && !(n == 1 && s.IsArc))
                    issues.Add(new Issue { Type = "tiny", Index = i, X = s.Sx, Y = s.Sy, Value = s.Len });
                if (Math.Abs(s.Sz) > o.ZTol || Math.Abs(s.Ez) > o.ZTol) anyZ = true;

                bool hasNext = i < n - 1 || closed;
                if (hasNext)
                {
                    var t = segs[(i + 1) % n];
                    double gap = Dist(s.Ex, s.Ey, t.Sx, t.Sy);
                    if (gap > o.GapTol)
                        issues.Add(new Issue { Type = "gap", Index = i, X = s.Ex, Y = s.Ey, Value = gap });
                    double turn = TurnDeg(s, t);
                    if (turn > o.SpikeTurnDeg && Math.Min(s.Len, t.Len) < o.SpikeMaxLength)
                        issues.Add(new Issue { Type = "spike", Index = i, X = s.Ex, Y = s.Ey, Value = turn, Note = "turn deg at joint " + i + "/" + ((i + 1) % n) });
                }
            }
            if (anyZ) issues.Add(new Issue { Type = "nonZeroZ", Index = -1, X = segs[0].Sx, Y = segs[0].Sy, Value = MaxAbsZ(segs) });
            if (!closed && n > 0)
            {
                double endGap = Dist(segs[0].Sx, segs[0].Sy, segs[n - 1].Ex, segs[n - 1].Ey);
                if (endGap > 1e-7 && endGap <= o.NearlyClosedTol)
                    issues.Add(new Issue { Type = "nearlyClosed", Index = -1, X = segs[n - 1].Ex, Y = segs[n - 1].Ey, Value = endGap });
            }
            var si = SelfIntersection(segs, closed, o);
            if (si != null) issues.Add(si);
            return issues;
        }

        public static double TurnDeg(Seg a, Seg b)
        {
            double ax, ay, bx, by;
            a.EndTangent(out ax, out ay);
            b.StartTangent(out bx, out by);
            double dot = Math.Max(-1, Math.Min(1, ax * bx + ay * by));
            return Math.Acos(dot) * 180 / Math.PI;
        }

        public static double Dist(double x0, double y0, double x1, double y1)
        {
            double dx = x1 - x0, dy = y1 - y0; return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double MaxAbsZ(List<Seg> segs)
        {
            double m = 0;
            foreach (var s in segs) { m = Math.Max(m, Math.Abs(s.Sz)); m = Math.Max(m, Math.Abs(s.Ez)); }
            return m;
        }

        // Flatten to a polyline and test non-adjacent segment pairs.
        public static Issue SelfIntersection(List<Seg> segs, bool closed, LintOptions o)
        {
            var pts = new List<double[]>();
            foreach (var s in segs) AppendFlattened(s, pts, o.FlattenTol);
            int m = pts.Count - 1;
            if (m < 3) return null;
            if (m > o.MaxSegmentsForIntersect) return new Issue { Type = "selfIntersectSkipped", Index = -1, Value = m, Note = "too many segments for the O(n^2) test" };
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 2; j < m; j++)
                {
                    if (closed && i == 0 && j == m - 1) continue;
                    double ix, iy;
                    if (SegIntersect(pts[i], pts[i + 1], pts[j], pts[j + 1], out ix, out iy))
                        return new Issue { Type = "selfIntersect", Index = -1, X = ix, Y = iy, Value = 0 };
                }
            }
            return null;
        }

        private static void AppendFlattened(Seg s, List<double[]> pts, double tol)
        {
            if (pts.Count == 0) pts.Add(new[] { s.Sx, s.Sy });
            if (!s.IsArc || s.R <= 0) { pts.Add(new[] { s.Ex, s.Ey }); return; }
            double sweep = s.Sweep();
            int k = ChordCount(s.R, Math.Abs(sweep), tol);
            double a0 = Math.Atan2(s.Sy - s.Cy, s.Sx - s.Cx);
            for (int i = 1; i < k; i++)
            {
                double a = a0 + sweep * i / k;
                pts.Add(new[] { s.Cx + s.R * Math.Cos(a), s.Cy + s.R * Math.Sin(a) });
            }
            pts.Add(new[] { s.Ex, s.Ey });
        }

        public static int ChordCount(double r, double sweepAbs, double tol)
        {
            if (r <= 0 || sweepAbs <= 0) return 1;
            double ratio = 1 - tol / r;
            if (ratio <= -1) return 1;
            double maxStep = 2 * Math.Acos(Math.Max(-1, ratio));
            if (maxStep <= 1e-9) return 1;
            return Math.Max(1, (int)Math.Ceiling(sweepAbs / maxStep));
        }

        private static bool SegIntersect(double[] p, double[] p2, double[] q, double[] q2, out double ix, out double iy)
        {
            ix = iy = 0;
            double rx = p2[0] - p[0], ry = p2[1] - p[1];
            double sx = q2[0] - q[0], sy = q2[1] - q[1];
            double den = rx * sy - ry * sx;
            if (Math.Abs(den) < 1e-14) return false; // parallel/collinear: ignored
            double qpx = q[0] - p[0], qpy = q[1] - p[1];
            double t = (qpx * sy - qpy * sx) / den;
            double u = (qpx * ry - qpy * rx) / den;
            const double eps = 1e-9;
            if (t < eps || t > 1 - eps || u < eps || u > 1 - eps) return false;
            ix = p[0] + t * rx; iy = p[1] + t * ry;
            return true;
        }

        // Build the repaired element list. Big arcs become chords within
        // ChordTol, micro elements and spike elements are dropped (the next
        // element simply starts where the previous one ended, which also
        // closes joint gaps), everything else is kept verbatim. Z is kept.
        public static List<Seg> Rebuild(List<Seg> segs, bool closed, LintOptions o, Dictionary<string, int> stats)
        {
            int n = segs.Count;
            var drop = new bool[n];
            for (int i = 0; i < n; i++)
            {
                var s = segs[i];
                if (s.Len < o.MinLength && n > 1) { drop[i] = true; Bump(stats, "tinyDropped"); continue; }
            }
            // A tangent reversal at a joint is never a legitimate cut; drop the
            // short element(s) on either side of it (out-and-back hooks are two
            // short elements, a hook into a long element is one).
            if (n > 2)
            {
                for (int i = 0; i < n; i++)
                {
                    bool hasNext = i < n - 1 || closed;
                    if (!hasNext) continue;
                    int j = (i + 1) % n;
                    if (drop[i] || drop[j]) continue;
                    if (TurnDeg(segs[i], segs[j]) <= o.SpikeTurnDeg) continue;
                    if (segs[i].Len < o.SpikeMaxLength) { drop[i] = true; Bump(stats, "spikeDropped"); }
                    if (segs[j].Len < o.SpikeMaxLength) { drop[j] = true; Bump(stats, "spikeDropped"); }
                }
            }
            var outSegs = new List<Seg>();
            double curX = segs[0].Sx, curY = segs[0].Sy, curZ = segs[0].Sz;
            // start from the first kept element's start so direction/start point are preserved
            for (int i = 0; i < n; i++) if (!drop[i]) { curX = segs[i].Sx; curY = segs[i].Sy; curZ = segs[i].Sz; break; }
            for (int i = 0; i < n; i++)
            {
                if (drop[i]) continue;
                var s = segs[i];
                if (s.IsArc && s.R > o.MaxArcRadius)
                {
                    double sweep = s.Sweep();
                    int k = ChordCount(s.R, Math.Abs(sweep), o.ChordTol);
                    double a0 = Math.Atan2(s.Sy - s.Cy, s.Sx - s.Cx);
                    for (int j = 1; j <= k; j++)
                    {
                        double ex, ey;
                        if (j == k) { ex = s.Ex; ey = s.Ey; }
                        else { double a = a0 + sweep * j / k; ex = s.Cx + s.R * Math.Cos(a); ey = s.Cy + s.R * Math.Sin(a); }
                        double ez = s.Sz + (s.Ez - s.Sz) * j / k;
                        outSegs.Add(new Seg { IsArc = false, Sx = curX, Sy = curY, Sz = curZ, Ex = ex, Ey = ey, Ez = ez, Len = Dist(curX, curY, ex, ey) });
                        curX = ex; curY = ey; curZ = ez;
                    }
                    Bump(stats, "bigArcChorded");
                    continue;
                }
                var c = s.Clone();
                if (Dist(c.Sx, c.Sy, curX, curY) > 1e-12) Bump(stats, "jointsClosed");
                c.Sx = curX; c.Sy = curY; c.Sz = curZ;
                c.Len = c.IsArc ? c.R * Math.Abs(c.Sweep()) : Dist(c.Sx, c.Sy, c.Ex, c.Ey);
                outSegs.Add(c);
                curX = c.Ex; curY = c.Ey; curZ = c.Ez;
            }
            if (closed && outSegs.Count > 0)
            {
                var first = outSegs[0]; var last = outSegs[outSegs.Count - 1];
                double g = Dist(last.Ex, last.Ey, first.Sx, first.Sy);
                if (g > 1e-9)
                {
                    if (!last.IsArc || g < o.GapTol) { last.Ex = first.Sx; last.Ey = first.Sy; last.Ez = first.Sz; }
                    else outSegs.Add(new Seg { IsArc = false, Sx = last.Ex, Sy = last.Ey, Sz = last.Ez, Ex = first.Sx, Ey = first.Sy, Ez = first.Sz, Len = g });
                    Bump(stats, "closureSnapped");
                }
            }
            return outSegs;
        }

        private static void Bump(Dictionary<string, int> stats, string key)
        {
            if (stats == null) return;
            int v; stats.TryGetValue(key, out v); stats[key] = v + 1;
        }
    }
}
