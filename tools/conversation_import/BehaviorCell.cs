using System;

// `behavior:<node>` in an action cell: switch the speaker's brain into its node
// of that name (SetSpeakerBehaviorAction). Not checked here - the speaker's
// species is chosen on the placement, not in the sheet - so a misspelt node is
// an error when the action fires.
static class BehaviorCell
{
	const string Prefix = "behavior:";

	public static bool IsBehaviorToken(string token)
	{
		return token.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);
	}

	public static string Parse(string token, SheetRow row, Report report)
	{
		string node = token.Substring(Prefix.Length).Trim();
		if (node.Length == 0 || node.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0)
		{
			report.Error(row, $"'{token}' must name one behavior node of the speaker's brain - say 'behavior:Escape'");
			return null;
		}
		return node;
	}
}
