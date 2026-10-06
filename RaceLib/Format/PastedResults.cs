using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RaceLib.Format
{
    // Reads a results table off the clipboard so results from another event can seed this one.
    //
    // Accepts:
    //  - "Copy to Clipboard" from an FPVTrackside results stage (name, values... per line)
    //  - a spreadsheet selection, optionally with a heading row and a position column before the names
    //  - a plain list of names, optionally numbered ("1. Alice", "2nd Bob")
    //
    // Rows come back in finishing order: sorted by the position column when there is one, otherwise
    // in the pasted order. A pilot listed twice keeps their best position.
    public static class PastedResults
    {
        private static readonly string[] nameHeadings = new string[] { "pilot", "pilots", "pilot name", "name", "callsign", "call sign", "handle", "racer" };

        // "1", "#1", "1st", "1."
        private static readonly Regex positionCell = new Regex(@"^#?(\d+)(st|nd|rd|th|\.)?$", RegexOptions.IgnoreCase);

        // "1. Alice", "1st Alice", "1) Alice", "1 - Alice"
        private static readonly Regex numberedName = new Regex(@"^#?(\d+)(st|nd|rd|th)?\s*[\.\):\-]?\s+(\S.*)$", RegexOptions.IgnoreCase);

        public static StandingsResult Parse(string text)
        {
            StandingsResult result = new StandingsResult() { Rows = new StandingsRow[0] };

            if (string.IsNullOrWhiteSpace(text))
                return result;

            string[] lines = text.Split('\n').Select(l => l.Trim('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            char? delimiter = null;
            if (lines.Any(l => l.Contains('\t')))
                delimiter = '\t';
            else if (lines.Any(l => l.Contains(',')))
                delimiter = ',';

            List<string[]> table = lines.Select(l => SplitLine(l, delimiter)).ToList();
            if (!table.Any())
                return result;

            int nameColumn = Array.FindIndex(table[0], c => nameHeadings.Contains(c.ToLowerInvariant()));
            string[] headingRow = null;
            if (nameColumn >= 0)
            {
                headingRow = table[0];
                table.RemoveAt(0);
            }
            else
            {
                nameColumn = FindNameColumn(table);
            }

            if (nameColumn < 0 || !table.Any())
                return result;

            int positionColumn = FindPositionColumn(table, nameColumn);
            bool numberedNames = table.All(row => nameColumn < row.Length && numberedName.IsMatch(row[nameColumn]));

            List<PastedRow> rows = new List<PastedRow>();
            foreach (string[] row in table)
            {
                if (nameColumn >= row.Length)
                    continue;

                string name = row[nameColumn];
                int? position = null;

                if (positionColumn >= 0 && positionColumn < row.Length)
                {
                    position = ParsePosition(row[positionColumn]);
                }

                if (numberedNames)
                {
                    Match match = numberedName.Match(name);
                    name = match.Groups[3].Value.Trim();
                    if (position == null && int.TryParse(match.Groups[1].Value, out int numbered))
                    {
                        position = numbered;
                    }
                }

                if (string.IsNullOrEmpty(name))
                    continue;

                rows.Add(new PastedRow() { Name = name, Position = position, Values = row.Skip(nameColumn + 1).ToArray() });
            }

            // OrderBy is stable, so rows without a position (or tied) keep their pasted order.
            IEnumerable<PastedRow> ordered = rows.OrderBy(r => r.Position ?? int.MaxValue);

            List<StandingsRow> output = new List<StandingsRow>();
            foreach (PastedRow row in ordered)
            {
                if (output.Any(o => string.Equals(o.Name, row.Name, StringComparison.OrdinalIgnoreCase)))
                    continue;

                output.Add(new StandingsRow() { Name = row.Name, Values = row.Values });
            }

            result.Rows = output.ToArray();
            if (headingRow != null)
            {
                result.Headings = headingRow.Skip(nameColumn + 1).ToArray();
            }
            return result;
        }

        private static string[] SplitLine(string line, char? delimiter)
        {
            if (delimiter == null)
                return new string[] { line.Trim() };

            return line.Split(delimiter.Value).Select(c => c.Trim()).ToArray();
        }

        private static bool IsText(string cell)
        {
            return cell.Any(char.IsLetter) && !positionCell.IsMatch(cell);
        }

        // The first column that's mostly text. Points, times and positions don't have letters in them.
        private static int FindNameColumn(List<string[]> table)
        {
            int columns = table.Max(r => r.Length);
            for (int column = 0; column < columns; column++)
            {
                int textCount = table.Count(r => column < r.Length && IsText(r[column]));
                if (textCount * 2 >= table.Count)
                {
                    return column;
                }
            }
            return -1;
        }

        // A position column has to come before the names, as columns after the names (round points, totals) are also numbers.
        private static int FindPositionColumn(List<string[]> table, int nameColumn)
        {
            for (int column = 0; column < nameColumn; column++)
            {
                if (table.All(r => column < r.Length && positionCell.IsMatch(r[column])))
                {
                    return column;
                }
            }
            return -1;
        }

        private static int? ParsePosition(string cell)
        {
            Match match = positionCell.Match(cell);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int position))
            {
                return position;
            }
            return null;
        }

        private class PastedRow
        {
            public string Name { get; set; }
            public int? Position { get; set; }
            public string[] Values { get; set; }
        }
    }
}
