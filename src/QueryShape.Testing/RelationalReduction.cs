namespace QueryShape.Testing;

/// <summary>A parent table and one directly dependent child table. More complex relationships need a custom candidate generator.</summary>
public sealed record RelationalInput<TParent, TChild>(IReadOnlyList<TParent> Parents, IReadOnlyList<TChild> Children);

public static class RelationalReduction
{
    /// <summary>Deletes parent groups and their dependent children together, preserving references in this one-to-many relationship.</summary>
    public static IEnumerable<RelationalInput<TParent, TChild>> RemoveParentGroups<TParent, TChild, TKey>(
        RelationalInput<TParent, TChild> input, Func<TParent, TKey> parentKey, Func<TChild, TKey> foreignKey) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(parentKey);
        ArgumentNullException.ThrowIfNull(foreignKey);
        var keys = input.Parents.Select(parentKey).ToHashSet();
        if (keys.Count != input.Parents.Count || input.Children.Any(c => !keys.Contains(foreignKey(c))))
            throw new ArgumentException("Input needs unique parent keys and valid child references.", nameof(input));
        foreach (var parents in QueryReduction.RemoveChunks(input.Parents))
        {
            var retained = parents.Select(parentKey).ToHashSet();
            yield return new(parents, input.Children.Where(c => retained.Contains(foreignKey(c))).ToArray());
        }
    }
}
