using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FUKA.AvatarBenchmark.Editor
{
    public static class BenchmarkOutputDirectory
    {
        // Keep report files usable without Windows long-path support. The longest
        // child name is case_2147483647.png; atomic report filenames are shorter.
        private const int MaximumFilePathLength = 259;
        private const int MaximumChildNameLength = 19;
        private const int MaximumNameLength = 255;
        private const string InvalidCharacters = "<>:\"/\\|?*";

        public static string Create(string parent, IEnumerable<string> displayNames, DateTime timestamp)
        {
            parent = Path.GetFullPath(parent);
            string prefix = timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_";
            string names = string.Join("_", displayNames.Select(Sanitize));
            if (names.Length == 0) names = "計測対象";
            int maximumLength = Math.Min(MaximumNameLength, MaximumFilePathLength - parent.Length - 2 - MaximumChildNameLength);
            for (int number = 1; ; number = checked(number + 1))
            {
                string suffix = number == 1 ? "" : "_" + number.ToString(CultureInfo.InvariantCulture);
                int available = maximumLength - prefix.Length - suffix.Length;
                if (available < 1)
                    throw new InvalidOperationException("計測結果の保存先が長すぎます。Unityプロジェクトを短いパスへ移動してください。");
                // UTF-8 filesystems limit bytes per name rather than UTF-16 characters.
                string name = prefix + Shorten(names, available, MaximumNameLength - prefix.Length - suffix.Length) + suffix;
                string path = Path.Combine(parent, name);
                if (Directory.Exists(path) || File.Exists(path)) continue;
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static string Sanitize(string value)
        {
            var name = new StringBuilder();
            foreach (char c in value ?? "")
                name.Append(c < 32 || InvalidCharacters.IndexOf(c) >= 0 ? '_' : c);
            string result = name.ToString().Trim().TrimEnd('.', ' ');
            return result.Length == 0 ? "計測対象" : result;
        }

        private static string Shorten(string value, int maximumCharacters, int maximumBytes)
        {
            if (value.Length <= maximumCharacters && Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
            const string omission = "…";
            maximumCharacters -= omission.Length;
            maximumBytes -= Encoding.UTF8.GetByteCount(omission);
            var name = new StringBuilder();
            var elements = StringInfo.GetTextElementEnumerator(value);
            int bytes = 0;
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                int length = Encoding.UTF8.GetByteCount(element);
                if (name.Length + element.Length > maximumCharacters || bytes + length > maximumBytes) break;
                name.Append(element); bytes += length;
            }
            return name.ToString().TrimEnd('.', ' ') + omission;
        }
    }
}
