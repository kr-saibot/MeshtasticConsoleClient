using System;
using System.Collections.Generic;

namespace Meshtastic.ConsoleHost
{
    internal static class TerminalOutputSanitizer
    {
        private static readonly HashSet<char> AllowedSymbols = CreateAllowedSymbols();

        public static void Sanitize(ref Span<char> output)
        {
            for (var index = 0; index < output.Length; index++)
            {
                var current = output[index];

                if (Char.IsHighSurrogate(current))
                {
                    output[index] = '?';
                    if (index + 1 < output.Length && Char.IsLowSurrogate(output[index + 1]))
                    {
                        output[index + 1] = ' ';
                        index++;
                    }
                    continue;
                }

                if (Char.IsLowSurrogate(current))
                {
                    output[index] = '?';
                    continue;
                }

                if (current < ' ' && current != '\t' && current != '\r' && current != '\n' && current != '\u001b')
                {
                    output[index] = ' ';
                    continue;
                }

                if (current == '\u007f' || (current >= '\u0080' && current <= '\u009f'))
                {
                    output[index] = '?';
                    continue;
                }

                if (current > '\u00ff' && !AllowedSymbols.Contains(current))
                    output[index] = '?';
            }
        }

        private static HashSet<char> CreateAllowedSymbols()
        {
            var result = new HashSet<char> { '\u221a' };
            AddRange(result, '\u2500', '\u259f');
            AddRange(result, '\u25a0', '\u25ff');
            return result;
        }

        private static void AddRange(HashSet<char> target, char first, char last)
        {
            for (var value = (int)first; value <= last; value++)
                target.Add((char)value);
        }
    }
}
