using System;
using System.IO;

internal static class GenerateDefaultAlertSound
{
    public static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Output WAV path required.");
        const int sampleRate = 44100;
        const double duration = 0.58;
        var sampleCount = (int)(sampleRate * duration);
        using (var stream = File.Create(args[0]))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + sampleCount * 2);
            writer.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
            writer.Write(new[] { 'd', 'a', 't', 'a' }); writer.Write(sampleCount * 2);
            for (var index = 0; index < sampleCount; index++)
            {
                var time = (double)index / sampleRate;
                var first = Bell(time, 0.00, 0.24, 659.25);
                var second = Bell(time, 0.18, 0.34, 987.77);
                var sample = Math.Max(-1.0, Math.Min(1.0, (first + second) * 0.42));
                writer.Write((short)(sample * Int16.MaxValue));
            }
        }
    }

    private static double Bell(double time, double start, double length, double frequency)
    {
        var local = time - start;
        if (local < 0 || local >= length) return 0;
        var attack = Math.Min(1.0, local / 0.012);
        var release = Math.Pow(1.0 - local / length, 2.2);
        var fundamental = Math.Sin(2 * Math.PI * frequency * local);
        var overtone = 0.18 * Math.Sin(2 * Math.PI * frequency * 2.01 * local);
        return attack * release * (fundamental + overtone);
    }
}
