﻿using Composition.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Tools;

namespace Composition.Nodes
{
    public class RenderTargetNode : ImageNode, IPreProcessable, IUpdateableNode
    {
        private RenderTarget2D renderTarget
        {
            get
            {
                return (RenderTarget2D)texture;
            }
            set
            {
                texture = value;
            }
        }

        private object renderTargetLock;
        private Drawer drawer;

        private Size size;
        public Size Size
        {
            get => size;
            set
            {
                if (size.Width != value.Width || size.Height != value.Height)
                {
                    size = value;
                    SetAspectRatio(size);
                    RequestRedraw();
                }
            }
        }

        public bool LayoutDefinesSize { get; set; }

        private bool hasLayedOut;
        private Rectangle lastLayoutBounds;

        public ScrollerNode Scroller { get; private set; }

        private bool disposed;

        public HoverNode HoverNode { get; set; }

        private int lastDrawFrame;

        public RenderTargetNode()
            : this(128, 128)
        {
            LayoutDefinesSize = true;
            hasLayedOut = false;
        }

        public RenderTargetNode(int width, int height)
        {
            Size = new Size(width, height);
            renderTargetLock = new object();
            Scroller = new ScrollerNode(this, ScrollerNode.Types.VerticalRight);
            HoverNode = null;
        }

        public override void Dispose()
        {
            disposed = true;

            lock (renderTargetLock)
            {
                if (renderTarget != null)
                {
                    renderTarget.Dispose();
                    renderTarget = null;
                }

                if (drawer != null)
                {
                    drawer.Dispose();
                    drawer = null;
                }
            }

            Scroller.Dispose();
            base.Dispose();
        }

        // Need to use basebounds as ImageNode over-rides the Bounds size based on image size. But our image size is dynamic..
        public override bool Contains(Point point)
        {
            return BaseBounds.Contains(point);
        }

        public override void Layout(Rectangle parentBounds)
        {
            Bounds = CalculateRelativeBounds(parentBounds);

            bool isAnimatingSize = IsAnimatingSize();

            if (lastLayoutBounds.Width != BaseBounds.Width || lastLayoutBounds.Height != BaseBounds.Height || NeedsLayout)
            {
                if (LayoutDefinesSize)
                {
                    if (!isAnimatingSize)
                    {
                        Size = new Size(BaseBounds.Width, BaseBounds.Height);
                        hasLayedOut = true;

                        LayoutChildren(new Rectangle(0, 0, Size.Width, Size.Height));
                        NeedsLayout = false;
                        NeedsDraw = true;
                        lastLayoutBounds = BaseBounds;
                    }
                }
                else
                {
                    hasLayedOut = true;
                    LayoutChildren(new Rectangle(0, 0, Size.Width, Size.Height));
                    NeedsLayout = false;
                    NeedsDraw = true;
                    lastLayoutBounds = BaseBounds;
                }

            }

            if (renderTarget != null && !isAnimatingSize)
            {
                Rectangle actualBounds = new Rectangle(Bounds.X, Bounds.Y, Size.Width, Size.Height);
                Scroller.Layout(actualBounds);

                switch (Scroller.ScrollType)
                {
                    case ScrollerNode.Types.Horizontal:
                        Scroller.ViewSizePixels = Bounds.Width;
                        Scroller.ContentSizePixels = Size.Width;
                        break;
                    case ScrollerNode.Types.VerticalLeft:
                    case ScrollerNode.Types.VerticalRight:
                        Scroller.ViewSizePixels = Bounds.Height;
                        Scroller.ContentSizePixels = Size.Height;
                        break;
                }
            }
        }

        public override void Draw(Drawer id, float parentAlpha)
        {
            lastDrawFrame = id.FrameCount;

            DebugTimer.DebugStartTime(this);

            float alpha = parentAlpha * Alpha;
            if (Tint.A != 255)
            {
                alpha *= Tint.A / 255.0f;
            }
            lock (renderTargetLock)
            {
                if (renderTarget != null)
                {
                    Rectangle sourceBounds = new Rectangle(0, 0, renderTarget.Width, renderTarget.Height);
                    Rectangle destBounds = Bounds;

                    if (!CanScale)
                    {
                        destBounds.Width = renderTarget.Width;
                        destBounds.Height = renderTarget.Height;

                        if (Scroller.Visible)
                        {
                            switch (Scroller.ScrollType)
                            {
                                case ScrollerNode.Types.Horizontal:
                                    sourceBounds.X += (int)Scroller.CurrentScrollPixels;

                                    if (sourceBounds.Width > Scroller.ViewSizePixels)
                                    {
                                        sourceBounds.Width = (int)Scroller.ViewSizePixels;
                                        destBounds.Width = (int)Scroller.ViewSizePixels;
                                    }
                                    break;
                                case ScrollerNode.Types.VerticalLeft:
                                case ScrollerNode.Types.VerticalRight:
                                    sourceBounds.Y += (int)Scroller.CurrentScrollPixels;

                                    if (sourceBounds.Height > Scroller.ViewSizePixels)
                                    {
                                        sourceBounds.Height = (int)Scroller.ViewSizePixels;
                                        destBounds.Height = (int)Scroller.ViewSizePixels;
                                    }
                                    break;
                            }
                        }
                    }

                    try
                    {
                        id.Draw(renderTarget, sourceBounds, destBounds, Tint, alpha);

                        HoverNode hoverNode = HoverNode;
                        if (hoverNode != null)
                        {
                            // Draw a hovernode over the top of everything
                            hoverNode.RenderTargetDraw(id, parentAlpha);
                        }
                    }
                    catch
                    {
                        if (renderTarget != null)
                        {
                            renderTarget.Dispose();
                            renderTarget = null;
                        }

                        if (drawer != null)
                        {
                            drawer.Dispose();
                            drawer = null;
                        }
                    }
                }
            }

            Scroller.Draw(id, parentAlpha);
            DebugTimer.DebugEndTime(this);
        }

