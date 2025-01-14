﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Composition;
using Composition.Input;
using Composition.Layers;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using RaceLib;
using Tools;

namespace UI.Nodes
{
    public class EventPointsNode : EventPilotListNode<PilotPointsNode>
    {
        public EventPointsNode(EventManager ev, Round round) 
            : base(ev, round)
        {
            SetHeading("Points");
            Refresh();
            EventManager.ResultManager.RaceResultsChanged += PointsManager_RaceResultsChanged;
        }

        protected override void UpdateButtons()
        {
            canAddTimes = true;
            base.UpdateButtons();
        }


        public override void Dispose()
        {
            EventManager.ResultManager.RaceResultsChanged -= PointsManager_RaceResultsChanged;
            base.Dispose();
        }

        private void PointsManager_RaceResultsChanged(Race obj)
        {
            Refresh();
        }

        protected override void Recalculate()
        {
            EventManager.ResultManager.Recalculate(Round);
            RequestLayout();
        }

        public override string MakeCSV()
        {
            string csv = "";
            foreach (PilotPointsNode pn in PilotNodes.OrderBy(pn => pn.Bounds.Y))
            {
                string line = ",";
                if (pn.Pilot != null)
                {
                    line = pn.Pilot.Name + ",";
                    foreach (TextNode tn in pn.ResultNodes)
                    {
                        line += tn.Text + ",";
                    }
                    line += pn.TotalPoints.ToString();

                    csv += line + "\n";
                }
            }

            return csv;
        }

        public override void UpdateNodes()
        {
            IEnumerable<Race> races = EventManager.ResultManager.GetRoundPointRaces(Round);
            IEnumerable<Pilot> pilots = races.SelectMany(r => r.Pilots).Where(p => !p.PracticePilot).Distinct();

            SetSubHeadingRounds(races);

            if (!PilotNodes.Any(pcn => pcn.Heading))
            {
                PilotPointsNode headingNode = new PilotPointsNode(EventManager, null);
                contentContainer.AddChild(headingNode);
            }

            foreach (Pilot pilot in pilots)
            {
                PilotPointsNode pn = PilotNodes.FirstOrDefault(pan => pan.Pilot == pilot);
                if (pn == null)
                {
                    pn = new PilotPointsNode(EventManager, pilot);
                    pn.ResultEdited += ResultEdited;
                    contentContainer.AddChild(pn);
                }
            }

            foreach (PilotPointsNode pcn in PilotNodes.ToArray())
            {
                if (pcn.Heading)
                {
                    continue;
                }

                if (!pilots.Contains(pcn.Pilot))
                {
                    pcn.Dispose();
                }
            }

            Round[] rounds = races.Select(r => r.Round).Distinct().ToArray();

            bool roundPositionRollover = rounds.Any(r => r.RoundType == Round.RoundTypes.Final);
            if (Round.PointSummary != null)
            {
                roundPositionRollover &= Round.PointSummary.RoundPositionRollover;
            }

            if (EventManager.ResultManager.GetRollOverRound(rounds.FirstOrDefault()) == null)
            {
                roundPositionRollover = false;
            }

            foreach (PilotPointsNode sn in PilotNodes)
            {
                if (sn.Heading)
                {
                    sn.MakeHeadings(rounds, roundPositionRollover);
                }
                else
                {
                    Pilot p = sn.Pilot;

                    IEnumerable<Race> pilotRaces = races.Where(r => r.HasPilot(p));
                    if (races.Any())
                    {
                        List<Result> results = EventManager.ResultManager.GetResults(pilotRaces, p, roundPositionRollover).ToList();

                        int totalPoints = EventManager.ResultManager.GetPointsTotal(results, Round.PointSummary.DropWorstRound);

                        Race.Brackets bracket = pilotRaces.First().Bracket;
                        sn.UpdateScoreText(rounds, bracket, results.ToArray(), totalPoints, roundPositionRollover);
                    }
                }
            }
        }

        private void ResultEdited(Result obj)
        {
            Refresh();
            RequestLayout();
        }

        public override IEnumerable<PilotPointsNode> Order(IEnumerable<PilotPointsNode> nodes)
        {
            return nodes.OrderByDescending(d => d.Heading)
                        .ThenBy(d => d.Bracket)
                        .ThenByDescending(d => d.TotalPoints)
                        .ThenBy(d => EventManager.LapRecordManager.GetPBTimePosition(d.Pilot));
        }

        public override void UpdatePositions(IEnumerable<PilotPointsNode> nodes)
        {
            int position = 0;
            int lastScore = 0;
            int inARow = 0;
            Race.Brackets lastBracket = Race.Brackets.None;


            foreach (PilotPointsNode ppn in nodes)
            {
                if (lastBracket != ppn.Bracket)
                {
                    lastBracket = ppn.Bracket;
                    position = 0;
                    inARow = 0;
                }

                if (!ppn.Heading)
                {
                    if (lastScore != ppn.TotalPoints)
                    {
                        position++;
                        position += inARow;
                        lastScore = ppn.TotalPoints;
                        inARow = 0;
                    }
                    else
                    {
                        inARow++;
                    }
                }

                ppn.Position = position;
            }
        }

