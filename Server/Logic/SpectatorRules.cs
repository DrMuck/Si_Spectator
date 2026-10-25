using System;
using System.Collections.Generic;
using System.Linq;

namespace SiSpectator.Logic
{
    // Pure C# (no Unity, MelonLoader or game types): compiled into the server mod and into the xunit tests.

    /// <summary>The config switches the permission rules read (a copy, so the rules never touch the JSON object).</summary>
    public sealed class RulesSettings
    {
        public bool Enabled = true;
        public bool AllowAll = false;
        public bool AllowAdmins = true;
        /// <summary>Spectator slots; 0 = unlimited. Admin-forced entries ignore it.</summary>
        public int MaxSpectators = 4;
    }

    public enum EnterVerdict { Ok, Disabled, NotAllowed, Full, Already }

    public readonly struct RuleResult
    {
        public readonly EnterVerdict Verdict;
        public readonly string Reason;
        public RuleResult(EnterVerdict verdict, string reason) { Verdict = verdict; Reason = reason; }
        public bool Ok => Verdict == EnterVerdict.Ok;
        public override string ToString() => Verdict + (Reason.Length > 0 ? ": " + Reason : "");
    }

    public static class SpectatorRules
    {
        public const int MaxSlots = 64;

        /// <summary>May this player spectate at all (ignores the slot cap)? This is the "allowed" bit of the S message.</summary>
        public static bool IsPermitted(RulesSettings s, bool isAdmin, bool onAllowedList)
            => s != null && s.Enabled && (s.AllowAll || onAllowedList || (s.AllowAdmins && isAdmin));

        /// <summary>
        /// Decides an enter request. Order: disabled, already spectating, permission, slots. A forced entry (admin
        /// <c>!spectator force</c>) skips the permission and the slot cap, but not the master switch.
        /// </summary>
        public static RuleResult CanEnter(RulesSettings s, bool isAdmin, bool onAllowedList, int spectatorCount, bool alreadySpectating, bool forced)
        {
            if (s == null || !s.Enabled) return new RuleResult(EnterVerdict.Disabled, "Spectator mode is switched off on this server.");
            if (alreadySpectating) return new RuleResult(EnterVerdict.Already, "Already spectating.");
            if (forced) return new RuleResult(EnterVerdict.Ok, "");
            if (!IsPermitted(s, isAdmin, onAllowedList)) return new RuleResult(EnterVerdict.NotAllowed, "You are not allowed to spectate. An admin can allow you (!spectator allow).");
            if (s.MaxSpectators > 0 && spectatorCount >= s.MaxSpectators)
                return new RuleResult(EnterVerdict.Full, "All spectator slots are taken (" + spectatorCount + "/" + s.MaxSpectators + ").");
            return new RuleResult(EnterVerdict.Ok, "");
        }

        /// <summary>True when an admin-forced entry goes past the slot cap (the server logs it).</summary>
        public static bool ForceExceedsCap(RulesSettings s, int spectatorCount) => s != null && s.MaxSpectators > 0 && spectatorCount >= s.MaxSpectators;
    }

    /// <summary>Players an admin designated, by Steam ID, with the last known name (persisted in the config).</summary>
    public sealed class AllowedList
    {
        private readonly List<KeyValuePair<ulong, string>> entries = new List<KeyValuePair<ulong, string>>();

        public AllowedList() { }
        public AllowedList(IEnumerable<KeyValuePair<ulong, string>> initial) { if (initial != null) foreach (var e in initial) Add(e.Key, e.Value); }

        public int Count => entries.Count;
        public IReadOnlyList<KeyValuePair<ulong, string>> Entries => entries.ToArray();
        public bool Contains(ulong steamId) => entries.Any(e => e.Key == steamId);
        public string NameOf(ulong steamId) => entries.FirstOrDefault(e => e.Key == steamId).Value ?? "";

        /// <summary>Adds or renames; false when the Steam ID was already listed (its name is refreshed).</summary>
        public bool Add(ulong steamId, string name)
        {
            if (steamId == 0) return false;
            name = CleanName(name);
            int i = entries.FindIndex(e => e.Key == steamId);
            if (i >= 0) { if (name.Length > 0) entries[i] = new KeyValuePair<ulong, string>(steamId, name); return false; }
            entries.Add(new KeyValuePair<ulong, string>(steamId, name));
            return true;
        }

        public bool Remove(ulong steamId) => entries.RemoveAll(e => e.Key == steamId) > 0;

        /// <summary>Refreshes a stored name when the player is seen online under a new one; true when it changed.</summary>
        public bool Rename(ulong steamId, string name)
        {
            name = CleanName(name);
            int i = entries.FindIndex(e => e.Key == steamId);
            if (i < 0 || name.Length == 0 || entries[i].Value == name) return false;
            entries[i] = new KeyValuePair<ulong, string>(steamId, name);
            return true;
        }

        public static string CleanName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var chars = name.Where(c => !char.IsControl(c)).ToArray();
            string s = new string(chars).Trim();
            return s.Length > 64 ? s.Substring(0, 64) : s;
        }
    }

    /// <summary>Unique name lookup used for players and the allowed list: exact (case-insensitive) first, then a unique substring.</summary>
    public static class NameMatch
    {
        public static T Find<T>(IEnumerable<T> items, Func<T, string> nameOf, string query, out string why) where T : class
        {
            why = "";
            string q = (query ?? "").Trim();
            if (q.Length == 0) { why = "no name given"; return null; }
            var list = (items ?? Enumerable.Empty<T>()).Where(i => i != null).ToList();
            var exact = list.Where(i => string.Equals(nameOf(i) ?? "", q, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            var partial = list.Where(i => (nameOf(i) ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (partial.Count == 1) return partial[0];
            if (exact.Count > 1) partial = exact;
            why = partial.Count == 0 ? "no player matches '" + q + "'" : "'" + q + "' matches " + partial.Count + " players: " + string.Join(", ", partial.Take(5).Select(nameOf));
            return null;
        }
    }
}