        public void Update(GameTime gameTime)
        {
            DebugTimer.DebugStartTime(this);
            if (NeedsLayout && Parent != null)
            {
                Layout(Parent.Bounds);
                NeedsLayout = false;
                NeedsDraw = true;
            }

            bool canRender = !LayoutDefinesSize || (LayoutDefinesSize && hasLayedOut);
            if (canRender && NeedsDraw && !disposed)
            {
                if (lastDrawFrame == CompositorLayer.FrameNumber)
                {
                    lock (renderTargetLock)
                    {
                        if (drawer == null)
                        {
                            drawer = new Drawer(CompositorLayer.GraphicsDevice, true);
                            drawer.CanPreProcess = false;
                        }

                        if (renderTarget != null && !IsAnimating() && (Size.Width != renderTarget.Width || Size.Height != renderTarget.Height))
                        {
                            renderTarget.Dispose();
                            renderTarget = null;
                        }

                        if (renderTarget == null && Size.Width > 0 && Size.Height > 0)
                        {
                            renderTarget = new RenderTarget2D(drawer.GraphicsDevice, Math.Min(4096, Size.Width), Math.Min(4096, Size.Height));
                            NeedsDraw = true;
                        }

                        if (renderTarget != null)
                        {
                            drawer?.PreProcess(this);
                        }
                    }
                }
            }
            DebugTimer.DebugEndTime(this);
        }

        public void PreProcess(Drawer id)
        {
            DebugTimer.DebugStartTime(this);
            DrawToTexture(id);
            NeedsDraw = false;
            DebugTimer.DebugEndTime(this);
        }

        protected void DrawToTexture(Drawer id)
        {
            lock (renderTargetLock)
            {
                try
                {
                    // Set the render target
                    CompositorLayer.GraphicsDevice.SetRenderTarget(renderTarget);
                    CompositorLayer.GraphicsDevice.Clear(Color.Transparent);
//#if DEBUG
//                    Random r = new Random();
//                    CompositorLayer.GraphicsDevice.Clear(new Color((float)r.NextDouble(), (float)r.NextDouble(), (float)r.NextDouble()));
//#endif
                    if (id != null)
                    {
                        id.Begin();
                        DrawContent(id);
                        id.End();
                    }
                }
                catch
                {
                    if (renderTarget != null)
                    {
                        renderTarget.Dispose();
                        renderTarget = null;
                    }

                    if (drawer != null)
                    {
                        drawer.Dispose();
                        drawer = null;
                    }
                }
                finally
                {
                    // Drop the render target
                    CompositorLayer.GraphicsDevice.SetRenderTarget(null);
                }
            }
            base.RequestRedraw();
        }

        protected virtual void DrawContent(Drawer id)
        {
            DrawChildren(id, 1);
        }

        public override void RequestLayout()
        {
            NeedsLayout = true;

            // Layouts still need to go up
            base.RequestLayout();
        }

        public override void RequestRedraw()
        {
            NeedsDraw = true;

            // Layouts still need to go up
            base.RequestRedraw();
        }

        // Mouse events all need to be translated.
        public override bool OnMouseInput(MouseInputEvent mouseInputEvent)
        {
#if DEBUG
            if (mouseInputEvent.Button == MouseButtons.Right && mouseInputEvent.ButtonState == ButtonStates.Pressed)
            {
                if (texture != null && Keyboard.GetState().IsKeyDown(Keys.LeftAlt))
                {
                    string filename = Address;
                    System.Text.RegularExpressions.Regex rgx = new System.Text.RegularExpressions.Regex("[^a-zA-Z0-9 -]");
                    filename = rgx.Replace(filename, "");

                    texture.SaveAsPng(filename + ".png");
                }
            }
#endif

            if (Scroller.OnMouseInput(mouseInputEvent))
            {
                return true;
            }

            if (BaseBounds.Contains(mouseInputEvent.Position) || mouseInputEvent is MouseInputLeaveEvent)
            {
                MouseInputEvent translated = Translate(mouseInputEvent);
                return base.OnMouseInput(translated);
            }
            return false;
        }

        public MouseInputEvent Translate(MouseInputEvent input)
        {
            Point translation = new Point(-Bounds.X, -Bounds.Y);

            // Scroller
            switch (Scroller.ScrollType)
            {
                case ScrollerNode.Types.Horizontal:
                    translation.X += (int)Scroller.CurrentScrollPixels;
                    break;
                case ScrollerNode.Types.VerticalLeft:
                case ScrollerNode.Types.VerticalRight:
                    translation.Y += (int)Scroller.CurrentScrollPixels;
                    break;
            }

            MouseInputEvent output = new MouseInputEvent(input, translation);

            if (input is MouseInputEnterEvent)
            {
                output = new MouseInputEnterEvent(output);
            }
            else if (input is MouseInputEnterEvent)
            {
                output = new MouseInputEnterEvent(output);
            }
            return output;
        }

        public override bool OnDrop(MouseInputEvent mouseInputEvent, Node node)
        {
            MouseInputEvent translated = Translate(mouseInputEvent);
            return base.OnDrop(translated, node);
        }

        // These should all just return false, as the render target breaks the animation knowledge chain.
        public override bool IsAnimating()
        {
            return false;
        }

        public override bool IsAnimatingInvisiblity()
        {
            return false;
        }

        public override bool IsAnimatingSize()
        {
            return false;
        }
        public override IEnumerable<Node> GetRecursiveChildren()
        {
            yield return this;
        }
    }

}
