// Wow!! https://claude.ai/ actually made it better well faster FastQuantizationDepth needs tweaking try 64

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics;

namespace Quantization
{
    public static partial class Quantizer
    {

        // Pre-allocated arrays to avoid repeated allocations
        private static readonly ThreadLocal<List<ColorEntry>> _colorListCache = new(() => new List<ColorEntry>(65536));
        private static readonly ThreadLocal<Dictionary<uint, int>> _colorMapCache = new(() => new Dictionary<uint, int>(65536));

        private struct ColorEntry
        {
            public RGBA8 Color;
            public float DistanceFromCenter;
            public int PixelCount;
            public int FirstPixelIndex; // Store only first index to save memory

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public ColorEntry(RGBA8 color, float distance, int firstIndex)
            {
                Color = color;
                DistanceFromCenter = distance;
                PixelCount = 1;
                FirstPixelIndex = firstIndex;
            }
        }
        public static (byte[] pixels, byte[] pallet) Quantize8BitAI(byte[] pixels, byte[] pallet, int width, int height, int colourCount)
        {
            Span<byte> memory = pixels;
            Span<RGBA8> pixelsrgba = new RGBA8[pixels.Length];
            ref byte buffer = ref MemoryMarshal.GetReference(memory);
            var off = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                var c = Unsafe.Add(ref buffer, i) * 3;
                pixelsrgba[off++] = new RGBA8
                {
                    R = pallet[c],
                    G = pallet[c + 1],
                    B = pallet[c + 2],
                    A = 255
                };
            }
            return Quantize32BitAI(pixelsrgba.ToArray(), width, height, colourCount);
        }
        public static (byte[] pixels, byte[] pallet) Quantize32BitAI(byte[] pixels, int width, int height, int colourCount)
        {
            Span<byte> memory = pixels;
            Span<RGBA8> pixelsrgba = MemoryMarshal.Cast<byte, RGBA8>(memory);
            return Quantize32BitAI(pixelsrgba.ToArray(), width, height, colourCount);
        }
        public static (byte[] pixels, byte[] palette) Quantize32BitAI(RGBA8[] pixels, int width, int height, int colourCount)
        {
            if (pixels.Length == 0 || colourCount == 0) return (Array.Empty<byte>(), Array.Empty<byte>());

            var pixelSpan = pixels.AsSpan();
            var colorList = _colorListCache.Value!;
            var colorMap = _colorMapCache.Value!;

            colorList.Clear();
            colorMap.Clear();

            // Calculate center color using SIMD when possible
            var center = CalculateCenterColor(pixelSpan);

            // Build unique color map with vectorized distance calculation
            BuildColorMap(pixelSpan, center, colorList, colorMap);

            if (colorList.Count <= colourCount)
            {
                return CreateDirectPalette(pixelSpan, colorList, colorMap);
            }

            // Sort by distance from center (closest first for better visual quality)
            colorList.Sort((a, b) => a.DistanceFromCenter.CompareTo(b.DistanceFromCenter));

            // Reduce colors using optimized merging
            ReduceColors(colorList, colourCount);

            // Generate final result
            return GenerateFinalResult(pixelSpan, colorList, colorMap);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static RGBA8 CalculateCenterColor(ReadOnlySpan<RGBA8> pixels)
        {
            if (Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
            {
                return CalculateCenterColorVectorized(pixels);
            }

            long sr = 0, sg = 0, sb = 0;
            foreach (var pixel in pixels)
            {
                sr += pixel.R;
                sg += pixel.G;
                sb += pixel.B;
            }

            var len = pixels.Length;
            return new RGBA8 { R = (byte)(sr / len), G = (byte)(sg / len), B = (byte)(sb / len), A = 255 };
        }

        private static RGBA8 CalculateCenterColorVectorized(ReadOnlySpan<RGBA8> pixels)
        {
            var vectorSize = Vector<uint>.Count;
            var vectorCount = pixels.Length / vectorSize;

            var rSum = Vector<uint>.Zero;
            var gSum = Vector<uint>.Zero;
            var bSum = Vector<uint>.Zero;

            var pixelBytes = MemoryMarshal.AsBytes(pixels);

            for (int i = 0; i < vectorCount; i++)
            {
                var offset = i * vectorSize * 4;
                var rVector = new Vector<uint>();
                var gVector = new Vector<uint>();
                var bVector = new Vector<uint>();

                for (int j = 0; j < vectorSize; j++)
                {
                    var pixelOffset = offset + j * 4;
                    Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref rVector), j) = pixelBytes[pixelOffset + 1]; // R
                    Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref gVector), j) = pixelBytes[pixelOffset + 2]; // G
                    Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref bVector), j) = pixelBytes[pixelOffset + 3]; // B
                }

                rSum += rVector;
                gSum += gVector;
                bSum += bVector;
            }

            uint totalR = 0, totalG = 0, totalB = 0;
            for (int i = 0; i < vectorSize; i++)
            {
                totalR += Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref rSum), i);
                totalG += Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref gSum), i);
                totalB += Unsafe.Add(ref Unsafe.As<Vector<uint>, uint>(ref bSum), i);
            }

            // Handle remaining pixels
            for (int i = vectorCount * vectorSize; i < pixels.Length; i++)
            {
                totalR += pixels[i].R;
                totalG += pixels[i].G;
                totalB += pixels[i].B;
            }

            var len = (uint)pixels.Length;
            return new RGBA8 { R = (byte)(totalR / len), G = (byte)(totalG / len), B = (byte)(totalB / len), A = 255 };
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void BuildColorMap(ReadOnlySpan<RGBA8> pixels, RGBA8 center, List<ColorEntry> colorList, Dictionary<uint, int> colorMap)
        {
            for (int i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                var key = pixel.ToUInt32();

                if (colorMap.TryGetValue(key, out int existingIndex))
                {
                    var entry = colorList[existingIndex];
                    entry.PixelCount++;
                    colorList[existingIndex] = entry;
                }
                else
                {
                    var distance = GetColorDistanceFast(center, pixel);
                    var newEntry = new ColorEntry(pixel, distance, i);
                    colorMap[key] = colorList.Count;
                    colorList.Add(newEntry);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static float GetColorDistanceFast(RGBA8 c1, RGBA8 c2)
        {
            // Use faster integer math and avoid sqrt
            int rd = c1.R - c2.R;
            int gd = c1.G - c2.G;
            int bd = c1.B - c2.B;
            return rd * rd + gd * gd + bd * bd;
        }

        private static (byte[] pixels, byte[] palette) CreateDirectPalette(ReadOnlySpan<RGBA8> pixels, List<ColorEntry> colorList, Dictionary<uint, int> colorMap)
        {
            var result = new byte[pixels.Length];
            var palette = new byte[colorList.Count * 3];

            // Fill palette
            for (int i = 0; i < colorList.Count; i++)
            {
                var color = colorList[i].Color;
                var offset = i * 3;
                palette[offset] = color.R;
                palette[offset + 1] = color.G;
                palette[offset + 2] = color.B;
            }

            // Map pixels to palette indices
            for (int i = 0; i < pixels.Length; i++)
            {
                result[i] = (byte)colorMap[pixels[i].ToUInt32()];
            }

            return (result, palette);
        }

        private static void ReduceColors(List<ColorEntry> colorList, int targetCount)
        {
            while (colorList.Count > targetCount)
            {
                var mergeIndex = FindColorToMerge(colorList);
                var nearestIndex = FindNearestColor(colorList, mergeIndex);

                MergeColors(colorList, mergeIndex, nearestIndex);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindColorToMerge(List<ColorEntry> colorList)
        {
            // Find color with least pixels (more aggressive merging of rare colors)
            int minPixels = int.MaxValue;
            int minIndex = colorList.Count - 1;

            var searchEnd = Math.Max(colorList.Count - FastQuantizationDepth, colorList.Count / 2);
            for (int i = colorList.Count - 1; i >= searchEnd; i--)
            {
                if (colorList[i].PixelCount < minPixels)
                {
                    minPixels = colorList[i].PixelCount;
                    minIndex = i;
                }
            }

            return minIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int FindNearestColor(List<ColorEntry> colorList, int targetIndex)
        {
            var targetColor = colorList[targetIndex].Color;
            float minDistance = float.MaxValue;
            int nearestIndex = targetIndex > 0 ? targetIndex - 1 : targetIndex + 1;

            var searchStart = Math.Max(0, targetIndex - FastQuantizationDepth / 2);
            var searchEnd = Math.Min(colorList.Count, targetIndex + FastQuantizationDepth / 2);

            for (int i = searchStart; i < searchEnd; i++)
            {
                if (i == targetIndex) continue;

                var distance = GetColorDistanceFast(targetColor, colorList[i].Color);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestIndex = i;
                }
            }

            return nearestIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void MergeColors(List<ColorEntry> colorList, int index1, int index2)
        {
            if (index1 > index2) (index1, index2) = (index2, index1);

            var color1 = colorList[index1];
            var color2 = colorList[index2];

            var totalPixels = color1.PixelCount + color2.PixelCount;
            var weight1 = (float)color1.PixelCount / totalPixels;
            var weight2 = (float)color2.PixelCount / totalPixels;

            var mergedColor = new RGBA8
            {
                R = (byte)(color1.Color.R * weight1 + color2.Color.R * weight2),
                G = (byte)(color1.Color.G * weight1 + color2.Color.G * weight2),
                B = (byte)(color1.Color.B * weight1 + color2.Color.B * weight2),
                A = 255
            };

            colorList[index1] = new ColorEntry
            {
                Color = mergedColor,
                DistanceFromCenter = Math.Min(color1.DistanceFromCenter, color2.DistanceFromCenter),
                PixelCount = totalPixels,
                FirstPixelIndex = color1.FirstPixelIndex
            };

            colorList.RemoveAt(index2);
        }

        private static (byte[] pixels, byte[] palette) GenerateFinalResult(ReadOnlySpan<RGBA8> pixels, List<ColorEntry> colorList, Dictionary<uint, int> originalColorMap)
        {
            var result = new byte[pixels.Length];
            var palette = new byte[colorList.Count * 3];
            var colorToPaletteIndex = new Dictionary<uint, byte>(colorList.Count);

            // Build new color mapping and palette
            for (int i = 0; i < colorList.Count; i++)
            {
                var color = colorList[i].Color;
                var offset = i * 3;
                palette[offset] = color.R;
                palette[offset + 1] = color.G;
                palette[offset + 2] = color.B;
                colorToPaletteIndex[color.ToUInt32()] = (byte)i;
            }

            // Map each pixel to nearest palette color
            for (int i = 0; i < pixels.Length; i++)
            {
                var pixelColor = pixels[i];
                var pixelKey = pixelColor.ToUInt32();

                if (colorToPaletteIndex.TryGetValue(pixelKey, out byte directIndex))
                {
                    result[i] = directIndex;
                }
                else
                {
                    // Find nearest color in final palette
                    result[i] = FindNearestPaletteColor(pixelColor, colorList);
                }
            }

            return (result, palette);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte FindNearestPaletteColor(RGBA8 targetColor, List<ColorEntry> colorList)
        {
            float minDistance = float.MaxValue;
            byte nearestIndex = 0;

            for (int i = 0; i < colorList.Count; i++)
            {
                var distance = GetColorDistanceFast(targetColor, colorList[i].Color);
                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestIndex = (byte)i;
                }
            }

            return nearestIndex;
        }
    }
}
