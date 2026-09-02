namespace Blocwerk.Core.Helpers;

/// <summary>
/// Mints a throwaway but climbing-flavoured name for a new boulder. A gym tablet in kiosk mode has
/// no on-screen keyboard worth typing on, so a required Name field is the one thing that can stop a
/// setter from saving. Pre-filling the field with a name that is already unique on the wall means
/// "create and publish" never needs the keyboard; the setter can still overwrite it.
/// </summary>
public static class BoulderNameGenerator
{
    /// <summary>How many random combinations to try before falling back to a numeric suffix.</summary>
    private const int RandomAttempts = 24;

    /// <summary>Boulder.Name is capped at 256 characters; stay well inside it.</summary>
    private const int MaxLength = 256;

    private static readonly string[] Adjectives =
    [
        "Crimpy", "Slopey", "Pumpy", "Greasy", "Burly", "Dusty", "Sketchy", "Lofty",
        "Chalky", "Tenuous", "Balancy", "Brutal", "Cheeky", "Committing", "Crusty", "Delicate",
        "Devious", "Dynamic", "Featureless", "Flared", "Glassy", "Gritty", "Heinous", "Humbling",
        "Improbable", "Insecure", "Juggy", "Lanky", "Mossy", "Nautical", "Overhung", "Polished",
        "Powerful", "Reachy", "Rounded", "Sandbagged", "Scrappy", "Shouldery", "Silky", "Slabby",
        "Sloping", "Spicy", "Stubborn", "Sustained", "Technical", "Thuggish", "Tricky", "Wandering",
        "Weary", "Windy", "Grippy", "Frosty", "Bouncy", "Steady", "Tidy",
    ];

    private static readonly string[] Nouns =
    [
        "Traverse", "Crimp", "Sloper", "Jug", "Pinch", "Arete", "Prow", "Roof",
        "Dyno", "Heel Hook", "Mantle", "Undercling", "Gaston", "Bulge", "Campus", "Compression",
        "Corner", "Crack", "Crux", "Dihedral", "Drop Knee", "Edge", "Flake", "Foothold",
        "Groove", "Highball", "Kneebar", "Ledge", "Lip", "Lock Off", "Overlap", "Pocket",
        "Problem", "Rail", "Rockover", "Runout", "Scoop", "Send", "Sit Start", "Slab",
        "Sloth", "Smear", "Spanner", "Sting", "Tension", "Toe Hook", "Topout", "Traversal",
        "Tufa", "Wobbler", "Crozzle", "Deadpoint", "Flag", "Jam", "Bump",
    ];

    /// <summary>
    /// Returns a climbing-flavoured name that does not collide (case-insensitive, trimmed) with
    /// <paramref name="existingNames"/>. Always terminates and always returns a non-empty name.
    /// </summary>
    /// <param name="existingNames">The names already in use on the wall; nulls are ignored.</param>
    /// <param name="random">Randomness source; <see cref="Random.Shared"/> when null.</param>
    public static string Generate(IEnumerable<string> existingNames, Random? random = null)
    {
        var rng = random ?? Random.Shared;
        var taken = BuildTakenSet(existingNames);

        string candidate = Compose(rng);
        for (var attempt = 0; attempt < RandomAttempts && taken.Contains(candidate); attempt++)
        {
            candidate = Compose(rng);
        }

        if (!taken.Contains(candidate))
        {
            return candidate;
        }

        // Every random draw collided (a very small wall vocabulary, or a wall with a lot of
        // boulders). Suffixing is guaranteed to terminate: the set of taken names is finite AND
        // every suffix produces a distinct name. That second half only holds if the BASE is
        // truncated first — truncating the joined string would clip the suffix back off once the
        // base sits at the cap, and the loop would then propose the same name forever.
        for (var suffix = 2; ; suffix++)
        {
            var suffixText = $" {suffix}";
            var numbered = Truncate(candidate, MaxLength - suffixText.Length) + suffixText;
            if (!taken.Contains(numbered))
            {
                return numbered;
            }
        }
    }

    private static HashSet<string> BuildTakenSet(IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in existingNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            taken.Add(name.Trim());
        }

        return taken;
    }

    private static string Compose(Random rng)
    {
        var adjective = Adjectives[rng.Next(Adjectives.Length)];
        var noun = Nouns[rng.Next(Nouns.Length)];
        return Truncate($"{adjective} {noun}", MaxLength);
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
