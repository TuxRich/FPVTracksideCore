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
    public class RaceTimeNode : TextNode, IUpdateableNode
    {
        public RaceManager RaceManager { get; private set; }
        public string Prepend { get; set; }

        public RaceTimeNode(RaceManager raceManager, Color textColor) 
            : base("0.00", textColor)
        {
            RaceManager = raceManager;
            Prepend = "Time ";
            Alignment = RectangleAlignment.CenterRight;
        }

        public virtual void Update(GameTime gameTime)
        {
            SetTime(RaceManager.ElapsedTime);
        }

        public void SetTime(TimeSpan timespan)
        {
            Text = Prepend + timespan.ToStringRaceTime(1);
        }
    }
}
