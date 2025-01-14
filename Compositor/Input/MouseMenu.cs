﻿using Composition.Layers;
using Composition.Nodes;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace Composition.Input
{
    public class MouseMenu : AnimatedNode
    {
        public Point Position { get { return CalleeArea.Location; } }
        public Rectangle CalleeArea { get; private set; }

        public MenuLayer MenuLayer { get; private set; }

        public IEnumerable<MenuItem> Items { get { return listNode.ChildrenOfType; }  }

        public int WidthPerChar { get; set; }

        public bool TopToBottom { get; set; }
        public bool LeftToRight { get; set; }

        public MouseMenu ParentMenu { get; private set; }
        private List<MouseMenu> childMenus;

        public bool Open { get; private set; }

        private ListNode<MenuItem> listNode;

        public int EdgePadding { get; set; }

        public MouseMenu OpenChildMenu
        {
            get
            {
                if (childMenus != null)
                {
                    return childMenus.FirstOrDefault(r => r.Open);
                }
                return null;
            }
        }

        public MouseMenu(Node creator)
            :this(creator.GetLayer<MenuLayer>())
        {
        }

        public MouseMenu(MenuLayer menuLayer)
        {
            MenuLayer = menuLayer;

            listNode = new ListNode<MenuItem>(MenuLayer.ScrollBar);
            AddChild(listNode);

            AnimationTime = TimeSpan.FromSeconds(0.1f);

            TopToBottom = true;
            LeftToRight = true;

            listNode.ItemHeight = 28;
            listNode.ItemPadding = 0;

            WidthPerChar = 12;
            AddChild(new ShadowNode());

            EdgePadding = 5;
        }

        public override void Dispose()
        {
            if (childMenus != null)
            {
                foreach (MouseMenu mm in childMenus)
                {
                    mm.Dispose();
                }
                childMenus = null;
            }
            base.Dispose();
        }

        private void ShowSubmenu(MouseMenu submenu, MenuItem caller)
        {
            if (OpenChildMenu == submenu)
                return;

            CloseChildMenu();

            if (caller != null)
            {
                Rectangle callArea = caller.Bounds;

                //Translate for rendernode..
                callArea.X += listNode.Bounds.X;
                callArea.Y += listNode.Bounds.Y;

                submenu.Show(callArea);
            }
        }

        public MouseMenu AddSubmenu<T>(string text, Action<T> action, params T[] values)
        {
            MouseMenu mouseMenu = new MouseMenu(MenuLayer);
            foreach (T t in values)
            {
                mouseMenu.AddItem(t.ToString(), () => { action(t); });
            }

            AddSubmenu(text, mouseMenu);
            return mouseMenu;
        }

        public MouseMenu AddSubmenu(string submenutitle)
        {
            MouseMenu menu = new MouseMenu(MenuLayer);
            AddSubmenu(submenutitle, menu);
            return menu;
        }

        public void AddSubmenu(string text, MouseMenu submenu)
        {
            submenu.TopToBottom = TopToBottom;
            submenu.LeftToRight = LeftToRight;
            submenu.ParentMenu = this;

            if (childMenus == null)
            {
                childMenus = new List<MouseMenu>();
            }
            childMenus.Add(submenu);

            MenuItem newItem = new MenuItem(text + " >>", MenuLayer.Background, MenuLayer.Hover, MenuLayer.Text);
            newItem.TextNode.Alignment = RectangleAlignment.CenterLeft;
            newItem.OnClick += (mie) =>
            {
                ShowSubmenu(submenu, newItem);
            };
            //textButtonNode.OnHover += (mie) =>
            //{
            //    ShowSubmenu(submenu, textButtonNode);
            //};
            listNode.AddChild(newItem);
        }

        public MenuItem AddItem(string text, System.Action action)
        {
            MenuItem newItem = new MenuItem(text, MenuLayer.Background, MenuLayer.Hover, MenuLayer.Text);
            newItem.TextNode.Alignment = RectangleAlignment.CenterLeft;
            newItem.OnClick += (mie) => 
            { 
                action();
                Close(); 
            };
            listNode.AddChild(newItem);
            return newItem;
        }

        public void AddItemConfirm(string text, System.Action action, bool enabled)
        {
            if (enabled)
            {
                AddItemConfirm(text, action);
            }
            else
            {
                AddDisabledItem(text);
            }
        }

        public void AddItemConfirm(string text, System.Action action)
        {
            MenuItem newItem = new MenuItem(text, MenuLayer.Background, MenuLayer.Hover, MenuLayer.Text);
            newItem.TextNode.Alignment = RectangleAlignment.CenterLeft;
            newItem.OnClick += (mie) => 
            { 
                Close(); 
                GetLayer<PopupLayer>().PopupConfirmation(text + "?", action);  
            };
            listNode.AddChild(newItem);
        }

        public void AddBlank()
        {
            AddDisabledItem("");
        }

        public void AddItem(string text, System.Action action, bool enabled)
        {
            if (enabled)
            {
                AddItem(text, action);
            }
            else
            {
                AddDisabledItem(text);
            }
        }

        public void AddDisabledItem(string text)
        {
            var tbn = AddItem(text, () => { });
            tbn.Enabled = false;
        }

        public int GetWidth()
        {
            MenuItem[] validItems = Items.Where(i => i.Text != null).ToArray();

            if (validItems.Any())
            {
                int chars = validItems.Select(b => b.Text.Length).Max();

                if (chars < 10)
                    chars = 10;

                return chars * WidthPerChar;
            }
            return 0;
        }

        public override Rectangle CalculateRelativeBounds(Rectangle parentBounds)
        {
            MenuItem[] validItems = Items.Where(i => i.Text != null).ToArray();

            if (validItems.Any())
            {
                Rectangle bounds = new Rectangle()
                {
                    X = LeftToRight ? CalleeArea.Right : CalleeArea.Left,
                    Y = TopToBottom ? CalleeArea.Top : CalleeArea.Bottom,
                    Width = GetWidth(),
                    Height = validItems.Count() * listNode.ItemHeightFull

                };

                // Keep on screen.
                if (bounds.Right > parentBounds.Right)
                {
                    LeftToRight = false;
                    bounds.X = CalleeArea.Left;
                }
                if (bounds.Bottom > parentBounds.Bottom && bounds.Y - bounds.Height > parentBounds.X)
                {
                    TopToBottom = false;
                }

                if (!TopToBottom)
                {
                    bounds.Y -= bounds.Height;
                }

                if (!LeftToRight)
                {
                    bounds.X -= bounds.Width;
                }

                if (bounds.Bottom > parentBounds.Bottom)
                {
                    bounds.Height = parentBounds.Bottom - (bounds.Y + EdgePadding);
                }

                if (bounds.Y < parentBounds.Y)
                {
                    bounds.Y += bounds.Height;
                }

                if (bounds.X < parentBounds.X)
                {
                    bounds.X += bounds.Width;
                }

                return bounds;
            }
            return base.CalculateRelativeBounds(parentBounds);
        }

        public void CloseChildMenu()
        {
            MouseMenu open = OpenChildMenu;
            if (open != null)
            {
                open.Open = false;
                open.Remove();
            }
        }

        public void Close()
        {
            if (ParentMenu != null)
            {
                ParentMenu.Close();
            }

            if (childMenus != null)
            {
                foreach (MouseMenu mm in childMenus)
                {
                    mm.ParentMenu = null;
                    mm.Close();
                }
            }
            
            Open = false;

            if (CompositorLayer != null)
            {
                CompositorLayer.CleanUp(this);
            }
            else
            {
                Dispose();
            }
        }

        public void Show(Node button)
        {
            Show(button.Bounds.Location);
        }

        public void Show(MouseInputEvent mouseEvent)
        {
            Show(mouseEvent.ScreenPosition);
        }

        public void Show(int x, int y)
        {
            Show(new Point(x, y));
        }

        public void Show(Point pos)
        {
            Show(new Rectangle(pos, new Point(1, 1)));
        }

        public void Show(Rectangle callee)
        {
            CalleeArea = callee;
            Bounds = new Rectangle(callee.Location, new Point(1, 1));

            if (Items.Any())
            {
                MenuLayer.Root.AddChild(this);
                RequestLayout();
                RequestRedraw();
                Open = true;
            }
            else
            {
                Dispose();
            }
        }

        public override bool IsAnimatingSize()
        {
            return false;
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            id.PushClipRectangle(Bounds);
            base.Draw(id, parentAlpha);
            id.PopClipRectangle();
        }
    }

    public class MenuItem : TextButtonNode
    {
        public MenuItem(string text, Color background, Color hover, Color textColor) 
            : base(text, background, hover, textColor)
        {
        }
    }

}
