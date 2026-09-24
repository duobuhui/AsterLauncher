namespace AsterLauncher.Core;

public static class GameLibraryOrder
{
    public static IReadOnlyList<string> OrderedIds(IEnumerable<string> availableIds, IEnumerable<string> orderedIds)
    {
        var available = availableIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var known = available.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return orderedIds.Concat(available)
            .Where(known.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IGameAdapter? MatchBuiltInExecutable(IEnumerable<IGameAdapter> adapters, string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        return adapters.FirstOrDefault(adapter => !adapter.Definition.IsCustom
            && adapter.Definition.ExecutableNames.Contains(fileName, StringComparer.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string> VisibleIds(
        IEnumerable<string> availableIds,
        IEnumerable<string> hiddenIds,
        IEnumerable<string> orderedIds)
    {
        var hidden = hiddenIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return OrderedIds(availableIds, orderedIds)
            .Where(id => !hidden.Contains(id))
            .ToArray();
    }

    public static bool Move(List<string> orderedIds, string gameId, int offset)
    {
        var index = orderedIds.FindIndex(id => string.Equals(id, gameId, StringComparison.OrdinalIgnoreCase));
        var target = index + offset;
        if (index < 0 || target < 0 || target >= orderedIds.Count) return false;
        (orderedIds[index], orderedIds[target]) = (orderedIds[target], orderedIds[index]);
        return true;
    }

    public static bool MoveWithin(List<string> orderedIds, string gameId, int offset, IEnumerable<string> groupIds)
    {
        var group = groupIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var positions = Enumerable.Range(0, orderedIds.Count)
            .Where(index => group.Contains(orderedIds[index]))
            .ToArray();
        var groupIndex = Array.FindIndex(positions, position => string.Equals(orderedIds[position], gameId, StringComparison.OrdinalIgnoreCase));
        var target = groupIndex + offset;
        if (groupIndex < 0 || target < 0 || target >= positions.Length) return false;
        (orderedIds[positions[groupIndex]], orderedIds[positions[target]]) =
            (orderedIds[positions[target]], orderedIds[positions[groupIndex]]);
        return true;
    }

    public static bool ApplyGroupOrder(List<string> orderedIds, IEnumerable<string> groupIds)
    {
        var desired = groupIds.ToArray();
        var group = desired.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (desired.Length != group.Count) return false;
        var positions = Enumerable.Range(0, orderedIds.Count)
            .Where(index => group.Contains(orderedIds[index]))
            .ToArray();
        if (positions.Length != desired.Length) return false;
        var changed = false;
        for (var index = 0; index < positions.Length; index++)
        {
            if (!string.Equals(orderedIds[positions[index]], desired[index], StringComparison.OrdinalIgnoreCase))
            {
                orderedIds[positions[index]] = desired[index];
                changed = true;
            }
        }
        return changed;
    }
}
