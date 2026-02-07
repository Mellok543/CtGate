using CounterStrikeSharp.API.Modules.Utils;

namespace CtGateRecruitment;

public class CtGateBalanceService
{
    public bool IsCtSlotAvailable(IEnumerable<CCSPlayerController> players, int ctPerT)
    {
        var tCount = players.Count(p => p.Team == CsTeam.Terrorist);
        var ctCount = players.Count(p => p.Team == CsTeam.CounterTerrorist);

        var allowedCt = tCount * ctPerT;
        return ctCount < allowedCt;
    }
}
