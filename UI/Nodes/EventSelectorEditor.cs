﻿using Composition;
using Composition.Input;
using Composition.Nodes;
using Microsoft.Xna.Framework.Graphics;
using RaceLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace UI.Nodes
{
    public class EventSelectorEditor : ObjectEditorNode<Event>
    {
        public override bool GroupItems { get { return true; } }

        public event System.Action GeneralSettingsSaved;

        public TextButtonNode CloneButton { get; private set; }
        public TextButtonNode RecoverButton { get; private set; }
        public TextButtonNode SyncButton { get; private set; }
        public TextButtonNode LoginButton { get; private set; }
        
        public EventSelectorEditor(Texture2D logo, bool addRemove = true, bool cancelButton = false)
            : this(GetEvents(), addRemove, cancelButton)
        {
            heading.RelativeBounds = new RectangleF(0, 0.18f, 1, 0.05f);
            container.RelativeBounds = new RectangleF(0, heading.RelativeBounds.Bottom, 1, 1 - heading.RelativeBounds.Bottom);

            RelativeBounds = new RectangleF(0.25f, 0.01f, 0.5f, 0.98f);

            ColorNode colorNode = new ColorNode(Theme.Current.TopPanel.XNA);
            colorNode.RelativeBounds = new RectangleF(0, 0, 1, 0.175f);
            AddChild(colorNode);

            ImageNode logoNode = new ImageNode(logo);
            logoNode.RelativeBounds = new RectangleF(0, 0, 1, 0.99f);
            logoNode.Alignment = RectangleAlignment.TopCenter;
            colorNode.AddChild(logoNode);

            MenuButton menuButton = new MenuButton(Theme.Current.Hover.XNA, Theme.Current.Editor.Text.XNA);
            menuButton.RelativeBounds = new RectangleF(0.96f, 0.01f, 0.025f, 0.025f);
            menuButton.GeneralSettingsSaved += () => { GeneralSettingsSaved?.Invoke(); };
            menuButton.Restart += (e) =>
            {
                if (CompositorLayer.LayerStack.Game is UI.BaseGame)
                {
                    ((UI.BaseGame)CompositorLayer.LayerStack.Game).Restart(e);
                }
            };

            AddChild(menuButton);
        }

        public EventSelectorEditor(IEnumerable<Event> events, bool addRemove = true, bool cancelButtona = false)
           : base(events.Where(e => e.Enabled), addRemove, cancelButtona, false)
        {
            Text = "Select an event";
            OnOK += EventEditor_OnOK;

            Selected = Objects.OrderByDescending(e => e.LastOpened).FirstOrDefault();

            if (addRemove)
            {
                SyncButton = new TextButtonNode("Sync", ButtonBackground, ButtonHover, TextColor);
                buttonContainer.AddChild(SyncButton);
                SyncButton.Visible = false;

                LoginButton = new TextButtonNode("Login..", ButtonBackground, ButtonHover, TextColor);
                buttonContainer.AddChild(LoginButton);
                LoginButton.Visible = false;

                RecoverButton = new TextButtonNode("Recover", ButtonBackground, ButtonHover, TextColor);
                RecoverButton.OnClick += (mie) => { Recover(); };
                RecoverButton.Visible = events.Any(r => !r.Enabled);
                buttonContainer.AddChild(RecoverButton);

                CloneButton = new TextButtonNode("Clone", ButtonBackground, ButtonHover, TextColor);
                CloneButton.OnClick += (mie) =>
                {
                    if (Selected != null)
                    {
                        Event newEvent = Selected.Clone();
                        using (Database db = new Database())
                        {
                            db.Events.Insert(newEvent);
                        }

                        AddNew(newEvent);
                    }
                };
                buttonContainer.AddChild(CloneButton);

                Node[] buttons = new Node[] { SyncButton, addButton, removeButton, CloneButton, RecoverButton, cancelButton, okButton };
                buttonContainer.SetOrder(buttons);

                AlignVisibleButtons();
            }
            itemName.Visible = false;
        }

        public void DisableButtons()
        {
            foreach (Node n in buttonContainer.Children)
            {
                TextButtonNode tbn = n as TextButtonNode;
                if (tbn != null)
                {
                    tbn.Enabled = false;
                }
            }
        }

        protected override string ItemToGroupString(Event item)
        {
            return item.Month;
        }

        public override IEnumerable<Event> Order(IEnumerable<Event> ts)
        {
            return ts.OrderByDescending(e => e.Start);
        }

        protected override void Remove(MouseInputEvent mie)
        {
            Event selected = Selected;
            if (selected != null)
            {
                using (Database db = new Database())
                {
                    selected.Enabled = false;
                    db.Events.Update(selected);
                }
            }


            base.Remove(mie);
            RecoverButton.Visible = true;
            AlignVisibleButtons();
        }

        protected override void AddOnClick(MouseInputEvent mie)
        {
            Event eve;
            using (Database db = new Database())
            {
                Club club = db.GetDefaultClub();

                eve = new Event();
                eve.Channels = Channel.Read();
                eve.Club = club;
                db.Events.Insert(eve);
            }

            AddNew(eve);
        }

        private void Recover()
        {
            MouseMenu mouseMenu = new MouseMenu(this);
            mouseMenu.TopToBottom = false;

            Event[] all = GetEvents();

            IEnumerable<Event> notIn = all.Except(Objects);

            foreach (var obj in notIn)
            {
                mouseMenu.AddItem(obj.Name, () => { Recover(obj); });
            }

            mouseMenu.Show(RecoverButton);
        }

        private void Recover(Event recover)
        {
            recover.Enabled = true;

            List<Event> elist = Objects.ToList();
            elist.Add(recover);

            SetObjects(elist, true, true);
        }

        private static Event[] GetEvents()
        {
            Event[] events;
            using (Database db = new Database())
            {
                events = db.Events.Include(e => e.Channels).Include(e => e.Club).FindAll().OrderBy(e => e.Name).ToArray();

                Club club = db.Clubs.FindAll().FirstOrDefault();
                if (club == null)
                {
                    club = new Club();
                    club.SyncWith = SyncWith.FPVTrackside;
                    db.Clubs.Insert(club);
                }

                if (events.Length == 0)
                {
                    events = new Event[] { new Event() { Club = club, Channels = Channel.Read() } };
                    db.Events.Insert(events.First());
                }
            }
            return events;
        }

        private void EventEditor_OnOK(BaseObjectEditorNode<Event> obj)
        {
            using (Database db = new Database())
            {
                if (obj.Selected != null)
                {
                    obj.Selected.LastOpened = DateTime.Now;
                }
            }

            SaveChanges();
        }

        public void SaveChanges()
        {
            using (Database db = new Database())
            {
                foreach (var o in Objects)
                {
                    o.Enabled = true;
                }

                db.Events.Upsert(Objects);
            }
        }

        protected override void ChildValueChanged(Change newChange)
        {
            base.ChildValueChanged(newChange);
            foreach (var propertyNode in GetPropertyNodes)
            {
                CheckVisible(propertyNode, Selected);
            }
        }

        private void CheckVisible(PropertyNode<Event> propertyNode, Event even)
        {
            if (propertyNode == null)
                return;

            if (propertyNode.PropertyInfo.Name == "RaceStartIgnoreDetections")
            {
                propertyNode.Visible = even.PrimaryTimingSystemLocation == PrimaryTimingSystemLocation.EndOfLap;
            }
        }

        protected override PropertyNode<Event> CreatePropertyNode(Event obj, PropertyInfo pi)
        {
            if (obj.ExternalID == default)
            {
                CategoryAttribute ca = pi.GetCustomAttribute<CategoryAttribute>();
                if (ca != null && ca.Category == "MultiGP")
                    return null;
            }

            if (pi.Name == "ClubName")
            {
                string name = pi.GetValue(obj) as string;
                if (!string.IsNullOrEmpty(name))
                {
                    var property = new StaticTextPropertyNode<Event>(obj, pi, TextColor);

                    if (obj.SyncWith == SyncWith.MultiGP)
                    {
                        property.Name.Text = "Chapter";
                    }

                    return property;
                }
                else
                {
                    return null;
                }
            }

            PropertyNode<Event> n = base.CreatePropertyNode(obj, pi);
            if (n != null)
            {
                CheckVisible(n, obj);
            }

            return n;
        }
    }
}
