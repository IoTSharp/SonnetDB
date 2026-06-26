namespace SonnetDB.FullText.Index;

internal static class PositionMatcher
{
    public static int CountPhraseMatches(List<int>[] positions)
    {
        int count = 0;
        foreach (int start in positions[0])
        {
            bool found = true;
            for (int i = 1; i < positions.Length; i++)
            {
                if (!positions[i].Contains(start + i))
                {
                    found = false;
                    break;
                }
            }
            if (found)
            {
                count++;
            }
        }
        return count;
    }

    public static int CountNearMatches(List<int>[] positions, int maxDistance, bool inOrder)
    {
        int count = 0;
        foreach (int anchor in positions[0])
        {
            if (inOrder)
            {
                if (HasOrderedNearMatch(positions, anchor, maxDistance))
                {
                    count++;
                }
                continue;
            }

            int min = anchor;
            int max = anchor;
            bool found = true;
            for (int i = 1; i < positions.Length; i++)
            {
                int nearest = FindNearestWithinWindow(positions[i], anchor, maxDistance);
                if (nearest < 0)
                {
                    found = false;
                    break;
                }
                min = Math.Min(min, nearest);
                max = Math.Max(max, nearest);
            }
            if (found && max - min <= maxDistance)
            {
                count++;
            }
        }
        return count;
    }

    private static bool HasOrderedNearMatch(List<int>[] positions, int previous, int maxDistance)
    {
        int first = previous;
        for (int i = 1; i < positions.Length; i++)
        {
            int next = -1;
            foreach (int candidate in positions[i])
            {
                if (candidate > previous && candidate - first <= maxDistance)
                {
                    next = candidate;
                    break;
                }
            }
            if (next < 0)
            {
                return false;
            }
            previous = next;
        }
        return true;
    }

    private static int FindNearestWithinWindow(List<int> positions, int anchor, int maxDistance)
    {
        int best = -1;
        int bestDistance = int.MaxValue;
        foreach (int position in positions)
        {
            int distance = Math.Abs(position - anchor);
            if (distance <= maxDistance && distance < bestDistance)
            {
                best = position;
                bestDistance = distance;
            }
        }
        return best;
    }
}
