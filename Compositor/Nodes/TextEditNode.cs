﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Xna.Framework.Input;
using Composition.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Tools;
using Composition.Text;

namespace Composition.Nodes
{
    public class TextEditNode : TextNode, IUpdateableNode
    {
        public event Action<string> TextChanged;
        public event Action<string> LostFocus;

        private bool showPipe;

        private int cursorIndex;

        public event System.Action OnReturn;
        public event System.Action OnTab;

        public bool CanEdit { get; set; }

        protected ITextRenderer cursorRenderer;

        public TextEditNode(string text, Color textColor) 
            : base(text, textColor)
        {
            OnFocusChanged += TextEditNode_OnFocusChanged;
            Alignment = RectangleAlignment.CenterLeft;
            CanEdit = true;
        }

        public override void Dispose()
        {
            base.Dispose();
            if (cursorRenderer != null)
            {
                cursorRenderer.Dispose();
                cursorRenderer = null;
            }
        }

        private void TextEditNode_OnFocusChanged(bool obj)
        {
            if (!obj)
            {
                LostFocus?.Invoke(Text);
            }
        }

        public override bool Contains(Point point)
        {
            return Bounds.Contains(point);
        }

        public void Update(GameTime gameTime)
        {
            bool newShowPipe = showPipe;
            newShowPipe = HasFocus && gameTime.TotalGameTime.TotalMilliseconds % 500 < 250;

            if (newShowPipe != showPipe)
            {
                showPipe = newShowPipe;
                RequestRedraw();
            }
        }

        public override void PreProcess(Drawer id)
        {
            base.PreProcess(id);

            if (cursorRenderer == null && textRenderer != null)
            {
                int height = Bounds.Height;
                if (OverrideHeight.HasValue)
                {
                    height = OverrideHeight.Value;
                }

                ITextRenderer cursorRenderer = PlatformTools.CreateTextRenderer();
                if (cursorRenderer != null)
                {
                    cursorRenderer.CreateGeometry(0, height, "|", Style);
                    cursorRenderer.CreateTextures(id);
                }
                this.cursorRenderer = cursorRenderer;
            }
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            base.Draw(id, parentAlpha);

            DebugTimer.DebugStartTime(this);

            if (showPipe)
            {
                if (cursorRenderer == null)
                {
                    id.PreProcess(this);
                }
                else
                {
                    Point cursorPosition = CharacterPosition(cursorIndex);
                    cursorRenderer.Draw(id, new Rectangle(Bounds.X + cursorPosition.X - 2, Bounds.Y + cursorPosition.Y, cursorRenderer.TextSize.Width, cursorRenderer.TextSize.Height), RectangleAlignment.Center, Composition.Text.Scale.Disallowed, Color.White, Alpha);
                }
            }
            DebugTimer.DebugEndTime(this);
        }

        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
            if (CanEdit && mouseInputEvent.ButtonState == ButtonStates.Pressed && mouseInputEvent.Button == MouseButtons.Left)
            {
                HasFocus = true;
                RequestRedraw();
                
                int index = HitCharacterIndex(mouseInputEvent); 
                if (index >= 0)
                {
                    cursorIndex = index;
                }
            }
            return base.OnMouseInput(mouseInputEvent);
        }

        public override bool OnKeyboardInput(KeyboardInputEvent inputEvent)
        {
            bool control = CompositorLayer.InputEventFactory.AreControlKeysDown();

            if (inputEvent.ButtonState == ButtonStates.Pressed || inputEvent.ButtonState == ButtonStates.Repeat)
            {
                int activeIndex = cursorIndex;
                if (activeIndex > Text.Length)
                {
                    activeIndex = text.Length;
                }

                string before = Text.Substring(0, activeIndex);
                string after = Text.Substring(before.Length);


                string input = "";
                char c = inputEvent.GetChar();

                if (control)
                {
                    if (inputEvent.Key == Keys.V)
                    {
                        input = PlatformTools.Clipboard.GetText();
                    }
                }
                else if (c != 0)
                {
                    input += c;
                }


                if (!string.IsNullOrEmpty(input))
                {
                    Text = before + input + after;
                    cursorIndex++;
                }

                switch (inputEvent.Key)
                {
                    case Keys.Back:
                        if (before.Length > 0)
                        {
                            Text = before.Substring(0, before.Length - 1) + after;
                            cursorIndex--;
                        }
                        break;

                    case Keys.Delete:
                        if (after.Length > 0)
                        {
                            Text = before + after.Substring(1);
                        }
                        break;

                    case Keys.Left:
                        cursorIndex--;
                        if (cursorIndex < 0) 
                            cursorIndex = 0;
                        break;

                    case Keys.Right:
                        cursorIndex++;
                        if (cursorIndex > Text.Length) 
                            cursorIndex = Text.Length;
                        break;

                    case Keys.Enter:
                        OnReturn?.Invoke();
                        HasFocus = false;
                        break;

                    case Keys.Tab:
                        OnTab?.Invoke();
                        HasFocus = false;
                        break;

                    case Keys.Home:
                        cursorIndex = 0;
                        break;

                    case Keys.End:
                        cursorIndex = Text.Length;
                        break;
                }
                RequestRedraw();
                TextChanged?.Invoke(Text);
                return true;
            }

            return base.OnKeyboardInput(inputEvent);
        }

        public int HitCharacterIndex(MouseInputEvent mouseInputEvent)
        {
            if (textRenderer != null)
            {
                Point point = new Point(mouseInputEvent.Position.X - Bounds.X, mouseInputEvent.Position.Y - Bounds.Y);

                return textRenderer.HitCharacterIndex(point);
            }
            return -1;
        }

        public Point CharacterPosition(int i)
        {
            if (textRenderer != null)
            {
                return textRenderer.CharacterPosition(i);
            }
            return Point.Zero;
        }
    }
}
