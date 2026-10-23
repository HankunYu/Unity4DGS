using System;
using System.Collections.Generic;

namespace GaussianSplatting.Editor.Utils
{
    // Orders strings the way people number files: runs of digits compare by
    // value, so "frame_2" comes before "frame_10". The rest compares
    // ordinally, ignoring case.
    public class NaturalStringComparer : IComparer<string>
    {
        public static readonly NaturalStringComparer Instance = new();

        public int Compare(string a, string b)
        {
            if (ReferenceEquals(a, b))
                return 0;
            if (a == null)
                return -1;
            if (b == null)
                return 1;

            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int startA = i, startB = j;
                    while (i < a.Length && char.IsDigit(a[i]))
                        i++;
                    while (j < b.Length && char.IsDigit(b[j]))
                        j++;
                    // Compare by value without parsing (runs may be long):
                    // strip leading zeros, then the longer run is larger
                    ReadOnlySpan<char> runA = a.AsSpan(startA, i - startA).TrimStart('0');
                    ReadOnlySpan<char> runB = b.AsSpan(startB, j - startB).TrimStart('0');
                    if (runA.Length != runB.Length)
                        return runA.Length.CompareTo(runB.Length);
                    int c = runA.CompareTo(runB, StringComparison.Ordinal);
                    if (c != 0)
                        return c;
                }
                else
                {
                    int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0)
                        return c;
                    i++;
                    j++;
                }
            }
            int rest = (a.Length - i).CompareTo(b.Length - j);
            // equal by value ("01" vs "1"): fall back to an ordinal order
            return rest != 0 ? rest : string.CompareOrdinal(a, b);
        }
    }
}
