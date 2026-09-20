using SimRigMode.Models;

namespace SimRigMode.Services.Usb;

public sealed record SanitizerFinding(string Code, string Rule, string Detail)
{
    public override string ToString() => $"{Code} [{Rule}]: {Detail}";
}

/// <summary>
/// Validates USB rules coming from outside the running process - an imported file, or the
/// on-disk config, which a user can hand-edit and which is the likelier of the two.
///
/// This is not theoretical. Before the matching guards landed, a config containing a single rule
/// with an empty InstanceId and Desktop=Disabled was a machine-wide USB kill switch, delivered
/// by opening a file: the empty id compared equal to the empty id of every synthetic Port node,
/// and rule inheritance then applied it to every device on the machine. See docs/report.md.
///
/// Everything here fails closed: an unusable rule is dropped, and a rule whose blast radius
/// cannot be confirmed is neutered to DoNotChange rather than trusted.
/// </summary>
public static class UsbRuleSanitizer
{
    public const int MaxRules = 256;
    public const int MaxTargetsPerRule = 64;

    public static IReadOnlyList<SanitizerFinding> Sanitize(AppConfig config)
    {
        var findings = new List<SanitizerFinding>();
        var rules = config.UsbRules;

        if (rules.Count > MaxRules)
        {
            findings.Add(new("TOO_MANY_RULES", "(config)",
                $"{rules.Count} rules exceeded the limit of {MaxRules}; the extras were dropped."));
            rules.RemoveRange(MaxRules, rules.Count - MaxRules);
        }

        for (var i = rules.Count - 1; i >= 0; i--)
        {
            var rule = rules[i];
            var name = string.IsNullOrWhiteSpace(rule.FriendlyName) ? "(unnamed rule)" : rule.FriendlyName;

            // A rule with no identity field matches nothing by design now, but keeping it around
            // is still a trap: it looks like a configured rule in the UI and it is the exact
            // shape of the kill switch above.
            if (!rule.IsAddressable)
            {
                findings.Add(new("UNADDRESSABLE", name,
                    "The rule carries no device identity and could never name a real device. Dropped."));
                rules.RemoveAt(i);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(rule.InstanceId) && !IsPlausibleInstanceId(rule.InstanceId))
            {
                findings.Add(new("BAD_INSTANCE_ID", name,
                    "The rule's device instance id is not a usable device path. Dropped."));
                rules.RemoveAt(i);
                continue;
            }

            // Disabling a hub disconnects its whole branch. No imported or hand-edited value for
            // this was ever backed by a confirmation on THIS machine, so it is never trusted.
            if (rule.DisableHubItself)
            {
                findings.Add(new("HUB_SELF_DISABLE_RESET", name,
                    "Disabling the hub itself was reset. Re-confirm it in Settings if you want it."));
                rule.DisableHubItself = false;
            }

            rule.LastKnownTargets ??= [];
            if (rule.LastKnownTargets.Count > MaxTargetsPerRule)
            {
                findings.Add(new("TOO_MANY_TARGETS", name,
                    $"{rule.LastKnownTargets.Count} remembered devices exceeded the limit of {MaxTargetsPerRule}; the extras were dropped."));
                rule.LastKnownTargets.RemoveRange(MaxTargetsPerRule, rule.LastKnownTargets.Count - MaxTargetsPerRule);
            }

            rule.LastKnownTargets.RemoveAll(target =>
                string.IsNullOrWhiteSpace(target.InstanceId) || !IsPlausibleInstanceId(target.InstanceId));
        }

        return findings;
    }

    /// <summary>
    /// A device instance id must look like one. This mirrors the shape gate the elevated worker
    /// applies independently; neither trusts the other.
    /// </summary>
    public static bool IsPlausibleInstanceId(string instanceId) =>
        !string.IsNullOrWhiteSpace(instanceId) &&
        instanceId.Length <= 512 &&
        !instanceId.Any(char.IsControl) &&
        instanceId.Contains('\\') &&
        instanceId.All(character =>
            char.IsLetterOrDigit(character) || character is '\\' or '&' or '_' or '-' or '.' or '{' or '}' or '#' or '+');
}
