using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CambotAlphacam
{
    // Top-down height-map comparison of two STL meshes (simulated stock vs
    // the model). 3-axis assumption: for every XY cell keep the highest Z of
    // each mesh, then difference. Positive = material left on, negative =
    // gouge. Enough to tell "roughing terraces left behind" from "finished
    // to tolerance" without a full mesh-distance library. Added 2026-09-29.
    internal static class StlCompare
    {
        public struct Tri { public double X0, Y0, Z0, X1, Y1, Z1, X2, Y2, Z2; }

        public static List<Tri> ReadStl(string path)
        {
            var bytes = File.ReadAllBytes(path);
            bool ascii = bytes.Length > 5 && Encoding.ASCII.GetString(bytes, 0, 5) == "solid";
            if (ascii)
            {
                // A binary file whose header happens to start with "solid" has
                // no "facet" keyword in the first few hundred bytes.
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 400));
                ascii = head.IndexOf("facet", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            var tris = new List<Tri>();
            if (ascii)
            {
                var verts = new List<double[]>(3);
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 4) continue;
                    verts.Add(new[] { P(parts[1]), P(parts[2]), P(parts[3]) });
                    if (verts.Count == 3)
                    {
                        tris.Add(new Tri
                        {
                            X0 = verts[0][0], Y0 = verts[0][1], Z0 = verts[0][2],
                            X1 = verts[1][0], Y1 = verts[1][1], Z1 = verts[1][2],
                            X2 = verts[2][0], Y2 = verts[2][1], Z2 = verts[2][2]
                        });
                        verts.Clear();
                    }
                }
            }
            else
            {
                if (bytes.Length < 84) throw new InvalidDataException("STL file too short: " + path);
                uint n = BitConverter.ToUInt32(bytes, 80);
                int off = 84;
                for (uint i = 0; i < n && off + 50 <= bytes.Length; i++, off += 50)
                {
                    tris.Add(new Tri
                    {
                        X0 = BitConverter.ToSingle(bytes, off + 12), Y0 = BitConverter.ToSingle(bytes, off + 16), Z0 = BitConverter.ToSingle(bytes, off + 20),
                        X1 = BitConverter.ToSingle(bytes, off + 24), Y1 = BitConverter.ToSingle(bytes, off + 28), Z1 = BitConverter.ToSingle(bytes, off + 32),
                        X2 = BitConverter.ToSingle(bytes, off + 36), Y2 = BitConverter.ToSingle(bytes, off + 40), Z2 = BitConverter.ToSingle(bytes, off + 44)
                    });
                }
            }
            return tris;
        }

        private static double P(string s) { return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture); }

        public static void Bounds(List<Tri> tris, out double minX, out double minY, out double minZ, out double maxX, out double maxY, out double maxZ)
        {
            minX = minY = minZ = double.PositiveInfinity; maxX = maxY = maxZ = double.NegativeInfinity;
            foreach (var t in tris)
            {
                minX = Math.Min(minX, Math.Min(t.X0, Math.Min(t.X1, t.X2))); maxX = Math.Max(maxX, Math.Max(t.X0, Math.Max(t.X1, t.X2)));
                minY = Math.Min(minY, Math.Min(t.Y0, Math.Min(t.Y1, t.Y2))); maxY = Math.Max(maxY, Math.Max(t.Y0, Math.Max(t.Y1, t.Y2)));
                minZ = Math.Min(minZ, Math.Min(t.Z0, Math.Min(t.Z1, t.Z2))); maxZ = Math.Max(maxZ, Math.Max(t.Z0, Math.Max(t.Z1, t.Z2)));
            }
        }

        // Highest Z of the mesh over each cell of an N×N grid spanning the
        // given XY box (cell centres sampled). NaN where nothing covers the cell.
        public static double[,] HeightMap(List<Tri> tris, double x0, double y0, double x1, double y1, int n)
        {
            var hm = new double[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) hm[i, j] = double.NaN;
            double dx = (x1 - x0) / n, dy = (y1 - y0) / n;
            foreach (var t in tris)
            {
                double txMin = Math.Min(t.X0, Math.Min(t.X1, t.X2)), txMax = Math.Max(t.X0, Math.Max(t.X1, t.X2));
                double tyMin = Math.Min(t.Y0, Math.Min(t.Y1, t.Y2)), tyMax = Math.Max(t.Y0, Math.Max(t.Y1, t.Y2));
                int i0 = Math.Max(0, (int)Math.Floor((txMin - x0) / dx)), i1 = Math.Min(n - 1, (int)Math.Floor((txMax - x0) / dx));
                int j0 = Math.Max(0, (int)Math.Floor((tyMin - y0) / dy)), j1 = Math.Min(n - 1, (int)Math.Floor((tyMax - y0) / dy));
                if (i1 < i0 || j1 < j0) continue;
                double det = (t.Y1 - t.Y2) * (t.X0 - t.X2) + (t.X2 - t.X1) * (t.Y0 - t.Y2);
                if (Math.Abs(det) < 1e-14) continue; // vertical facet: no top-down footprint
                for (int i = i0; i <= i1; i++)
                {
                    double px = x0 + (i + 0.5) * dx;
                    for (int j = j0; j <= j1; j++)
                    {
                        double py = y0 + (j + 0.5) * dy;
                        double l1 = ((t.Y1 - t.Y2) * (px - t.X2) + (t.X2 - t.X1) * (py - t.Y2)) / det;
                        double l2 = ((t.Y2 - t.Y0) * (px - t.X2) + (t.X0 - t.X2) * (py - t.Y2)) / det;
                        double l3 = 1 - l1 - l2;
                        if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;
                        double z = l1 * t.Z0 + l2 * t.Z1 + l3 * t.Z2;
                        if (double.IsNaN(hm[i, j]) || z > hm[i, j]) hm[i, j] = z;
                    }
                }
            }
            return hm;
        }

        public class Spot { public double X, Y, Value, ModelZ, StockZ; }

        // cos of the model's local slope at a cell (1 = flat, 0 = vertical),
        // from central differences on the height map. NaN neighbours are
        // treated as level so edges don't read as cliffs.
        private static double CosSlope(double[,] hm, int i, int j, int n, double dx, double dy)
        {
            double z = hm[i, j];
            double zl = i > 0 && !double.IsNaN(hm[i - 1, j]) ? hm[i - 1, j] : z;
            double zr = i < n - 1 && !double.IsNaN(hm[i + 1, j]) ? hm[i + 1, j] : z;
            double zd = j > 0 && !double.IsNaN(hm[i, j - 1]) ? hm[i, j - 1] : z;
            double zu = j < n - 1 && !double.IsNaN(hm[i, j + 1]) ? hm[i, j + 1] : z;
            double gx = (zr - zl) / (2 * dx), gy = (zu - zd) / (2 * dy);
            return 1.0 / Math.Sqrt(1.0 + gx * gx + gy * gy);
        }

        public class Report
        {
            public int Cells, CoveredCells, SteepCells;
            public double MaxRemaining, MeanRemaining, MaxGouge, MeanGouge;
            public double PctWithinTolerance, PctRemainingAboveTolerance, PctGougeBeyondTolerance;
            public double AreaRemainingAboveTolerance; // drawing units²
            public List<Spot> WorstRemaining = new List<Spot>();
            public List<Spot> WorstGouges = new List<Spot>();
        }

        // Compare stock over model. `tolerance` = allowed +/- deviation for the
        // "within tolerance" percentage. Worst spots are reported with a
        // minimum separation so a single ridge doesn't fill the list.
        public static Report Compare(List<Tri> model, List<Tri> stock, int n, double tolerance, int topN)
        {
            double mx0, my0, mz0, mx1, my1, mz1;
            Bounds(model, out mx0, out my0, out mz0, out mx1, out my1, out mz1);
            var hmM = HeightMap(model, mx0, my0, mx1, my1, n);
            var hmS = HeightMap(stock, mx0, my0, mx1, my1, n);
            double dx = (mx1 - mx0) / n, dy = (my1 - my0) / n;
            var r = new Report { Cells = n * n };
            var diffs = new List<Tuple<int, int, double>>();
            double sumRem = 0, sumGouge = 0; int nRem = 0, nGouge = 0, nWithin = 0, nRemAbove = 0, nGougeBeyond = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (double.IsNaN(hmM[i, j]) || double.IsNaN(hmS[i, j])) continue;
                    // Deviation measured NORMAL to the model surface, not along
                    // Z: dz * cos(slope). On a flat this is dz; on a steep wall a
                    // one-cell XY straddle otherwise reads as a huge Z "gouge"
                    // (0.03 phantom gouges on pocket walls, 2026-09-30).
                    double cosSlope = CosSlope(hmM, i, j, n, dx, dy);
                    double dzRaw = hmS[i, j] - hmM[i, j];
                    double d = dzRaw * cosSlope;
                    if (cosSlope < 0.2) r.SteepCells++;
                    r.CoveredCells++;
                    diffs.Add(Tuple.Create(i, j, d));
                    if (d > 0) { sumRem += d; nRem++; if (d > r.MaxRemaining) r.MaxRemaining = d; if (d > tolerance) nRemAbove++; }
                    else if (d < 0) { sumGouge += -d; nGouge++; if (-d > r.MaxGouge) r.MaxGouge = -d; if (-d > tolerance) nGougeBeyond++; }
                    if (Math.Abs(d) <= tolerance) nWithin++;
                }
            if (r.CoveredCells > 0)
            {
                r.PctWithinTolerance = 100.0 * nWithin / r.CoveredCells;
                r.PctRemainingAboveTolerance = 100.0 * nRemAbove / r.CoveredCells;
                r.PctGougeBeyondTolerance = 100.0 * nGougeBeyond / r.CoveredCells;
                r.AreaRemainingAboveTolerance = nRemAbove * dx * dy;
            }
            r.MeanRemaining = nRem > 0 ? sumRem / nRem : 0;
            r.MeanGouge = nGouge > 0 ? sumGouge / nGouge : 0;
            int sep = Math.Max(2, n / 20);
            diffs.Sort((a, b) => b.Item3.CompareTo(a.Item3));
            r.WorstRemaining = PickSpots(diffs, hmM, hmS, mx0, my0, dx, dy, sep, topN, true, tolerance);
            diffs.Sort((a, b) => a.Item3.CompareTo(b.Item3));
            r.WorstGouges = PickSpots(diffs, hmM, hmS, mx0, my0, dx, dy, sep, topN, false, tolerance);
            return r;
        }

        private static List<Spot> PickSpots(List<Tuple<int, int, double>> sorted, double[,] hmM, double[,] hmS, double x0, double y0, double dx, double dy, int sep, int topN, bool positive, double tol)
        {
            var spots = new List<Spot>();
            var taken = new List<Tuple<int, int>>();
            foreach (var c in sorted)
            {
                if (positive ? c.Item3 <= tol : -c.Item3 <= tol) break;
                bool near = false;
                foreach (var t in taken) if (Math.Abs(t.Item1 - c.Item1) < sep && Math.Abs(t.Item2 - c.Item2) < sep) { near = true; break; }
                if (near) continue;
                taken.Add(Tuple.Create(c.Item1, c.Item2));
                spots.Add(new Spot
                {
                    X = Math.Round(x0 + (c.Item1 + 0.5) * dx, 4),
                    Y = Math.Round(y0 + (c.Item2 + 0.5) * dy, 4),
                    Value = Math.Round(positive ? c.Item3 : -c.Item3, 4),
                    ModelZ = Math.Round(hmM[c.Item1, c.Item2], 4),
                    StockZ = Math.Round(hmS[c.Item1, c.Item2], 4)
                });
                if (spots.Count >= topN) break;
            }
            return spots;
        }
    }
}
