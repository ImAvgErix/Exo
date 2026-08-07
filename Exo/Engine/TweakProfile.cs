namespace Exo.Engine;

/// <summary>
/// One desired value binding inside a profile: the catalog tweak id plus the
/// value the profile wants for it. Profiles never carry logic — only bindings.
/// </summary>
public sealed record TweakProfileBinding(string TweakId, int DesiredValue);

/// <summary>
/// A versioned collection of atomic tweak bindings. Profiles are immutable data:
/// applying one resolves every binding through the catalog, and every change to
/// the profile bumps <see cref="Version"/> so stale UI state is never trusted.
/// </summary>
public sealed record TweakProfile(
    string Id,
    string Title,
    int Version,
    IReadOnlyList<TweakProfileBinding> Bindings)
{
    public TweakProfile Bump(int newVersion) => this with { Version = newVersion };

    public TweakProfileBinding? Find(string tweakId) =>
        Bindings.FirstOrDefault(b => string.Equals(b.TweakId, tweakId, StringComparison.Ordinal));
}

/// <summary>
/// Validates profiles against a catalog at construction time: unknown tweak ids,
/// duplicate bindings, and non-positive versions fail fast instead of surfacing
/// as apply-time surprises. The default game profile only binds tweaks the
/// default catalog actually ships, so a fresh install can never reference a
/// tweak that does not exist.
/// </summary>
public static class TweakProfileCatalog
{
    public static TweakProfile Validate(TweakProfile profile, TweakCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(catalog);

        if (string.IsNullOrWhiteSpace(profile.Id))
            throw new ArgumentException("Profile id is required.", nameof(profile));
        if (string.IsNullOrWhiteSpace(profile.Title))
            throw new ArgumentException("Profile title is required.", nameof(profile));
        if (profile.Version <= 0)
            throw new ArgumentException($"Profile '{profile.Id}' must have a positive version.", nameof(profile));

        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in profile.Bindings)
        {
            if (string.IsNullOrWhiteSpace(binding.TweakId))
            {
                issues.Add($"Profile '{profile.Id}' has a binding with an empty tweak id.");
                continue;
            }

            if (!seen.Add(binding.TweakId))
            {
                issues.Add($"Profile '{profile.Id}' binds '{binding.TweakId}' more than once.");
            }

            try
            {
                var adapter = catalog.Resolve<int, RegistryTweakSnapshot>(binding.TweakId);
                _ = adapter; // resolution is the validation
            }
            catch (KeyNotFoundException)
            {
                issues.Add($"Profile '{profile.Id}' binds unknown tweak '{binding.TweakId}'.");
            }
            catch (InvalidOperationException)
            {
                issues.Add($"Profile '{profile.Id}' binds non-int tweak '{binding.TweakId}'.");
            }
        }

        if (issues.Count > 0)
        {
            throw new ArgumentException(
                "Tweak profile validation failed: " + string.Join("; ", issues),
                nameof(profile));
        }

        return profile;
    }

    /// <summary>
    /// The shipped game profile: Game Mode on. It exists to prove the profile
    /// pipeline with real catalog tweaks, not to guess at per-game magic.
    /// </summary>
    public static TweakProfile CreateDefaultGameProfile(TweakCatalog catalog)
    {
        var profile = new TweakProfile(
            "game.default",
            "Default game profile",
            1,
            [new TweakProfileBinding("system.game-mode", 1)]);

        return Validate(profile, catalog);
    }
}
