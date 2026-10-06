using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InterviewCopilot
{
    /// <summary>One word the reader found on the screen: its text and where it sits, in pixels.</summary>
    internal readonly record struct OcrWord(string Text, double X, double Y, double W, double H);

    /// <summary>
    /// Turns the words a text reader found on a screen into text a person (or a model) can read: rows top to bottom,
    /// side by side panels one after the other instead of mixed along each row, and indentation kept.
    ///
    /// The reader hands back words with positions, and its own line order is built for a page of prose. A problem on the
    /// left and an editor on the right share every row, so read straight across they interleave: half a sentence of the
    /// statement, then half a line of code, then the rest of the sentence. Here the page is cut at its empty vertical
    /// gutters first, and each panel is read down on its own.
    /// </summary>
    internal static class OcrLayout
    {
        internal static string ToText(IReadOnlyList<OcrWord> words)
        {
            var ws = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.W > 0 && w.H > 0).ToList();
            if (ws.Count == 0) return "";

            double charW = Median(ws.Where(w => w.Text.Length >= 3).Select(w => w.W / w.Text.Length).DefaultIfEmpty(8));
            double lineH = Median(ws.Select(w => w.H));

            var sb = new StringBuilder();
            foreach (var pane in SplitIntoPanes(ws, charW, lineH))
            {
                string text = PaneText(pane, charW, lineH);
                if (text.Length == 0) continue;
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append(text);
            }
            return sb.ToString();
        }

        /// <summary>Columns of the page with an empty vertical gutter between them, left to right.</summary>
        private static List<List<OcrWord>> SplitIntoPanes(List<OcrWord> ws, double charW, double lineH)
        {
            double minX = ws.Min(w => w.X), maxX = ws.Max(w => w.X + w.W);
            const double bin = 4;
            int bins = (int)Math.Ceiling((maxX - minX) / bin) + 1;
            var rows = ClusterRows(ws, lineH);
            if (rows.Count < 6) return new List<List<OcrWord>> { ws };

            // How many rows have a word over each 4 px column of the page.
            var cover = new int[bins];
            foreach (var row in rows)
            {
                var hit = new bool[bins];
                foreach (var w in row)
                {
                    int a = (int)((w.X - minX) / bin), b = (int)((w.X + w.W - minX) / bin);
                    for (int i = Math.Max(0, a); i <= Math.Min(bins - 1, b); i++) hit[i] = true;
                }
                for (int i = 0; i < bins; i++) if (hit[i]) cover[i]++;
            }

            // A gutter is a run of columns that almost no row touches (a title bar's words may cross it), wide enough to
            // be a gap between panels rather than the space between two words, and with text on both sides of it.
            int emptyMax = Math.Max(1, (int)(rows.Count * 0.04));
            double gutterMin = Math.Max(28, 3 * charW);
            var cuts = new List<double>();
            int runStart = -1;
            for (int i = 0; i <= bins; i++)
            {
                bool empty = i < bins && cover[i] <= emptyMax;
                if (empty && runStart < 0) runStart = i;
                if (!empty && runStart >= 0)
                {
                    int runEnd = i - 1;
                    bool inside = runStart > 0 && runEnd < bins - 1;
                    if (inside && (runEnd - runStart + 1) * bin >= gutterMin)
                        cuts.Add(minX + (runStart + runEnd + 1) / 2.0 * bin);
                    runStart = -1;
                }
            }
            if (cuts.Count == 0) return new List<List<OcrWord>> { ws };

            var panes = new List<List<OcrWord>>();
            for (int i = 0; i <= cuts.Count; i++) panes.Add(new List<OcrWord>());
            foreach (var w in ws)
            {
                double cx = w.X + w.W / 2;
                int idx = 0;
                while (idx < cuts.Count && cx > cuts[idx]) idx++;
                panes[idx].Add(w);
            }
            return panes.Where(p => p.Count > 0).ToList();
        }

        private static List<List<OcrWord>> ClusterRows(List<OcrWord> ws, double lineH)
        {
            var sorted = ws.OrderBy(w => w.Y + w.H / 2).ToList();
            var rows = new List<List<OcrWord>>();
            double rowCenter = double.NaN;
            foreach (var w in sorted)
            {
                double c = w.Y + w.H / 2;
                if (rows.Count == 0 || Math.Abs(c - rowCenter) > lineH * 0.55)
                {
                    rows.Add(new List<OcrWord>());
                    rowCenter = c;
                }
                rows[^1].Add(w);
                rowCenter = rows[^1].Average(x => x.Y + x.H / 2);
            }
            return rows;
        }

        private static string PaneText(List<OcrWord> pane, double charW, double lineH)
        {
            var rows = ClusterRows(pane, lineH);

            // The panel's left edge is where most of its rows start, not its leftmost word: a heading that begins
            // further left would otherwise push every other line of the panel to the right by the difference.
            var starts = rows.Select(r => r.Min(w => w.X)).ToList();
            double common = Math.Max(2, rows.Count * 0.15);
            var frequent = starts.GroupBy(x => Math.Round(x / charW)).Where(g => g.Count() >= common)
                                 .Select(g => g.Average()).ToList();
            double left = frequent.Count > 0 ? frequent.Min() : starts.Min();

            var sb = new StringBuilder();
            foreach (var row in rows)
            {
                var line = row.OrderBy(w => w.X).ToList();
                int indent = (int)Math.Round((line[0].X - left) / charW);
                sb.Append(' ', Math.Clamp(indent, 0, 40));
                double prevEnd = line[0].X;
                for (int i = 0; i < line.Count; i++)
                {
                    if (i > 0)
                    {
                        int gap = (int)Math.Round((line[i].X - prevEnd) / charW);
                        sb.Append(' ', Math.Clamp(gap, 1, 12));
                    }
                    sb.Append(line[i].Text);
                    prevEnd = line[i].X + line[i].W;
                }
                sb.Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        private static double Median(IEnumerable<double> values)
        {
            var a = values.OrderBy(v => v).ToArray();
            if (a.Length == 0) return 8;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }
    }
}
