namespace XtraPOS.Api.Devices;

public static class PosNumberAllocator
{
    public static int SuggestNext(IEnumerable<int> used)
    {
        var taken = new HashSet<int>();
        foreach (var number in used)
        {
            if (number > 0)
            {
                taken.Add(number);
            }
        }

        var candidate = 1;
        while (taken.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    public static bool IsAvailable(IEnumerable<int> used, int requested)
    {
        return requested > 0 && !used.Any(number => number == requested);
    }
}
