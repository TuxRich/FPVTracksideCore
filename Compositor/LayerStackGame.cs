﻿using Composition.Layers;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Composition
{
    public class LayerStackGame : Microsoft.Xna.Framework.Game
    {
        public LayerStackScaled LayerStack { get; private set; }
        public GraphicsDeviceManager GraphicsDeviceManager { get; private set; }

        public Color ClearColor { get; set; }

        private Rectangle lastBounds;

        public PlatformTools PlatformTools { get; protected set; }

        public LayerStackGame(PlatformTools platformTools)
        {
            Window.AllowUserResizing = true;
            GraphicsDeviceManager = new GraphicsDeviceManager(this);
            GraphicsDeviceManager.PreferredBackBufferHeight = 1000;
            GraphicsDeviceManager.PreferredBackBufferWidth = 1900;

            lastBounds = new Rectangle(0, 0, 0, 0);

            ClearColor = Color.CornflowerBlue;
            PlatformTools = platformTools;
        }

        protected override void LoadContent()
        {
            try
            {
                LayerStack = new LayerStackScaled(GraphicsDevice, this, PlatformTools);
            }
            catch(Exception e)
            {
                Tools.Logger.UI.LogException(this, e);
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            LayerStack?.Dispose();
            LayerStack = null;
        }

        public void Restart()
        {
            PlatformTools.Invoke(() =>
            {
                UnloadContent();

                if (LayerStack != null)
                {
                    LayerStack.Dispose();
                    LayerStack = null;
                }

                LoadContent();
            });
        }

        protected override void Update(GameTime gameTime)
        {
            
            try
            {
                Rectangle bounds = new Rectangle(0, 0, Window.ClientBounds.Width, Window.ClientBounds.Height);
                if (bounds != lastBounds)
                {
                    lastBounds = bounds;

                    GraphicsDeviceManager.PreferredBackBufferWidth = bounds.Width;
                    GraphicsDeviceManager.PreferredBackBufferHeight = bounds.Height;
                    GraphicsDeviceManager.ApplyChanges();
                }

                if (LayerStack != null)
                {
                    LayerStack.Update(gameTime);
                }

                base.Update(gameTime);
            }
            catch (Exception e)
            {
                Tools.Logger.UI.LogException(this, e);
            }
        }

        protected override void Draw(GameTime gameTime)
        {
            try
            {
                GraphicsDeviceManager.GraphicsDevice.Clear(ClearColor);
                if (LayerStack != null)
                {
                    LayerStack.Draw();
                }
                base.Draw(gameTime);
            }
            catch (Exception e)
            {
                Tools.Logger.UI.LogException(this, e);
            }
        }
    }
}
