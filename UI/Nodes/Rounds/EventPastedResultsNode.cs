using Composition.Input;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;

namespace UI.Nodes.Rounds
{
    // Results pasted in from elsewhere (eg another event). Pilots are listed with their pasted
    // position and are used, in that order, to seed formats and rounds added from here.
    public class EventPastedResultsNode : EventPilotListNode<EventPilotNode>
    {
        public EventPastedResultsNode(RoundsNode roundsNode, EventManager ev, Round round)
            : base(roundsNode, ev, round)
        {
            Refresh();
        }

        public override void Refresh()
        {
            SetHeading("Pasted Results");
            base.Refresh();
        }

        protected override void UpdateButtons()
        {
            base.UpdateButtons();

            // Finals are seeded from the calling round's races, which pasted results don't have.
            canAddFinal = false;
        }

        public override void EditSettings()
        {
            RoundsNode.EditPastedResults(Round);
        }

        public override void MakeMenu(MouseMenu mm)
        {
            base.MakeMenu(mm);

            // Replaces these results with the clipboard's.
            mm.AddItem("Paste Results...", () => { RoundsNode.PasteResultsStage(Round); });
        }

        public override string[][] MakeTable()
        {
            return Order(PilotNodes).Select(pn => new string[] { pn.Pilot.Name, pn.Position.ToString() }).ToArray();
        }

        public override void UpdateNodes()
        {
            Tuple<int, Pilot>[] pilots = EventManager.RoundManager.GetPastedResultPilots(Stage).ToArray();

            foreach (EventPilotNode node in PilotNodes.ToArray())
            {
                if (!pilots.Any(t => t.Item2 == node.Pilot))
                {
                    node.Dispose();
                }
            }

            foreach (Tuple<int, Pilot> positionPilot in pilots)
            {
                EventPilotNode node = PilotNodes.FirstOrDefault(pn => pn.Pilot == positionPilot.Item2);
                if (node == null)
                {
                    node = new EventPilotNode(EventManager, positionPilot.Item2);
                    contentContainer.AddChild(node);
                }
                node.Position = positionPilot.Item1;
            }

            SetSubHeading(MakeSubHeading(pilots.Length));
        }

        private string MakeSubHeading(int pilotCount)
        {
            if (Stage == null || Stage.PastedResults == null || Stage.Standings == null || Stage.Standings.Rows == null)
                return "";

            int total = Stage.Standings.Rows.Length;
            int from = Stage.PastedResults.FromPosition;
            int to = Math.Min(Stage.PastedResults.ToPosition, total);

            string text = "Positions " + from + " - " + to + " of " + total;

            int missing = Math.Max(0, to - from + 1) - pilotCount;
            if (missing > 0)
            {
                text += "\n" + missing + " not in the event";
            }
            return text;
        }

        public override IEnumerable<EventPilotNode> Order(IEnumerable<EventPilotNode> nodes)
        {
            return nodes.OrderBy(n => n.Position);
        }

        public override void UpdatePositions(IEnumerable<EventPilotNode> nodes)
        {
            // Positions come from the pasted results, set in UpdateNodes.
        }

        public override bool HasResult()
        {
            if (Stage == null)
                return false;

            return Stage.PastedResults != null;
        }
    }
}
