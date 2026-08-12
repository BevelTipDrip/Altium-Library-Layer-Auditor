namespace OriginalCircuit.Altium.Serialization.Writers;

/// <summary>
/// Shared utility methods for Altium file writers.
/// </summary>
internal static class WriterUtilities
{
    // OpenMcdf rejects any of these in a storage/stream name -- on lookup as well as on creation --
    // a restriction the real CFB/OLE format doesn't itself impose (see CompoundStorage's
    // TryGetStorage/TryGetStream remarks), but one a name we're about to hand to AddStorage/AddStream
    // must avoid regardless, since there is no way to create an entry with a name it refuses.
    private static readonly char[] InvalidSectionKeyChars = ['\\', '/', ':', '!'];

    /// <summary>
    /// Converts a component name to a compound file storage key by truncating to 31 chars and
    /// replacing every character OpenMcdf's writer rejects ('\', '/', ':', '!') with '_'.
    /// </summary>
    internal static string GetSectionKeyFromName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";

        var maxLength = Math.Min(name.Length, 31);
        var key = name.Substring(0, maxLength);
        return SanitizeSectionKey(key);
    }

    /// <summary>
    /// Replaces every character OpenMcdf's writer rejects ('\', '/', ':', '!') with '_'. Applied both
    /// to freshly-generated keys (see <see cref="GetSectionKeyFromName"/>) and to section keys
    /// preserved from a prior read (<c>PcbLibrary.SectionKeys</c>/<c>SchLibrary.SectionKeys</c>) --
    /// those can legitimately contain them too (real Altium-authored files sometimes do; OpenMcdf's
    /// own restriction is stricter than the actual format), and there is no way to re-create an entry
    /// with the exact original name in that case either, so the key must be re-mangled the same way
    /// on every write regardless of where it came from.
    /// </summary>
    internal static string SanitizeSectionKey(string key)
    {
        foreach (var c in InvalidSectionKeyChars)
            key = key.Replace(c, '_');
        return key;
    }
}
