﻿using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tools
{
    public struct Size
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public float AspectRatio
        {
            get
            {
                if (Height == 0)
                    return 0;

                return Width / (float)Height;
            }
        }

        public Size(int width, int height)
        {
            this.Width = width;
            this.Height = height;
        }

        public override string ToString()
        {
            return Width + " " + Height;
        }
    }

    public struct RectangleF
    {
        public float X { get; set; }
        public float Y { get; set; }

        public float Width { get; set; }
        public float Height { get; set; }

        public float Bottom { get { return Y + Height; } }
        public float Right { get { return X + Width; } }

        public float CenterX { get { return X + (Width / 2); } }
        public float CenterY { get { return Y + (Height / 2); } }

        public RectangleF(float x, float y, float width, float height)
        {
            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
        }

        public RectangleF(float width, float height)
        {
            Width = width;
            Height = height;

            X = (1 - width) / 2;
            Y = (1 - height) / 2;
        }

        public override string ToString()
        {
            return X.ToString("0.##") + " " + Y.ToString("0.##") + " " + Width.ToString("0.##") + " " + Height.ToString("0.##");
        }

        public override bool Equals(object obj)
        {
            if (obj is RectangleF)
            {
                RectangleF other = (RectangleF)obj;

                return other.X == X &&
                       other.Y == Y &&
                       other.Width == Width &&
                       other.Height == Height;
            }

            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() +
            Y.GetHashCode() +
            Width.GetHashCode() +
            Height.GetHashCode();
        }

        public static bool operator ==(RectangleF a, RectangleF b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RectangleF a, RectangleF b)
        {
            return !a.Equals(b);
        }


        public RectangleF Scale(float scale)
        {
            return Scale(scale, scale);
        }

        public RectangleF Scale(float scaleX, float scaleY)
        {
            float newWidth = Width * scaleX;
            float newHeight = Height * scaleY;

            float diffWidth = Width - newWidth;
            float diffHeight = Height - newHeight;

            return new RectangleF(X + (diffWidth / 2),
                                  Y + (diffHeight / 2),
                                  newWidth,
                                  newHeight);
        }

        public Microsoft.Xna.Framework.Rectangle ToRectangle()
        {
            return new Microsoft.Xna.Framework.Rectangle((int)X, (int)Y, (int)Width, (int)Height);
        }
    }


    public struct RectangleI
    {
        public int X { get; set; }
        public int Y { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }

        public int Bottom { get { return Y + Height; } }
        public int Right { get { return X + Width; } }

        public int CenterX { get { return X + (Width / 2); } }
        public int CenterY { get { return Y + (Height / 2); } }

        public RectangleI(int x, int y, int width, int height)
        {
            this.X = x;
            this.Y = y;
            this.Width = width;
            this.Height = height;
        }

        public RectangleI(int width, int height)
        {
            Width = width;
            Height = height;

            X = (1 - width) / 2;
            Y = (1 - height) / 2;
        }

        public override string ToString()
        {
            return X.ToString("0.##") + " " + Y.ToString("0.##") + " " + Width.ToString("0.##") + " " + Height.ToString("0.##");
        }

        public override bool Equals(object obj)
        {
            if (obj is RectangleI)
            {
                RectangleI other = (RectangleI)obj;

                return other.X == X &&
                       other.Y == Y &&
                       other.Width == Width &&
                       other.Height == Height;
            }

            return base.Equals(obj);
        }

        public override int GetHashCode()
        {
            return X.GetHashCode() +
            Y.GetHashCode() +
            Width.GetHashCode() +
            Height.GetHashCode();
        }

        public static bool operator ==(RectangleI a, RectangleI b)
        {
            return a.Equals(b);
        }

        public static bool operator !=(RectangleI a, RectangleI b)
        {
            return !a.Equals(b);
        }


        public RectangleI Scale(int scale)
        {
            return Scale(scale, scale);
        }

        public RectangleI Scale(int scaleX, int scaleY)
        {
            int newWidth = Width * scaleX;
            int newHeight = Height * scaleY;

            int diffWidth = Width - newWidth;
            int diffHeight = Height - newHeight;

            return new RectangleI(X + (diffWidth / 2),
                                  Y + (diffHeight / 2),
                                  newWidth,
                                  newHeight);
        }

        public Microsoft.Xna.Framework.Rectangle ToRectangle()
        {
            return new Microsoft.Xna.Framework.Rectangle((int)X, (int)Y, (int)Width, (int)Height);
        }
    }
}
