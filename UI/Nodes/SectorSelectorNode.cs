using Composition.Input;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace UI.Nodes
{
    // Which times are shown: the laps, or the times through one of the sectors they're divided into (see
    // RaceManager.GetSplitTimingSystems). Shared, so the replay and the lap editor opened from it show the same.
    public class SectorSelection
    {
        // 0 for the laps.
        public int Sector { get; private set; }

        public event Action OnChanged;

        public void Select(int sector)
        {
            if (Sector == sector)
                return;

            Sector = sector;
            OnChanged?.Invoke();
        }

        public static string GetName(int sector)
        {
            if (sector == 0)
                return "Laps";

            return "Sector " + sector;
        }
    }

    public class SectorSelectorNode : TextButtonNode
    {
        public SectorSelection Selection { get; private set; }

        // The sectors the race's laps are divided into, 0 without laps (e.g. games).
        public int SectorCount { get; set; }

        public SectorSelectorNode(SectorSelection selection, Color background, Color hover, Color text)
            : base(SectorSelection.GetName(selection.Sector), background, hover, text)
        {
            Selection = selection;
            Selection.OnChanged += Selection_OnChanged;

            OnClick += SectorSelectorNode_OnClick;
        }

        public override void Dispose()
        {
            Selection.OnChanged -= Selection_OnChanged;
            base.Dispose();
        }

        private void Selection_OnChanged()
        {
            Text = SectorSelection.GetName(Selection.Sector);
        }

        private void SectorSelectorNode_OnClick(MouseInputEvent mie)
        {
            MouseMenu mm = new MouseMenu(this);
            for (int sector = 0; sector <= SectorCount; sector++)
            {
                int selected = sector;
                mm.AddItem(SectorSelection.GetName(selected), () => { Selection.Select(selected); });
            }
            mm.Show(this);
        }
    }
}
