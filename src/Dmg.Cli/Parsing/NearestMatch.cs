namespace Dmg.Cli.Parsing;

/// <summary>
/// "Did you mean --verbose?" - the edit distance behind an unknown-option message.
/// </summary>
/// <remarks>
/// <para>
/// Edit distance, with two rules on top. A candidate has to be within a distance
/// that scales with the length of what was typed, so <c>--zzz</c> does not get told
/// it might have meant <c>--json</c>; and a candidate that starts with what was
/// typed always wins, because <c>--part</c> for <c>--partition</c> is a truncation
/// rather than a typo and edit distance is bad at seeing that.
/// </para>
/// <para>
/// <b>Transpositions cost one edit, not two.</b> Swapping two neighbouring letters
/// is the single most common typing mistake, and plain Levenshtein charges it as a
/// delete plus an insert - which pushed <c>--qiuet</c> outside the budget for a
/// five-letter word and printed no suggestion at all for the most obvious typo
/// there is. The distance below is therefore optimal string alignment (restricted
/// Damerau-Levenshtein): the same recurrence with one extra move for an adjacent
/// swap.
/// </para>
/// <para>
/// The matrix is three rolling rows rather than a full table - two would do for
/// plain Levenshtein, but the transposition move reaches back a second row. Option
/// names are short, yet this runs on a failure path where allocating is pointless.
/// </para>
/// </remarks>
public static class NearestMatch
{
    /// <summary>
    /// The closest of <paramref name="candidates"/> to <paramref name="typed"/>, or
    /// null when nothing is close enough to be worth suggesting.
    /// </summary>
    /// <param name="typed">What the user typed, without any leading dashes.</param>
    /// <param name="candidates">The names it might have been, without dashes.</param>
    public static string? Find(string typed, IEnumerable<string> candidates)
    {
        ArgumentNullException.ThrowIfNull(typed);
        ArgumentNullException.ThrowIfNull(candidates);

        if (typed.Length == 0)
        {
            return null;
        }

        // A short word tolerates one edit, a long one up to three. Without this an
        // unknown option is always "close" to something and the suggestion becomes
        // noise the user learns to ignore.
        int budget = Math.Clamp(typed.Length / 3, 1, 3);

        string? best = null;
        int bestDistance = int.MaxValue;

        foreach (string candidate in candidates)
        {
            if (candidate is null || candidate.Length == 0)
            {
                continue;
            }

            if (candidate.StartsWith(typed, StringComparison.Ordinal))
            {
                // A prefix is a truncation, not a typo. Rank it ahead of anything
                // edit distance could offer, and prefer the shortest completion.
                if (bestDistance > 0 || candidate.Length < (best?.Length ?? int.MaxValue))
                {
                    best = candidate;
                    bestDistance = 0;
                }

                continue;
            }

            if (bestDistance == 0)
            {
                continue;
            }

            int distance = Distance(typed, candidate);

            if (distance <= budget && distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>
    /// The optimal string alignment distance between two strings, compared
    /// ordinally: insertions, deletions, substitutions and swaps of two adjacent
    /// characters, each costing one.
    /// </summary>
    /// <remarks>
    /// "Restricted" in the usual sense - a substring is never edited twice, so
    /// <c>ca</c> to <c>abc</c> is 3 here rather than the unrestricted 2. For typo
    /// suggestions over option names the distinction never arises, and the
    /// restricted form needs three rows instead of a full <c>O(n*m)</c> table.
    /// </remarks>
    /// <param name="left">One string.</param>
    /// <param name="right">The other.</param>
    public static int Distance(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        // beforePrevious is row-2, which only the transposition move reads.
        int[] beforePrevious = new int[right.Length + 1];
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];

        for (int column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (int row = 1; row <= left.Length; row++)
        {
            current[0] = row;

            for (int column = 1; column <= right.Length; column++)
            {
                int cost = left[row - 1] == right[column - 1] ? 0 : 1;
                int substitution = previous[column - 1] + cost;
                int deletion = previous[column] + 1;
                int insertion = current[column - 1] + 1;

                int best = Math.Min(substitution, Math.Min(deletion, insertion));

                // The swap: the two characters ending here are each other's, the
                // wrong way round. One edit, not the two that a delete plus an
                // insert would cost.
                if (row > 1
                    && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1])
                {
                    best = Math.Min(best, beforePrevious[column - 2] + 1);
                }

                current[column] = best;
            }

            (beforePrevious, previous, current) = (previous, current, beforePrevious);
        }

        return previous[right.Length];
    }
}
