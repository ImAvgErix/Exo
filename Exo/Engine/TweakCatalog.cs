using System.Text.RegularExpressions;

namespace Exo.Engine;

public sealed class TweakCatalogValidationException(IReadOnlyList<string> issues)
    : ArgumentException("Tweak catalog validation failed: " + string.Join("; ", issues))
{
    public IReadOnlyList<string> Issues { get; } = issues;
}

public sealed partial class TweakCatalog
{
    private readonly Dictionary<string, ITweakAdapter> _adapters;

    public TweakCatalog(IEnumerable<ITweakAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var materialized = adapters.ToArray();
        var issues = Validate(materialized);
        if (issues.Count > 0)
        {
            throw new TweakCatalogValidationException(issues);
        }

        _adapters = materialized.ToDictionary(
            adapter => adapter.Definition.Id,
            StringComparer.Ordinal);
    }

    public IReadOnlyCollection<TweakDefinition> Definitions =>
        _adapters.Values
            .Select(adapter => adapter.Definition)
            .OrderBy(definition => definition.Id, StringComparer.Ordinal)
            .ToArray();

    public static TweakCatalog CreateDefault() => new(
    [
        new TracerTweakAdapter(),
        ..SystemLeverCatalog.BuildAdapters(),
        ..PrivacyLeverCatalog.BuildAdapters()
    ]);

    public ITweakAdapter<TValue, TSnapshot> Resolve<TValue, TSnapshot>(string id)
    {
        if (!_adapters.TryGetValue(id, out var adapter))
        {
            throw new KeyNotFoundException($"Tweak '{id}' is not registered.");
        }

        if (adapter is not ITweakAdapter<TValue, TSnapshot> typed)
        {
            throw new InvalidOperationException(
                $"Tweak '{id}' uses value type '{adapter.Definition.ValueType.Name}' " +
                $"and snapshot type '{adapter.SnapshotType.Name}'.");
        }

        return typed;
    }

    private static IReadOnlyList<string> Validate(IReadOnlyList<ITweakAdapter> adapters)
    {
        var issues = new List<string>();
        for (var index = 0; index < adapters.Count; index++)
        {
            var adapter = adapters[index];
            if (adapter is null)
            {
                issues.Add($"Adapter at index {index} is null.");
                continue;
            }

            var definition = adapter.Definition;
            if (definition is null)
            {
                issues.Add($"Adapter at index {index} has no definition.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(definition.Id) || !TweakIdPattern().IsMatch(definition.Id))
            {
                issues.Add($"Tweak id '{definition.Id}' must use lowercase dot-separated identifiers.");
            }

            if (string.IsNullOrWhiteSpace(definition.Title))
            {
                issues.Add($"Tweak '{definition.Id}' must have a title.");
            }

            if (string.IsNullOrWhiteSpace(definition.Description))
            {
                issues.Add($"Tweak '{definition.Id}' must have a description.");
            }

            if (definition.ValueType == typeof(void))
            {
                issues.Add($"Tweak '{definition.Id}' must declare a value type.");
            }
        }

        foreach (var group in adapters
                     .Where(adapter => adapter?.Definition is not null)
                     .GroupBy(adapter => adapter.Definition.Id, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            issues.Add($"Duplicate tweak id '{group.Key}'.");
        }

        return issues;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex TweakIdPattern();
}
