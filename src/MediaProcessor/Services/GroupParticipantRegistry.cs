using System.Collections.Concurrent;

namespace MediaProcessor.Services;

/// <summary>
/// Tracks active participants and their source languages per group call,
/// so that translation / TTS work is limited to languages that actually
/// have listeners.
/// </summary>
public class GroupParticipantRegistry
{
    // groupId → { participantId → sourceLanguage (e.g. "fr-FR") }
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _groups = new();

    // ACS raw user ID (e.g. "8:acs:xxx") → (groupId, participantId) — used to resolve unmixed audio streams
    private readonly ConcurrentDictionary<string, (string GroupId, string ParticipantId)> _acsUserMap = new();

    /// <summary>Registers (or updates) a participant's language for a group call.</summary>
    public void AddParticipant(string groupId, string participantId, string sourceLanguage, string? acsUserId = null)
    {
        var participants = _groups.GetOrAdd(groupId, _ => new ConcurrentDictionary<string, string>());
        participants[participantId] = sourceLanguage;

        if (!string.IsNullOrWhiteSpace(acsUserId))
            _acsUserMap[acsUserId] = (groupId, participantId);
    }

    /// <summary>Removes a participant from a group call.</summary>
    public void RemoveParticipant(string groupId, string participantId)
    {
        if (_groups.TryGetValue(groupId, out var participants))
            participants.TryRemove(participantId, out _);

        // Also remove from the ACS user map
        var acsEntry = _acsUserMap.FirstOrDefault(kvp =>
            kvp.Value.GroupId == groupId && kvp.Value.ParticipantId == participantId);
        if (acsEntry.Key != null)
            _acsUserMap.TryRemove(acsEntry.Key, out _);
    }

    /// <summary>Removes a participant by their ACS raw user ID.</summary>
    public bool RemoveByAcsUserId(string acsRawId)
    {
        if (!_acsUserMap.TryRemove(acsRawId, out var mapping))
            return false;

        if (_groups.TryGetValue(mapping.GroupId, out var participants))
            participants.TryRemove(mapping.ParticipantId, out _);

        return true;
    }

    /// <summary>
    /// Looks up a participant by their ACS raw user ID (from unmixed audio metadata).
    /// Returns the source language and participant ID, or null if unknown.
    /// </summary>
    public (string SourceLanguage, string ParticipantId)? ResolveAcsUser(string acsRawId)
    {
        if (!_acsUserMap.TryGetValue(acsRawId, out var mapping))
            return null;

        if (!_groups.TryGetValue(mapping.GroupId, out var participants))
            return null;

        if (!participants.TryGetValue(mapping.ParticipantId, out var sourceLanguage))
            return null;

        return (sourceLanguage, mapping.ParticipantId);
    }

    /// <summary>
    /// Returns the set of short language codes (e.g. "fr", "en", "zh-Hans") for
    /// all participants currently in the group.
    /// </summary>
    public HashSet<string> GetActiveLanguageCodes(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var participants) || participants.IsEmpty)
            return [];

        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var lang in participants.Values)
        {
            codes.Add(ToShortCode(lang));
        }
        return codes;
    }

    /// <summary>
    /// Returns full BCP-47 source languages (e.g. "fr-FR", "en-US") for all
    /// participants in the group — useful when computing translation targets.
    /// </summary>
    public HashSet<string> GetActiveFullLanguages(string groupId)
    {
        if (!_groups.TryGetValue(groupId, out var participants) || participants.IsEmpty)
            return [];

        return participants.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Removes an entire group (e.g. on call disconnect).</summary>
    public void RemoveGroup(string groupId) => _groups.TryRemove(groupId, out _);

    /// <summary>Converts "fr-FR" → "fr", maps "zh-CN" → "zh-Hans", keeps "zh-Hans" intact.</summary>
    private static string ToShortCode(string bcp47)
    {
        if (bcp47.StartsWith("zh-", StringComparison.OrdinalIgnoreCase))
        {
            // Already in script-subtag form (zh-Hans, zh-Hant)
            if (bcp47.Length >= 7 && !char.IsUpper(bcp47[3]))
                return bcp47[..7];

            // Map region codes to script subtags
            return bcp47.ToUpperInvariant() switch
            {
                "ZH-CN" or "ZH-SG" => "zh-Hans",
                "ZH-TW" or "ZH-HK" => "zh-Hant",
                _ => "zh-Hans", // default to simplified
            };
        }

        return bcp47.Split('-')[0];
    }
}
