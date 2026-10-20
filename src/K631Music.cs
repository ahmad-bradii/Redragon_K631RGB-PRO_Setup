// Music styles modelled on the Redragon app's music mode (seen in the USB capture): wave line,
// equalizer bars, bloom from the centre, stream from the left, full-board pulse.
// Works on the keyboard's LED grid: 15 columns x 6 rows, slot = column * 6 + row (0-based),
// rows 0-4 are the key rows (top to bottom); row 5 isn't a key row and stays dark.
using System;

namespace K631Live
{
    class MusicRenderer
    {
        public const int Columns = 15;
        public const int GridRows = 6;
        const int KeyRows = 5;
        public const int Slots = Columns * GridRows;
        public static readonly string[] Styles = { "wave", "bars", "bloom", "stream", "pulse" };

        readonly double[] shown = new double[AudioMeter.Bands];
        double bloomHue, pulseHue, bassAverage;

        // levels: 16 band levels 0..1 (bass to treble). Returns R,G,B for each of the 90 slots.
        public byte[] Render(string style, double[] levels)
        {
            // Rise instantly, fall gently, like the app's animations.
            for (int b = 0; b < shown.Length; b++)
                shown[b] = levels[b] > shown[b] ? levels[b] : shown[b] * 0.82 + levels[b] * 0.18;

            byte[] rgb = new byte[Slots * 3];
            double volume = Average(0, 15), bass = Average(0, 2);
            switch (style)
            {
                case "bars":    // columns fill from the bottom to their band's level
                    for (int c = 0; c < Columns; c++)
                        for (int row = 0; row < KeyRows; row++)
                            if (shown[c] >= (KeyRows - row) / (double)KeyRows - 0.1) Set(rgb, c, row, ZoneHue(c), 1);
                    break;

                case "bloom":   // a blob that grows from the centre with the volume, colours cycling
                    bloomHue = (bloomHue + 1.5) % 360;
                    double radius = Average(0, 7) * 9;
                    for (int c = 0; c < Columns; c++)
                        for (int row = 0; row < KeyRows; row++)
                        {
                            double d = Math.Sqrt((c - 7) * (c - 7) + (row - 2) * (row - 2) * 2.56);
                            if (d <= radius) Set(rgb, c, row, bloomHue + d * 25, 1 - 0.6 * d / (radius + 0.5));
                        }
                    break;

                case "stream":  // colour streams in from the left as far as the volume reaches
                    double reach = volume * Columns * 1.3;
                    for (int c = 0; c < Columns && c < reach; c++)
                        for (int row = 0; row < KeyRows; row++)
                            Set(rgb, c, row, 120 + c * 8, Math.Min(1, reach - c));
                    break;

                case "pulse":   // the whole board in one colour, breathing with the bass, new colour on each beat
                    if (bass > bassAverage * 1.35 && bass > 0.35) pulseHue = (pulseHue + 55) % 360;
                    bassAverage = bassAverage * 0.95 + bass * 0.05;
                    for (int c = 0; c < Columns; c++)
                        for (int row = 0; row < KeyRows; row++)
                            Set(rgb, c, row, pulseHue, 0.12 + 0.88 * bass);
                    break;

                default:        // "wave": one key per column, riding at its band's level
                    for (int c = 0; c < Columns; c++)
                        if (shown[c] > 0.05)
                            Set(rgb, c, KeyRows - 1 - (int)Math.Round(Math.Min(1, shown[c]) * (KeyRows - 1)), ZoneHue(c), 1);
                    break;
            }
            return rgb;
        }

        // Green on the left through cyan, blue, magenta and red to yellow on the right, like the app's bars.
        static double ZoneHue(int column)
        {
            return 120 + column * 300.0 / (Columns - 1);
        }

        double Average(int from, int to)
        {
            double sum = 0;
            for (int b = from; b <= to; b++) sum += shown[b];
            return sum / (to - from + 1);
        }

        static void Set(byte[] rgb, int column, int row, double hue, double intensity)
        {
            intensity = Math.Max(0, Math.Min(1, intensity));
            hue = ((hue % 360) + 360) % 360;
            double x = 1 - Math.Abs(hue / 60 % 2 - 1);
            double r, g, b;
            if (hue < 60) { r = 1; g = x; b = 0; }
            else if (hue < 120) { r = x; g = 1; b = 0; }
            else if (hue < 180) { r = 0; g = 1; b = x; }
            else if (hue < 240) { r = 0; g = x; b = 1; }
            else if (hue < 300) { r = x; g = 0; b = 1; }
            else { r = 1; g = 0; b = x; }
            int i = (column * GridRows + row) * 3;
            rgb[i] = (byte)Math.Round(r * 255 * intensity);
            rgb[i + 1] = (byte)Math.Round(g * 255 * intensity);
            rgb[i + 2] = (byte)Math.Round(b * 255 * intensity);
        }
    }
}
