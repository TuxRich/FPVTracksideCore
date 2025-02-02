﻿using Composition;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using RaceLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace UI.Nodes
{
    public class LapCountSummaryNode : PilotRoundsResultTable
    {
        public ResultManager PointsManager { get { return eventManager.ResultManager; } }

        public LapCountSummaryNode(EventManager eventManager)
            :base(eventManager, "Lap Count")
        {
            eventManager.OnPilotRefresh += Refresh;
        }


        public override void Dispose()
        {
            eventManager.OnPilotRefresh -= Refresh;
            base.Dispose();
        }

        public override void CreateHeadings(Node container, out Round[] rounds, out int column)
        {
            rounds = eventManager.Event.Rounds.OrderBy(r => r.Order).ThenBy(r => r.RoundNumber).ToArray();

            column = 0;
            foreach (Round r in rounds)
            {
                column++;
                int ca = column;

                TextButtonNode headingText = new TextButtonNode(r.ToStringShort(), Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
                headingText.TextNode.Alignment = RectangleAlignment.TopRight;
                headingText.OnClick += (mie) => { columnToOrderBy = ca; Refresh(); };
                container.AddChild(headingText);
            }

            int c = column + 1;
            TextButtonNode total = new TextButtonNode("Total", Theme.Current.InfoPanel.Foreground.XNA, Theme.Current.Hover.XNA, Theme.Current.InfoPanel.Text.XNA);
            total.TextNode.Alignment = RectangleAlignment.TopRight;
            total.OnClick += (mie) => { columnToOrderBy = c; Refresh(); };
            container.AddChild(total);
        }

        public override void SetOrder()
        {
            // order them
            if (columnToOrderBy == 0)
            {
                rows.SetOrder<PilotResultNode, string>(pa => pa.Pilot.Name);
            }
            else
            {
                rows.SetOrder<PilotResultNode, double>(pa =>
                {
                    return -pa.GetValue(columnToOrderBy);
                });
            }
        }

        protected override void SetResult(PilotResultNode pilotResNode, Pilot pilot, Round[] rounds)
        {
            List<Node> nodes = new List<Node>();

            int count = 0;

            foreach (Round round in rounds)
            {
                Race race = eventManager.RaceManager.GetRaces(r => r.Round == round && r.HasPilot(pilot)).FirstOrDefault();

                TextNode rn = new TextNode("", Theme.Current.Rounds.Text.XNA);
                rn.Alignment = RectangleAlignment.TopRight;
                nodes.Add(rn);

                if (race != null)
                {
                    int laps = race.GetValidLapsCount(pilot, false);
                    rn.Text = laps.ToString();

                    count += laps;
                }
            }

            TextNode t = new TextNode(count.ToString(), Theme.Current.Rounds.Text.XNA);
            t.Alignment = RectangleAlignment.TopRight;
            nodes.Add(t);

            pilotResNode.Set(pilot, nodes);
        }
    }
}
