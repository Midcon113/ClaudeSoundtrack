using ClaudeSoundtrack.Core.Services;

namespace ClaudeSoundtrack.App.ViewModels;

/// <summary>One readiness finding, formatted for the verify list.</summary>
public sealed class IssueRow
{
    public IssueRow(ReadinessIssue issue)
    {
        Issue = issue;
    }

    public ReadinessIssue Issue { get; }

    public string Message => Issue.Message;

    /// <summary>Uppercased for errors so the list scans quickly for real blockers.</summary>
    public string SeverityText => Issue.Severity switch
    {
        ReadinessSeverity.Error => "MUST FIX",
        ReadinessSeverity.Warning => "check",
        _ => "note"
    };

    public string TrackText => Issue.TrackNumber?.ToString() ?? "album";
}
