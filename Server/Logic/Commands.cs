using System;
using System.Globalization;
using System.Linq;

namespace SiSpectator.Logic
{
    public enum SpectateAction { Toggle, On, Off, Status, View, Fow, Help, Invalid }

    /// <summary>
    /// Player commands: <c>/spectate [on|off|status|view &lt;player&gt;|fow &lt;all|sol|cent|alien&gt;|help]</c>, alias
    /// <c>/spec</c>, and <c>/play</c> (= <c>/spectate off</c>). Parses the full chat line the Admin Mod hands over
    /// (prefix character + command word + arguments).
    /// </summary>
    public sealed class SpectateCommand
    {
        public SpectateAction Action;
        public string Arg = "";     // player query for View, canonical fog value for Fow
        public string Error = "";   // usage text for Invalid

        public const string Usage = "/spectate [on|off|status] | /spectate view <player> | /spectate fow <all|sol|cent|alien> | /play";

        public static SpectateCommand Parse(string line)
        {
            var tokens = Tokens(line);
            string word = tokens.Length > 0 ? CommandWord(tokens[0]) : "";
            if (word == "play") return new SpectateCommand { Action = SpectateAction.Off };
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "";
            string rest = tokens.Length > 2 ? string.Join(" ", tokens, 2, tokens.Length - 2) : "";
            switch (sub)
            {
                case "": case "toggle": return new SpectateCommand { Action = SpectateAction.Toggle };
                case "on": case "enter": case "start": return new SpectateCommand { Action = SpectateAction.On };
                case "off": case "leave": case "stop": case "exit": return new SpectateCommand { Action = SpectateAction.Off };
                case "status": case "info": return new SpectateCommand { Action = SpectateAction.Status };
                case "help": case "?": return new SpectateCommand { Action = SpectateAction.Help };
                case "view": case "follow": case "watch":
                    if (rest.Length == 0) return Invalid("usage: /spectate view <player>");
                    return new SpectateCommand { Action = SpectateAction.View, Arg = rest };
                case "fow": case "fog":
                {
                    string f = Fog(rest);
                    if (f == null) return Invalid("usage: /spectate fow <all|sol|cent|alien>");
                    return new SpectateCommand { Action = SpectateAction.Fow, Arg = f };
                }
                default: return Invalid("usage: " + Usage);
            }
        }

        /// <summary>Canonical fog perspective: all | sol | cent | alien, null for anything else.</summary>
        public static string Fog(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "all": case "none": case "off": case "everything": return "all";
                case "sol": return "sol";
                case "cent": case "centauri": return "cent";
                case "alien": case "aliens": return "alien";
                default: return null;
            }
        }

        private static SpectateCommand Invalid(string error) => new SpectateCommand { Action = SpectateAction.Invalid, Error = error };

        internal static string[] Tokens(string line) => (line ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>"/spectate" -> "spectate"; prefix characters the Admin Mod accepts are ! / .</summary>
        internal static string CommandWord(string token)
        {
            string t = (token ?? "").Trim();
            if (t.Length > 0 && (t[0] == '!' || t[0] == '/' || t[0] == '.')) t = t.Substring(1);
            return t.ToLowerInvariant();
        }
    }

    public enum AdminAction { Help, Allow, Deny, List, Force, Kick, All, Max, Reload, Status, Invalid }

    /// <summary>Admin command: <c>!spectator allow|deny|list|force|kick &lt;player&gt;|all on|off|max &lt;n&gt;|reload|status</c>.</summary>
    public sealed class SpectatorAdminCommand
    {
        public AdminAction Action;
        public string Arg = "";
        public bool On;
        public int Number;
        public string Error = "";

        public const string Usage = "!spectator allow|deny|force|kick <player> | list | all on|off | max <n> (0 = unlimited) | reload | status";

        public static SpectatorAdminCommand Parse(string line)
        {
            var tokens = SpectateCommand.Tokens(line);
            string sub = tokens.Length > 1 ? tokens[1].ToLowerInvariant() : "";
            string rest = tokens.Length > 2 ? string.Join(" ", tokens, 2, tokens.Length - 2) : "";
            switch (sub)
            {
                case "": case "help": case "?": return new SpectatorAdminCommand { Action = AdminAction.Help };
                case "list": return new SpectatorAdminCommand { Action = AdminAction.List };
                case "status": return new SpectatorAdminCommand { Action = AdminAction.Status };
                case "reload": return new SpectatorAdminCommand { Action = AdminAction.Reload };
                case "allow": case "add": return Target(AdminAction.Allow, rest, "allow");
                case "deny": case "remove": case "disallow": return Target(AdminAction.Deny, rest, "deny");
                case "force": return Target(AdminAction.Force, rest, "force");
                case "kick": return Target(AdminAction.Kick, rest, "kick");
                case "all":
                {
                    bool? v = OnOff(rest);
                    if (v == null) return Invalid("usage: !spectator all on|off");
                    return new SpectatorAdminCommand { Action = AdminAction.All, On = v.Value };
                }
                case "max":
                {
                    if (!int.TryParse(rest.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > SpectatorRules.MaxSlots)
                        return Invalid("usage: !spectator max <0-" + SpectatorRules.MaxSlots + "> (0 = unlimited)");
                    return new SpectatorAdminCommand { Action = AdminAction.Max, Number = n };
                }
                default: return Invalid("usage: " + Usage);
            }
        }

        public static bool? OnOff(string value)
        {
            switch ((value ?? "").Trim().ToLowerInvariant())
            {
                case "on": case "true": case "1": case "yes": return true;
                case "off": case "false": case "0": case "no": return false;
                default: return null;
            }
        }

        private static SpectatorAdminCommand Target(AdminAction action, string rest, string word)
            => rest.Trim().Length == 0 ? Invalid("usage: !spectator " + word + " <player>") : new SpectatorAdminCommand { Action = action, Arg = rest.Trim() };

        private static SpectatorAdminCommand Invalid(string error) => new SpectatorAdminCommand { Action = AdminAction.Invalid, Error = error };
    }
}
