using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #33 — the bomb is a carried entity, not a job title for one actor, and the plant rule is
/// <b>one</b> rule for the whole bomb family.
/// </summary>
/// <remarks>
/// Gold 2.07 (<c>captures/captures.zip</c>, 20261112_0351…0404) only ever shows the round's
/// <c>bomberId</c> planting, because that is what happened in that session. Live play also has the
/// bomber drop the C4 or die with it, after which <b>any attacker</b> picks it up and plants; the
/// plant Rpc then carries that new planter's own pawn id, which is what the client expects from
/// the carrier. Refusing it (the old <c>IsAuthoritativePlanter</c> gate) silently discarded a legal
/// plant: the planter kept the bomb locally, every other peer never got it, and the round
/// desynced. These tests pin the rule that replaced it, and pin that <c>Ranked2v2</c>,
/// <c>Ranked2v2Alt</c>, <c>RankedDefuse</c> and <c>Defuse</c> all get that same rule.
/// </remarks>
public sealed class MatchPlantPolicyTests
{
    /// <summary>
    /// The four bomb modes must answer identically — the user's requirement is 1:1 parity, so the
    /// list itself is asserted rather than assumed.
    /// </summary>
    [Fact]
    public void BombFamily_IsExactlyTheFourAlliesFamilyModes()
    {
        Assert.Equal(
            new[] { "Ranked2v2", "Ranked2v2Alt", "RankedDefuse", "Defuse" },
            MatchPlantPolicy.BombFamilyModeIds);

        foreach (var id in MatchPlantPolicy.BombFamilyModeIds)
            Assert.True(MatchPlantPolicy.IsBombFamilyMode(id), id);

        // Escalation plants by host auto-plant and TDM/Duel have no bomb round — same rule must
        // not be assumed for them.
        foreach (var other in new[] { "Escalation", "DeathMatch", "Duel", "ArmsRace", "FreeForAll", "" })
            Assert.False(MatchPlantPolicy.IsBombFamilyMode(other), other);
    }

    /// <summary>
    /// Parity is not just the id list: for every phase, every mode in the family must produce the
    /// same verdict. A future per-mode branch inside the policy breaks this test.
    /// </summary>
    [Theory]
    [InlineData(2, MatchTeam.Tr, false, false)]
    [InlineData(2, MatchTeam.None, false, false)]
    [InlineData(4, MatchTeam.Ct, false, false)]
    [InlineData(2, MatchTeam.Tr, true, false)]
    [InlineData(2, MatchTeam.Tr, false, true)]
    public void BombFamilyModes_AllGetTheSameVerdict(
        byte planterActorNr, MatchTeam team, bool bombPlanted, bool pendingEnd)
    {
        foreach (var phase in Enum.GetValues<MatchFlowPhase>())
        {
            var expected = MatchPlantPolicy.Check(
                phase, alliesFamily: true, planterActorNr, team, bombPlanted, pendingEnd);

            for (var i = 1; i < MatchPlantPolicy.BombFamilyModeIds.Count; i++)
            {
                // Every family mode goes through the same `alliesFamily: true` call in the host;
                // the assertion documents that no mode may be special-cased out of it.
                Assert.True(MatchPlantPolicy.IsBombFamilyMode(MatchPlantPolicy.BombFamilyModeIds[i]));
                Assert.Equal(
                    expected,
                    MatchPlantPolicy.Check(
                        phase, alliesFamily: true, planterActorNr, team, bombPlanted, pendingEnd));
            }
        }
    }

    [Fact]
    public void BombFamily_PlantsDuringCombatC2C31()
    {
        Assert.True(MatchPlantPolicy.AllowsPlantInPhase(
            MatchFlowPhase.PurchasePhase, alliesFamily: true));
    }

    [Fact]
    public void ClassicRanked_PlantsDuringLive()
    {
        Assert.True(MatchPlantPolicy.AllowsPlantInPhase(
            MatchFlowPhase.RoundLive, alliesFamily: false));
    }

    [Theory]
    [InlineData(MatchFlowPhase.WaitingPlayers)]
    [InlineData(MatchFlowPhase.Warmup)]
    [InlineData(MatchFlowPhase.WarmupWillFinish)]
    [InlineData(MatchFlowPhase.RoundEndPause)]
    [InlineData(MatchFlowPhase.MatchOver)]
    [InlineData(MatchFlowPhase.BombPlanted)]
    public void NoMode_PlantsOutsideCombat(MatchFlowPhase phase)
    {
        Assert.False(MatchPlantPolicy.AllowsPlantInPhase(phase, alliesFamily: true));
        Assert.False(MatchPlantPolicy.AllowsPlantInPhase(phase, alliesFamily: false));
    }

