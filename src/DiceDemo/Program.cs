using System;
using System.Linq;
using System.Text;

namespace DiceDemo
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args == null || (args.Length != 1 && args.Length != 2) || (args[0] != "u" && args[0] != "n"))
            {
                Console.Error.WriteLine("Usage: DiceDemo u|n [count]");
                return 1;
            }

            var count = 1;
            if (args.Length == 2 && (!Int32.TryParse(args[1], out count) || count < 1 || count > 100))
            {
                Console.Error.WriteLine("Count must be a number from 1 to 100.");
                return 1;
            }

            Console.Write("Deine Zahlen sind: ");
            var random = new Random();
            var rolls = Enumerable.Range(0, count).Select(_ => random.Next(1, 7)).ToArray();
            if (args[0] == "u")
            {
                Console.OutputEncoding = Encoding.UTF8;
                Console.WriteLine(String.Join(" ", rolls.Select(roll => Char.ConvertFromUtf32(0x267F + roll))));
            }
            else Console.WriteLine(String.Join(" ", rolls));
            return 0;
        }
    }
}
