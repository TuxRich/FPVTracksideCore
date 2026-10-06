using Composition.Nodes;
using RaceLib;
using RaceLib.Format;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Tools;

namespace UI.Nodes.Rounds
{
    public class PasteResultsOptions
    {
        [Category("Stage")]
        public string StageName { get; set; }

        [Category("Results to use")]
        [DisplayName("From position")]
        public int FromPosition { get; set; }

        [Category("Results to use")]
        [DisplayName("To position")]
        public int ToPosition { get; set; }

        [Category("Results to use")]
        [DisplayName("Using")]
        public string Summary { get { return MakeSummary(); } }

        [Category("Pilots")]
        [DisplayName("Add missing pilots to the event")]
        public bool AddMissingPilots { get; set; }

        [Category("Pasted Results")]
        public StandingsResult Pasted { get; private set; }

        private Func<StandingsRow, Pilot> findPilot;

        public PasteResultsOptions(StandingsResult pasted, string stageName, PastedResultsSettings settings, Func<StandingsRow, Pilot> findPilot)
        {
            Pasted = pasted;
            StageName = stageName;
            this.findPilot = findPilot;

            FromPosition = 1;
            ToPosition = pasted.Rows.Length;

            if (settings != null)
            {
                FromPosition = settings.FromPosition;
                ToPosition = Math.Min(settings.ToPosition, pasted.Rows.Length);
            }

            AddMissingPilots = false;
        }

        public PastedResultsSettings GetSettings()
        {
            int count = Pasted.Rows.Length;

            int from = Math.Clamp(FromPosition, 1, Math.Max(1, count));
            int to = Math.Clamp(ToPosition, from, Math.Max(from, count));

            return new PastedResultsSettings() { FromPosition = from, ToPosition = to };
        }

        public bool InEvent(StandingsRow row)
        {
            return findPilot(row) != null;
        }

        private string MakeSummary()
        {
            PastedResultsSettings settings = GetSettings();

            StandingsRow[] inRange = Pasted.Rows.Where((r, i) => settings.Contains(i + 1)).ToArray();
            int missing = inRange.Count(r => !InEvent(r));

            string summary = inRange.Length + " of " + Pasted.Rows.Length + " pilots";
            if (missing > 0)
            {
                if (AddMissingPilots)
                    summary += " (" + missing + " to be added to the event)";
                else
                    summary += " (" + missing + " not in the event, skipped)";
            }
            return summary;
        }
    }

    public class PasteResultsEditor : ObjectEditorNode<PasteResultsOptions>
    {
        private List<CustomTextPropertyNode<PasteResultsOptions>> rowNodes;

        public PasteResultsEditor(PasteResultsOptions options)
        {
            rowNodes = new List<CustomTextPropertyNode<PasteResultsOptions>>();

            SetObject(options);
            heading.Text = "Paste Results";
        }

        protected override IEnumerable<PropertyNode<PasteResultsOptions>> CreatePropertyNodes(PasteResultsOptions obj, PropertyInfo pi)
        {
            if (pi.Name == nameof(PasteResultsOptions.Pasted))
            {
                return CreateRowNodes(obj, pi);
            }

            return base.CreatePropertyNodes(obj, pi);
        }

        private IEnumerable<PropertyNode<PasteResultsOptions>> CreateRowNodes(PasteResultsOptions obj, PropertyInfo pi)
        {
            rowNodes.Clear();

            int position = 0;
            foreach (StandingsRow row in obj.Pasted.Rows)
            {
                position++;

                CustomTextPropertyNode<PasteResultsOptions> node = new CustomTextPropertyNode<PasteResultsOptions>(obj, pi, TextColor, row.Name);
                node.Name = position.ToStringPosition();
                rowNodes.Add(node);
            }

            UpdateRowNodes(obj);
            return rowNodes;
        }

        private void UpdateRowNodes(PasteResultsOptions options)
        {
            PastedResultsSettings settings = options.GetSettings();

            int position = 0;
            foreach (CustomTextPropertyNode<PasteResultsOptions> node in rowNodes)
            {
                StandingsRow row = options.Pasted.Rows[position];
                position++;

                bool inRange = settings.Contains(position);

                string text = row.Name;
                if (inRange && !options.InEvent(row))
                {
                    if (options.AddMissingPilots)
                        text += " (will be added to the event)";
                    else
                        text += " (not in the event, skipped)";
                }

                node.Value.Text = text;
                node.Alpha = inRange ? 1 : 0.35f;
            }
        }

        protected override void ChildValueChanged(Change newChange)
        {
            UpdateRowNodes(Single);
            base.ChildValueChanged(newChange);
        }
    }
}