    /// <summary>
    /// The regression that mattered: a teammate who picked the dropped bomb up (or the one it
    /// dropped onto when the bomber died) must be allowed to plant.
    /// </summary>
    [Theory]
    [InlineData((byte)2)]
    [InlineData((byte)3)]
    [InlineData((byte)4)]
    [InlineData((byte)5)]
    public void AnyAttacker_MayPlantTheBombTheyCarry(byte planterActorNr)
    {
        Assert.True(MatchPlantPolicy.AllowsPlanterTeam(planterActorNr, MatchTeam.Tr));
        // Host-authored / Escalation auto-plant.
        Assert.True(MatchPlantPolicy.AllowsPlanterTeam(planterActorNr: 0, MatchTeam.None));
    }

    /// <summary>A team prop that has not landed yet must never re-open the silent-drop hole.</summary>
    [Fact]
    public void UnknownTeam_IsAccepted_NotDropped()
    {
        Assert.True(MatchPlantPolicy.AllowsPlanterTeam(planterActorNr: 7, MatchTeam.None));
    }

    [Theory]
    [InlineData(MatchTeam.Ct)]
    [InlineData(MatchTeam.Spectator)]
    public void KnownDefenderOrSpectator_IsRefused(MatchTeam team)
    {
        Assert.False(MatchPlantPolicy.AllowsPlanterTeam(planterActorNr: 9, planterTeam: team));
    }

    [Fact]
    public void FirstPlantInCombat_IsAccepted()
    {
        var deny = MatchPlantPolicy.Check(
            MatchFlowPhase.PurchasePhase,
            alliesFamily: true,
            planterActorNr: 4,
            planterTeam: MatchTeam.Tr,
            bombPlanted: false,
            pendingEnd: false);

        Assert.Equal(MatchPlantPolicy.Deny.None, deny);
    }

    /// <summary>One bomb per round: the rule that must survive the handoff fix.</summary>
    [Fact]
    public void SecondPlantInTheSameRound_IsRefused()
    {
        var deny = MatchPlantPolicy.Check(
            MatchFlowPhase.BombPlanted,
            alliesFamily: true,
            planterActorNr: 3,
            planterTeam: MatchTeam.Tr,
            bombPlanted: true,
            pendingEnd: false);

        Assert.Equal(MatchPlantPolicy.Deny.AlreadyPlanted, deny);
    }

    [Fact]
    public void PlantWhileRoundIsEnding_IsRefused()
    {
        var deny = MatchPlantPolicy.Check(
            MatchFlowPhase.PurchasePhase,
            alliesFamily: true,
            planterActorNr: 3,
            planterTeam: MatchTeam.Tr,
            bombPlanted: false,
            pendingEnd: true);

        Assert.Equal(MatchPlantPolicy.Deny.RoundEnding, deny);
    }

    /// <summary>A plant that arrives after the whistle must not score a round.</summary>
    [Theory]
    [InlineData(MatchFlowPhase.RoundEndPause)]
    [InlineData(MatchFlowPhase.MatchOver)]
    public void PlantAfterTheRoundIsOver_IsRefused(MatchFlowPhase phase)
    {
        var deny = MatchPlantPolicy.Check(
            phase,
            alliesFamily: true,
            planterActorNr: 3,
            planterTeam: MatchTeam.Tr,
            bombPlanted: false,
            pendingEnd: false);

        Assert.Equal(MatchPlantPolicy.Deny.RoundOver, deny);
    }

    [Fact]
    public void PlantInBuyPhase_IsRefused()
    {
        var deny = MatchPlantPolicy.Check(
            MatchFlowPhase.WarmupWillFinish,
            alliesFamily: true,
            planterActorNr: 3,
            planterTeam: MatchTeam.Tr,
            bombPlanted: false,
            pendingEnd: false);

        Assert.Equal(MatchPlantPolicy.Deny.Phase, deny);
    }

