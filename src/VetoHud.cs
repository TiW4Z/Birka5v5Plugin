using System.Text;

namespace Birka5v5;

public partial class Birka5v5Plugin
{
    // HTML is rebuilt only when something changed; the OnTick refresh just re-sends the cached strings.
    private bool hudDirty = true;
    private string hudActingTeam = "";
    private string hudOthers = "";

    private void OnTick()
    {
        if (phase != MatchPhase.Veto || !VetoHud.Value) return;

        if (hudDirty)
        {
            RebuildVetoHud();
            hudDirty = false;
        }

        int actingTeam = vetoVoteActive ? CurrentVetoStep?.Team ?? -1 : -1;
        foreach (var player in HumanPlayers())
        {
            player.PrintToCenterHtml(player.TeamNum == actingTeam ? hudActingTeam : hudOthers);
        }
    }

    private void ClearVetoHud()
    {
        foreach (var player in HumanPlayers())
        {
            player.PrintToCenterHtml(" ");
        }
    }

    private void RebuildVetoHud()
    {
        var step = CurrentVetoStep;
        if (vetoVoteActive && step != null)
        {
            string title = step.IsPick
                ? "<font class='fontSize-m' color='#40e070'>PICK A MAP</font>"
                : "<font class='fontSize-m' color='#ff4040'>BAN A MAP</font>";
            string timer = $"<font color='#80c0ff'>{Math.Max(0, vetoSecondsLeft)}s</font>";
            string teamColor = step.Team == TeamCT ? "#5ca8ff" : "#e0b040";

            hudActingTeam = $"{title}  <font color='#40e070'>YOUR TURN</font>  {timer}<br>{BuildMapRows(showVotes: true)}";
            hudOthers = $"{title}  <font color='{teamColor}'>{TeamShort(step.Team)} VOTING</font>  {timer}<br>{BuildMapRows(showVotes: false)}";
        }
        else
        {
            string html = $"{vetoHeadline}<br>{BuildMapRows(showVotes: false)}";
            hudActingTeam = html;
            hudOthers = html;
        }
    }

    private string BuildMapRows(bool showVotes)
    {
        string Cell(int i)
        {
            var map = vetoPool[i];
            var (state, team) = vetoMapStates[map.Name];
            string label = $"{i + 1}. {map.Label}";
            switch (state)
            {
                case MapState.Banned:
                    return $"<font color='#7a3a3a'>{label} (ban)</font>";
                case MapState.Picked:
                    string by = team == 0 ? "" : $" ({TeamShort(team)})";
                    return $"<font color='#40e070'>{label}{by}</font>";
                default:
                    int votes = showVotes ? vetoBallots.Values.Count(m => m == map.Name) : 0;
                    string votePart = votes > 0 ? $" <font color='#ffcc00'>{votes}</font>" : "";
                    return $"<font color='#c0c0c0'>{label}</font>{votePart}";
            }
        }

        // The CS2 center HUD clips at about 8 lines, so large pools go two per row.
        var sb = new StringBuilder();
        if (vetoPool.Count > 7)
        {
            for (int i = 0; i < vetoPool.Count; i += 2)
            {
                sb.Append(Cell(i));
                if (i + 1 < vetoPool.Count) sb.Append("   <font color='#606060'>|</font>   ").Append(Cell(i + 1));
                sb.Append("<br>");
            }
        }
        else
        {
            for (int i = 0; i < vetoPool.Count; i++) sb.Append(Cell(i)).Append("<br>");
        }
        return sb.ToString();
    }
}
