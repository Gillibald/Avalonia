using System;
using System.Text;

namespace TextStress.Scenarios
{
    /// <summary>Seeded pseudo-words and sentences for generated content.</summary>
    internal sealed class TextGenerator
    {
        private const string Consonants = "bcdfghjklmnprstvwz";
        private const string Vowels = "aeiou";

        private readonly Random _random;

        public TextGenerator(int seed)
        {
            _random = new Random(seed);
        }

        public Random Random => _random;

        public string Word(int minLength = 3, int maxLength = 9)
        {
            var length = _random.Next(minLength, maxLength + 1);
            var builder = new StringBuilder(length);

            for (var i = 0; i < length; i++)
            {
                var set = i % 2 == 0 ? Consonants : Vowels;
                builder.Append(set[_random.Next(set.Length)]);
            }

            return builder.ToString();
        }

        public string Sentence(int minWords, int maxWords, bool capitalize = true)
        {
            var count = _random.Next(minWords, maxWords + 1);
            var builder = new StringBuilder();

            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                var word = Word();

                if (i == 0 && capitalize)
                {
                    word = char.ToUpperInvariant(word[0]) + word.Substring(1);
                }

                builder.Append(word);
            }

            return builder.ToString();
        }
    }
}