        public override void EditSettings()
        {
            ObjectEditorNode<PointSummary> editor = new ObjectEditorNode<PointSummary>(Round.PointSummary);
            GetLayer<PopupLayer>().Popup(editor);
            editor.OnOK += (a) =>
            {
                SaveRound();
                Recalculate();
            };
        }
    }

    public class PilotPointsNode : EventPilotNode
    {
        public int TotalPoints { get; private set; }

        private TextNode totalScoreNode;

        public IEnumerable<ResultNode> ResultNodes { get { return roundScoreContainer.Children.OfType<ResultNode>(); } }

        public event Action<Result> ResultEdited;

        public PilotPointsNode(EventManager eventManager, Pilot pilot)
            :base (eventManager, pilot)
        {    
            totalScoreNode = new TextNode("0", Theme.Current.Rounds.Text.XNA);
            totalScoreNode.Alignment = RectangleAlignment.BottomRight;
            totalScoreNode.Style.Bold = true;
            AddChild(totalScoreNode);
        }

        public void MakeHeadings(IEnumerable<Round> rounds, bool rollover)
        {
            totalScoreNode.Remove();
            roundScoreContainer.ClearDisposeChildren();

            if (rollover)
            {
                TextNode pointNode = new TextNode("RO", Theme.Current.Rounds.Text.XNA);
                pointNode.Alignment = RectangleAlignment.CenterRight;
                roundScoreContainer.AddChild(pointNode);
            }

            foreach (Round round in rounds)
            {
                string text = round.ToStringShort();
                TextNode pointNode = new TextNode(text, Theme.Current.Rounds.Text.XNA);
                pointNode.Alignment = RectangleAlignment.CenterRight;
                roundScoreContainer.AddChild(pointNode);
            }

            roundScoreContainer.AddChild(totalScoreNode);

            totalScoreNode.Text = "Total";
            positionNode.Text = "Pos.";
            int roundCount = rounds.Count() + 1;
            if (rollover)
                roundCount++;
        }

        public void UpdateScoreText(IEnumerable<Round> rounds, Race.Brackets bracket, IEnumerable<Result> results, int total, bool rollover)
        {
            totalScoreNode.Remove();
            roundScoreContainer.ClearDisposeChildren();
            Bracket = bracket;

            HasRaced = false;
            TotalPoints = total;
            
            if (rollover)
            {
                Result rollOverPoints = results.FirstOrDefault(r => r.ResultType == Result.ResultTypes.RoundRollOver);
                if (rollOverPoints != null)
                {
                    ResultNode pointNode = new ResultNode(rollOverPoints);
                    pointNode.ResultEdited += PointNode_ResultEdited;
                    roundScoreContainer.AddChild(pointNode);
                }
                else
                {
                    ResultNode n = new ResultNode(null);
                    roundScoreContainer.AddChild(n);
                }
            }

            foreach (Round round in rounds)
            {
                Result point = results.FirstOrDefault(r => r.Round == round && r.ResultType == Result.ResultTypes.Race);

                if (point != null)
                {
                    HasRaced = true;
                }

                ResultNode pointNode = new ResultNode(point);
                pointNode.ResultEdited += PointNode_ResultEdited;
                roundScoreContainer.AddChild(pointNode);
            }

            int roundCount = rounds.Count() + 1;
            if (rollover)
                roundCount++;

            roundScoreContainer.AddChild(totalScoreNode);

            totalScoreNode.Text = string.Format("{0,3}", TotalPoints);
        }

        protected override int GetItemWidth(Node node)
        {
            if (node != totalScoreNode && node != positionNode)
            {
                return 25;
            }

            return base.GetItemWidth(node);
        }


        private void PointNode_ResultEdited(Result obj)
        {
            ResultEdited?.Invoke(obj);
        }

        public override string ToString()
        {
            return base.ToString() + " " + Pilot.Name + " " + roundScoreContainer.ChildCount;
        }
    }

    public class ResultNode : TextEditNode
    {
        public Result Result { get; private set; }

        public event Action<Result> ResultEdited;

        private int original;

        public ResultNode(Result result)
            :base("", Theme.Current.Rounds.Text.XNA)
        {
            Alignment = RectangleAlignment.CenterRight;
            Result = result;

            if (result == null)
            {
                original = 0;
                Text = "-";
                CanEdit = false;
            }
            else if (Result.Points == 0)
            {
                original = 0;
                Text = "-";
            }
            else
            {
                original = result.Points;
                Text = Result.Points.ToString();
            }

            TextChanged += ResultNode_TextChanged;

            LostFocus += ResultNode_LostFocus;
        }

        private void ResultNode_LostFocus(string obj)
        {
            if (Result.Points != original)
            {
                ResultEdited?.Invoke(Result);
            }
        }

        private void ResultNode_TextChanged(string obj)
        {
            int points;
            if (int.TryParse(obj, out points))
            {
                Result.Points = points;
                Text = points.ToString();

                using (Database db = new Database())
                {
                    db.Results.Update(Result);
                }
            }
        }
    }
}
