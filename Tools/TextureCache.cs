﻿using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tools
{
    public class TextureCache
    {
        private Dictionary<string, Texture2D> stringToTexture;

        public GraphicsDevice GraphicsDevice { get; private set; }

        public TextureCache(GraphicsDevice device)
        {
            GraphicsDevice = device;
            stringToTexture = new Dictionary<string, Texture2D>();
        }

        public Texture2D GetTextureFromColor(Color color)
        {
            if (stringToTexture == null)
            {
                stringToTexture = new Dictionary<string, Texture2D>();
            }

            string colorString = color.ToString();

            Texture2D texture;
            lock (stringToTexture)
            {
                if (!stringToTexture.TryGetValue(colorString, out texture))
                {
                    texture = TextureHelper.CreateTextureFromColor(GraphicsDevice, color);
                    stringToTexture.Add(colorString, texture);
                }
            }
            return texture;
        }

        public Texture2D GetTextureFromFilename(string filename)
        {
            return GetTextureFromFilename(filename, Color.Transparent);
        }

        public Texture2D GetTextureFromFilename(string filename, Color fallback)
        {
            if (stringToTexture == null)
            {
                stringToTexture = new Dictionary<string, Texture2D>();
            }

            // mac compatibility.
            filename = filename.Replace("\\", "/");

            Texture2D texture;
            lock (stringToTexture)
            {
                if (!stringToTexture.TryGetValue(filename, out texture))
                {
                    try
                    {
                        texture = TextureHelper.LoadTexture(GraphicsDevice, filename);
                        if (texture != null)
                        {
                            stringToTexture.Add(filename, texture);
                        }
                    }
                    catch
                    {
                        return GetTextureFromColor(fallback);
                    }
                }
            }
            return texture;
        }
    }
}