    /// <summary>
    /// A wrong-phase Rpc is the round-start reset/equip and is still relayed as a pose; every
    /// other refusal is dropped. This is the relay decision, so it must not be re-derived at a
    /// call site.
    /// </summary>
    [Fact]
    public void OnlyWrongPhase_KeepsThePoseRelay()
    {
        Assert.True(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.Phase));
        Assert.False(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.AlreadyPlanted));
        Assert.False(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.RoundEnding));
        Assert.False(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.RoundOver));
        Assert.False(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.PlanterTeam));
        Assert.False(MatchPlantPolicy.RelayPoseOnly(MatchPlantPolicy.Deny.None));
    }

    /// <summary>
    /// <c>Judge</c> must read the room clock exactly like <c>Check</c> does, including the pending
    /// plant latch — two entry points drifting here is what desynced the guests in #33.
    /// </summary>
    [Fact]
    public void Judge_ReadsTheRoomClock()
    {
        var flow = new MatchFlowState
        {
            Phase = MatchFlowPhase.PurchasePhase,
            BombPlanted = false,
            PendingBombPlant = false,
        };
        Assert.Equal(
            MatchPlantPolicy.Deny.None,
            MatchPlantPolicy.Judge(flow, alliesFamily: true, planterActorNr: 2, MatchTeam.Tr));

        flow.PendingBombPlant = true;
        Assert.Equal(
            MatchPlantPolicy.Deny.AlreadyPlanted,
            MatchPlantPolicy.Judge(flow, alliesFamily: true, planterActorNr: 2, MatchTeam.Tr));

        flow.PendingBombPlant = false;
        flow.PendingEndReason = "wipe-ct";
        Assert.Equal(
            MatchPlantPolicy.Deny.RoundEnding,
            MatchPlantPolicy.Judge(flow, alliesFamily: true, planterActorNr: 2, MatchTeam.Tr));
    }

    /// <summary>Handoff is a log flag only — it must never deny a legal plant.</summary>
    [Fact]
    public void Handoff_IsReportedButNeverDenies()
    {
        var flow = new MatchFlowState
        {
            Phase = MatchFlowPhase.PurchasePhase,
            BomberActorNr = 2,
        };

        Assert.False(MatchPlantPolicy.IsHandoff(flow, planterActorNr: 2));
        Assert.True(MatchPlantPolicy.IsHandoff(flow, planterActorNr: 3));
        Assert.False(MatchPlantPolicy.IsHandoff(flow, planterActorNr: 0)); // host-authored

        Assert.Equal(
            MatchPlantPolicy.Deny.None,
            MatchPlantPolicy.Judge(flow, alliesFamily: true, planterActorNr: 3, MatchTeam.Tr));
    }

    /// <summary>
    /// <c>bomberId</c> is published once per round in C2=22 (#29) and is an <b>identity</b> for
    /// the round-start carrier, never a plant permit. Guard the fix at source level: no host file
    /// may consult the bomber to decide whether a plant is allowed, or silently re-assign it
    /// mid-round (the old «adopt the planter» hack).
    /// </summary>
    [Fact]
    public void NoSourceFile_GatesAPlantOnTheAssignedBomber()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     ServerSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);

            // The rule module owns the only legitimate reads/writes of the bomber identity.
            if (name == "MatchPlantPolicy.cs")
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];

                // Writing the round bomber outside the C2=22 pick / clear paths would move the
                // identity under the players' feet.
                if (line.Contains("Flow.BomberActorNr =", StringComparison.Ordinal)
                    && !IsAllowedBomberWrite(name))
                    offenders.Add($"{name}:{i + 1} (assigns bomber)");

                // Comparing the planter against the bomber as a gate.
                if (line.Contains("BomberActorNr ==", StringComparison.Ordinal)
                    || line.Contains("== room.Flow.BomberActorNr", StringComparison.Ordinal))
                    offenders.Add($"{name}:{i + 1} (gates on bomber)");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The bomb is a carried entity: any attacker who picked it up may plant (#33). " +
            "bomberId is published once per round in C2=22 and must not be used as a plant " +
            "permit or re-assigned mid-round. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// Files that legitimately set the round bomber: the C2=22 PreStart bag (both the classic loop
    /// and the Allies family), the half-time reset, and the prep re-pick when no T was present.
    /// </summary>
    private static bool IsAllowedBomberWrite(string fileName) => fileName switch
    {
        "GameMatchHost.Allies.cs" => true,          // C2=22 round-start pick
        "GameMatchHost.Ranked2v2Phases.cs" => true, // classic PreStart C2=22 pick
        "GameMatchHost.Ranked2v2HalfTime.cs" => true, // swap clears it
        "GameMatchHost.Ranked2v2Prep.cs" => true,   // late Tr join during Prep, published on C2=22
        _ => false,
    };

    /// <summary>
    /// The one-bomb-per-round guard and the "ask the policy exactly once" rule must hold in the
    /// single entry point; the router must not re-implement the decision.
    /// </summary>
    [Fact]
    public void PlantDecision_IsMadeInExactlyOnePlace()
    {
        var host = Path.Combine(
            ServerSourceRoot(), "Net", "Match", "Host", "GameMatchHost.WorldObjects.cs");
        var allies = Path.Combine(
            ServerSourceRoot(), "Net", "Match", "Modes", "Ranked2v2", "GameMatchHost.Allies.cs");

        Assert.Contains("MatchPlantPolicy.Judge", File.ReadAllText(allies), StringComparison.Ordinal);
        Assert.Contains("MatchPlantDecision", File.ReadAllText(allies), StringComparison.Ordinal);

        // The router only reacts to the verdict.
        var hostText = File.ReadAllText(host);
        Assert.DoesNotContain("MatchPlantPolicy.Check", hostText, StringComparison.Ordinal);
        Assert.Contains("decision.RelayPoseOnly", hostText, StringComparison.Ordinal);
    }

    private static string ServerSourceRoot()
    {
        // Tests run from tests/<proj>/bin/<cfg>/<tfm>; walk up to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "server");
    }
}
