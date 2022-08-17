using Ryujinx.Common;
using System;

namespace Ryujinx.Graphics.Texture
{
    public readonly struct Size
    {
        public int Width  { get; }
        public int Height { get; }
        public int Depth  { get; }

        public Size(int width, int height, int depth)
        {
            Width  = width;
            Height = height;
            Depth  = depth;
        }

        public override string ToString() => $"{Width}x{Height}x{Depth}";
    }
}