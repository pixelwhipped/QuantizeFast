// I (Ben Tarrant) had some free time so made a color quantizer that out-perfomed Median Cut and Octree(speed), Both optimized best I could.
// Yes I am aware it should be pallete, Bad habits.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Quantization
{
    public static partial class Quantizer
    {
        public static int FastQuantizationDepth = 32; //How far to traverse sorted pallet
        public struct RGBA8 //Could just be RGB but kept the Alpha for 32bit 
        {
            public byte A;
            public byte R;
            public byte G;
            public byte B;
        }
        public static (byte[] pixels, byte[] pallet) Quantize32BitFast(RGBA8[] pixels, int width, int height, int colourCount)
        {
            static int FindNearestColor(RGBA8 rgb, int index, int depth, List<(RGBA8 RGB, float Order, List<int> Indices)> palletList)
            {
                int shortestDistance = int.MaxValue;
                int p = 0;
                var end = Math.Max(index - depth, 0);
                var list = CollectionsMarshal.AsSpan(palletList);
                for (int i = index - 1; i >= end; i--)
                {
                    var pi = list[i];
                    int rd = rgb.R - pi.RGB.R;
                    int gd = rgb.G - pi.RGB.G;
                    int bd = rgb.B - pi.RGB.B;
                    int distance = rd * rd + gd * gd + bd * bd;
                    if (distance < shortestDistance)
                    {
                        p = i;
                        shortestDistance = distance;
                    }
                }
                return p;
            }
            static int GetColorDistance(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
            {
                int redDifference = r1 - r2;
                int greenDifference = g1 - g2;
                int blueDifference = b1 - b2;
                return redDifference * redDifference + greenDifference * greenDifference + blueDifference * blueDifference;
            }
            var table = new Dictionary<RGBA8, (RGBA8 RGB, float Order, List<int> Indices)>();
            Span<RGBA8> px = pixels;

            long sr = 0L;
            long sg = 0L;
            long sb = 0L;
            for (int i = 0; i < px.Length; i++)
            {
                var pix = px[i];
                sr += pix.R;
                sg += pix.G;
                sb += pix.B;
            }
            var center = new RGBA8 { R = (byte)(sr / px.Length), G = (byte)(sg / px.Length), B = (byte)(sb / px.Length), A = 255 };

            for (int i = 0; i < px.Length; i++)
            {
                var pix = px[i];
                if (!table.TryGetValue(pix, out var value)) value = new(pix, GetColorDistance(center.R, center.G, center.B, pix.R, pix.G, pix.B), new List<int>());
                value.Indices.Add(i);
                table[pix] = value;
            }

            var list = table.Values.OrderBy(x => x.Order).ToList();
            var index = list.Count;
            while (list.Count > colourCount)
            {
                index--;
                var c1 = list[index];
                var index2 = FindNearestColor(c1.RGB, index, FastQuantizationDepth, list);
                var c2 = list[index2];
                if (c1.Indices.Count < c2.Indices.Count) (c1, c2) = (c2, c1);
                var t = (float)c2.Indices.Count / (c2.Indices.Count + c1.Indices.Count);
                var rgb = new RGBA8
                {
                    R = (byte)(c1.RGB.R + (c2.RGB.R - c1.RGB.R) * t),
                    G = (byte)(c1.RGB.G + (c2.RGB.G - c1.RGB.G) * t),
                    B = (byte)(c1.RGB.B + (c2.RGB.B - c1.RGB.B) * t),
                    A = 255
                };
                c1.Indices.AddRange(c2.Indices);
                if (index > index2) (index, index2) = (index2, index);
                list.RemoveAt(index2);
                list[index] = new(rgb, c1.Order, c1.Indices);
                index--;
                if (index <= 1)
                {
                    index = list.Count;
                }
            }
            var ret = new byte[pixels.Length];
            var pallet = new byte[list.Count * 3];
            Parallel.For(0, list.Count, (i) =>
            {
                var (RGB, Order, Indices) = list[i];
                var indices = CollectionsMarshal.AsSpan(Indices);
                for (int j = 0; j < indices.Length; j++) ret[(int)indices[j]] = (byte)i;
                var x = i * 3;
                pallet[x] = RGB.R;
                pallet[x + 1] = RGB.G;
                pallet[x + 2] = RGB.B;
            });
            return (ret, pallet);
        }
    }
}
