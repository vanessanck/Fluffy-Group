using UnityEngine;
using System.Collections.Generic;
using System.Linq;

// The computer player (Red in Single Player, both sides in AI vs AI).
// It is the same GameManager class (a "partial" class), split into its own file to keep things readable.
//
// Every turn the AI:
//   Building phase - looks at what it is short of and builds for it (Farm / Hunting Lodge for food,
//                    Well for water, Lumber Camp, Mine Quarry, House when full, Outposts toward resources
//                    it doesn't have, town upgrade, Barracks, Transmutation Lab, Warehouse), then trains
//                    warriors if it can feed them, then promotes.
//                    Tech path (AiTech): from the plan's techRound it collects Mana, upgrades the town,
//                    builds the Transmutation Lab and Mana Sanctums, and promotes every warrior it can.
//   Move phase     - splits its warriors into GATHERERS (stand on resource tiles it needs and collect
//                    them at Upkeep), DEFENDERS (when enemies come near the capital) and the ARMY.
//                    The army waits at a rally point until it is strong enough, then attacks together.
//   Fight phase    - picks the best target for each warrior: capture the capital if it can, finish
//                    off weak warriors, and avoids attacks where it would die for nothing.
//
// Three plans (pick them in the menu for AI vs AI):
//   Balanced   - gathers, builds, attacks with a medium army when it is stronger
//   Aggressive - small economy, Barracks early, attacks early with Riders
//   Economic   - many gatherers, upgrades early, promotes, attacks late with a big army
public partial class GameManager
{
    public enum AiStrategy { Balanced, Aggressive, Economic, Random }

    public static AiStrategy blueStrategy = AiStrategy.Balanced;   // used in AI vs AI (and after Play)
    public static AiStrategy redStrategy = AiStrategy.Balanced;    // the enemy in Single Player too

    [Header("Computer player")]
    [Tooltip("Enemies this close to the capital make the AI defend.")]
    public int aiThreatRadius = 4;
    [Tooltip("Gatherers look for resource tiles at most this far from their capital.")]
    public int aiGatherRadius = 7;

    // Numbers that make each plan different
    class AiPlan
    {
        public float gathererShare;     // part of the warriors that gather
        public int baseArmy;            // warriors wanted at the start ...
        public int armyGrowEvery;       // ... +1 every this many rounds
        public int minAttackers;        // army size before attacking
        public float attackRatio;       // our army strength / enemy army strength needed to attack
        public int earliestAttack;      // round
        public int forceAttackRound;    // after this round attack with what we have
        public int upgradeAfterBuildings;
        public int barracksRound;
        public int techRound;           // from this round: collect Mana, upgrade the town, build the Lab, promote
        public float[] mix;             // wanted share of Squire, Hunter, Scout, Rider
    }

    static AiPlan PlanFor(AiStrategy s)
    {
        switch (s)
        {
            case AiStrategy.Aggressive:
                return new AiPlan { gathererShare = 0.2f, baseArmy = 5, armyGrowEvery = 2, minAttackers = 3,
                                    attackRatio = 0.9f, earliestAttack = 4, forceAttackRound = 18,
                                    upgradeAfterBuildings = 5, barracksRound = 3, techRound = 8,
                                    mix = new[] { 2f, 2f, 1f, 4f } };
            case AiStrategy.Economic:
                return new AiPlan { gathererShare = 0.5f, baseArmy = 3, armyGrowEvery = 4, minAttackers = 7,
                                    attackRatio = 1.5f, earliestAttack = 15, forceAttackRound = 50,
                                    upgradeAfterBuildings = 2, barracksRound = 20, techRound = 3,
                                    mix = new[] { 3f, 3f, 2f, 1f } };
            default:
                return new AiPlan { gathererShare = 0.35f, baseArmy = 4, armyGrowEvery = 3, minAttackers = 5,
                                    attackRatio = 1.25f, earliestAttack = 8, forceAttackRound = 35,
                                    upgradeAfterBuildings = 3, barracksRound = 10, techRound = 5,
                                    mix = new[] { 3f, 3f, 1f, 2f } };
        }
    }

    // What one computer side remembers between turns
    class AiBrain
    {
        public string owner;
        public AiStrategy strategy;
        public AiPlan plan;
        public string state = "Build up";         // Build up, Attack, Defend
        public Dictionary<Unit, string> roles = new Dictionary<Unit, string>();   // Gather, Army
        public HexTile rally;
        public int totalGathered;                  // for the research CSV
        public string lastAction = "";
        public bool savingForTech;                 // keeping Wood/Mineral/Mana for the town upgrade or the Lab
        public Cost techGoal;                      // the price being saved for (null = nothing)
    }

    Dictionary<string, AiBrain> brains = new Dictionary<string, AiBrain>();

    static AiStrategy NextStrategy(AiStrategy s)
    {
        return (AiStrategy)(((int)s + 1) % 4);
    }

    void SetupBrains()
    {
        brains.Clear();
        BrainFor("Player");
        BrainFor("Enemy");
    }

    AiBrain BrainFor(string owner)
    {
        AiBrain b;
        if (brains.TryGetValue(owner, out b)) return b;
        AiStrategy s = owner == "Player" ? blueStrategy : redStrategy;
        if (s == AiStrategy.Random) s = (AiStrategy)Random.Range(0, 3);
        b = new AiBrain { owner = owner, strategy = s, plan = PlanFor(s) };
        brains[owner] = b;
        return b;
    }

    AiStrategy StrategyFor(string owner)
    {
        return BrainFor(owner).strategy;
    }

    // ---------- Reading the situation ----------

    static float Strength(Unit u)
    {
        return u.hp * (u.type.attack + u.type.defense * 0.5f) / 10f;
    }

    float ArmyStrength(string owner)
    {
        float s = 0f;
        foreach (Unit u in units) if (u.owner == owner) s += Strength(u);
        return s;
    }

    // Enemy warriors close to this side's capital
    List<Unit> Threats(string owner)
    {
        HexTile town = FindTown(owner);
        if (town == null) return new List<Unit>();
        return units.Where(e => e.owner != owner && Dist(e.tile, town) <= aiThreatRadius).ToList();
    }

    // Food per turn, counting Meat when it can be eaten
    int FoodNet(string owner)
    {
        Cost net = NetIncome(owner);
        return net.wheat + (meatCountsAsFood ? net.meat : 0);
    }

    int FoodStock(string owner)
    {
        Cost s = sides[owner].stock;
        return s.wheat + (meatCountsAsFood ? s.meat : 0);
    }

    // How much this side wants each resource right now: 0.1 (plenty) .. about 2 (urgent)
    float Need(string owner, ResourceType r)
    {
        Side s = sides[owner];
        Cost net = NetIncome(owner);
        int target;
        switch (r)
        {
            case ResourceType.Wheat: target = 45; break;
            case ResourceType.Meat: target = 25; break;
            case ResourceType.Wood: target = 45; break;
            case ResourceType.Mineral: target = 40; break;
            case ResourceType.Water: target = 30; break;
            // Mana pays for the town upgrade (5), the Lab (5) and every promotion (5-6), so the AI always wants some
            default: target = HasBuilding(owner, "Transmutation Lab") ? 24 : 14; break;
        }
        float need = Mathf.Clamp((target - s.stock.Get(r)) / (float)target, 0.1f, 1f);
        if (net.Get(r) <= 0) need += 0.5f;
        if (r == ResourceType.Wheat && FoodNet(owner) < 0) need += 0.5f;
        if (r == ResourceType.Wheat && NeedsPeople(owner)) need += 0.6f;   // Wheat surplus grows citizens
        if (r == ResourceType.Meat && meatCountsAsFood && FoodNet(owner) < 0) need += 0.3f;
        if (r == ResourceType.Water && s.dehydrated) need += 0.5f;
        if (TechShort(owner, r) > 0) need += r == ResourceType.Mana ? 1.2f : 0.6f;   // saving for town upgrade / Lab
        return need;
    }

    // ---------- Building phase ----------

    void AiBuildingPhase(string owner)
    {
        AiBrain brain = BrainFor(owner);
        bool threatened = Threats(owner).Count > 0;
        brain.lastAction = "";

        if (threatened)
        {
            // Warriors first, then only the most important building
            AiTrainArmy(owner, true);
            AiEconomy(owner, true);
        }
        else
        {
            AiEconomy(owner, false);
            AiTrainArmy(owner, false);
        }
        AiPromote(owner);
    }

    // One build per turn, picked from a priority list
    void AiEconomy(string owner, bool threatened)
    {
        AiBrain brain = BrainFor(owner);
        AiPlan plan = brain.plan;
        Side s = sides[owner];
        Cost net = NetIncome(owner);
        int myBuildings = buildings.Values.Count(b => b.owner == owner);

        // 1. Food: a Farm (or Hunting Lodge); if no tile fits, grow toward one
        if (FoodNet(owner) < 2)
        {
            if (TryBuild(owner, "Farm") || TryBuild(owner, "Hunting Lodge")) return;
            if (!threatened && TryOutpostToward(owner, ResourceType.Wheat, ResourceType.Meat)) return;
        }
        // 1b. People: citizens only grow with a Wheat surplus, and every warrior needs a citizen
        if (NeedsPeople(owner) && net.wheat < growthSurplus)
        {
            if (TryBuild(owner, "Farm")) return;
            if (!threatened && TryOutpostToward(owner, ResourceType.Wheat)) return;
        }
        // 2. Water
        if (net.water < 1)
        {
            if (TryBuild(owner, "Well")) return;
            if (!threatened && TryOutpostToward(owner, ResourceType.Water)) return;
        }
        if (threatened) return;   // save the rest for warriors

        // 2b. Tech: Mana -> town Level 2 -> Transmutation Lab -> Mana Sanctums for promotions
        if (AiTech(owner)) return;
        if (brain.savingForTech) return;   // only the must-haves above until the upgrade / Lab is paid for

        // 3. Room for more people
        if (PopUsed(owner) >= PopCap(owner) - 1 && TryBuild(owner, "House")) return;

        // 4. Building materials
        if (net.wood < 4 && TryBuild(owner, "Lumber Camp")) return;
        if (net.mineral < 3 && TryBuild(owner, "Mine Quarry")) return;

        // From here on the buildings are extras: while the army is too small, keep enough to train warriors
        bool saving = UnitCount(owner) < WantedArmy(owner);

        // 5. Town Level 2 once the basics stand (also done by AiTech from techRound on)
        if (myBuildings >= plan.upgradeAfterBuildings && (!saving || KeepsReserve(owner, upgradeCost))
            && UpgradeTown(owner)) { Did(owner, "upgraded the town"); return; }

        // 6. A Barracks gives a second place to train
        if (turn >= plan.barracksRound && !HasBuilding(owner, "Barracks") && TryBuild(owner, "Barracks", saving)) return;

        // 7. Promotions: Transmutation Lab, then Mana for it
        if (s.townLevel >= 2 && turn >= plan.techRound)
        {
            if (TryBuild(owner, "Transmutation Lab", saving)) return;
            if (net.mana <= 0 && TryBuild(owner, "Mana Sanctum", saving)) return;
        }

        // 8. Warehouse (at most 2) when Wheat, Wood or Mineral is about to hit the storage cap
        int cap = StorageCap(owner);
        bool nearCap = new[] { ResourceType.Wheat, ResourceType.Wood, ResourceType.Mineral }.Any(r => s.stock.Get(r) >= cap - 15);
        if (nearCap && CountBuildings(owner, "Warehouse") < 2 && TryBuild(owner, "Warehouse", saving)) return;

        // 9. More of whatever we really need
        foreach (ResourceType r in Res.All.OrderByDescending(r => Need(owner, r)))
        {
            if (Need(owner, r) < 0.6f) break;
            string b = ExtractorFor(r);
            if (b != null && TryBuild(owner, b, saving)) return;
        }

        // 10. Expand toward resource tiles we still don't own (each Outpost eats 1 Wheat, so not too many)
        if (CountBuildings(owner, "Outpost") < 2 + turn / 10 && !saving)
        {
            ResourceType most = Res.All.OrderByDescending(r => Need(owner, r)).First();
            if (TryOutpostToward(owner, most)) return;
        }

        // 11. A spare Barracks for the aggressive plan
        if (brain.strategy == AiStrategy.Aggressive && BarracksTiles(owner).Count < 2) TryBuild(owner, "Barracks", saving);
    }

    // The tech path every plan follows from plan.techRound: get Mana, upgrade the town to Level 2,
    // build the Transmutation Lab, then keep Mana coming for promotions.
    // Returns true when it used this turn's build. Sets brain.savingForTech while it is saving up.
    bool AiTech(string owner)
    {
        AiBrain brain = BrainFor(owner);
        Side s = sides[owner];
        brain.savingForTech = false;
        brain.techGoal = null;
        if (turn < brain.plan.techRound) return false;

        Cost goal = null;
        if (s.townLevel < 2)
        {
            if (UpgradeTown(owner)) { Did(owner, "upgraded the town"); return true; }
            goal = upgradeCost;
        }
        else if (!HasBuilding(owner, "Transmutation Lab"))
        {
            if (TryBuild(owner, "Transmutation Lab")) return true;
            BuildingType lab = FindType("Transmutation Lab");
            goal = lab != null ? lab.cost : null;
        }

        // Mana income: a Mana Sanctum on a Mana tile, or an Outpost toward one
        Cost net = NetIncome(owner);
        int manaSanctums = CountBuildings(owner, "Mana Sanctum");
        bool wantSanctum = net.mana <= 0 || (goal == null && manaSanctums < 2);
        if (wantSanctum)
        {
            if (TryBuild(owner, "Mana Sanctum")) return true;
            if (manaSanctums == 0 && s.stock.mana < 5 && CountBuildings(owner, "Outpost") < 3 + turn / 10
                && TryOutpostToward(owner, ResourceType.Mana)) return true;
        }

        if (goal == null) return false;

        // Still saving: don't spend what the upgrade / Lab needs on extras or extra warriors,
        // and build income for whatever is short (Need() also sends gatherers there)
        brain.techGoal = goal;
        brain.savingForTech = s.stock.wood < goal.wood || s.stock.mineral < goal.mineral || s.stock.mana < goal.mana;
        if (s.stock.mineral < goal.mineral && net.mineral < 4)
        {
            if (TryBuild(owner, "Mine Quarry")) return true;
            if (CountBuildings(owner, "Outpost") < 3 + turn / 10 && TryOutpostToward(owner, ResourceType.Mineral)) return true;
        }
        if (s.stock.wood < goal.wood && net.wood < 5 && TryBuild(owner, "Lumber Camp")) return true;
        return false;
    }

    // How much of the upgrade / Lab price is still missing for this resource (0 = none)
    int TechShort(string owner, ResourceType r)
    {
        AiBrain b = BrainFor(owner);
        if (!b.savingForTech || b.techGoal == null) return 0;
        return Mathf.Max(0, b.techGoal.Get(r) - sides[owner].stock.Get(r));
    }

    static string ExtractorFor(ResourceType r)
    {
        switch (r)
        {
            case ResourceType.Wheat: return "Farm";
            case ResourceType.Meat: return "Hunting Lodge";
            case ResourceType.Wood: return "Lumber Camp";
            case ResourceType.Mineral: return "Mine Quarry";
            case ResourceType.Water: return "Well";
            case ResourceType.Mana: return "Mana Sanctum";
            default: return null;
        }
    }

    // Few citizens left and room to grow
    bool NeedsPeople(string owner)
    {
        return sides[owner].citizens <= 1 && PopUsed(owner) < PopCap(owner);
    }

    int CountBuildings(string owner, string typeName)
    {
        return buildings.Values.Count(b => b.owner == owner && b.type.name == typeName);
    }

    int WantedArmy(string owner)
    {
        AiPlan plan = BrainFor(owner).plan;
        return plan.baseArmy + turn / Mathf.Max(1, plan.armyGrowEvery);
    }

    // After paying this, is there still enough left to train a warrior?
    bool KeepsReserve(string owner, Cost price)
    {
        Cost st = sides[owner].stock;
        return st.wheat - price.wheat >= 8 && st.wood - price.wood >= 8 && st.mineral - price.mineral >= 6;
    }

    // saving = only build if enough is left over to train a warrior afterwards
    bool TryBuild(string owner, string typeName, bool saving = false)
    {
        if (saving)
        {
            BuildingType t = FindType(typeName);
            if (t == null || !KeepsReserve(owner, t.cost)) return false;
        }
        if (!BuildAnywhere(owner, typeName)) return false;
        Did(owner, "built a " + typeName);
        return true;
    }

    void Did(string owner, string what)
    {
        AiBrain b = BrainFor(owner);
        b.lastAction = b.lastAction == "" ? what : b.lastAction + ", " + what;
    }

    // Builds an Outpost on the border tile closest to an unclaimed tile with one of these resources
    bool TryOutpostToward(string owner, params ResourceType[] wanted)
    {
        BuildingType outpost = FindType("Outpost");
        if (outpost == null || sides[owner].builtThisTurn || !CanPay(owner, outpost.cost)) return false;

        HexTile enemyTown = FindTown(Enemy(owner));
        List<HexTile> goals = map.tiles.Where(t => t.owner == "" && t.town == "" &&
                                                   System.Array.IndexOf(wanted, t.resource) >= 0 &&
                                                   (enemyTown == null || Dist(t, enemyTown) > 3)).ToList();
        if (goals.Count == 0) return false;

        HexTile best = null;
        int bestD = int.MaxValue;
        foreach (HexTile t in map.tiles)
        {
            if (!CanBuild(outpost, t, owner)) continue;
            foreach (HexTile g in goals)
            {
                int d = Dist(t, g);
                if (d < bestD) { bestD = d; best = t; }
            }
        }
        if (best == null || bestD > 4) return false;   // too far to be worth it
        if (!Build(outpost, best, owner)) return false;
        Did(owner, "built an Outpost toward " + string.Join("/", wanted.Select(r => r.ToString()).ToArray()));
        return true;
    }

    // Trains warriors at the capital and each Barracks while it can feed them and wants more
    void AiTrainArmy(string owner, bool urgent)
    {
        AiBrain brain = BrainFor(owner);
        AiPlan plan = brain.plan;
        int wanted = WantedArmy(owner);

        // Saving for the town upgrade / Lab: keep only a small army until it is paid for
        if (!urgent && brain.savingForTech) wanted = Mathf.Min(wanted, plan.baseArmy + 1);

        int tries = TrainingTiles(owner).Count;
        for (int i = 0; i < tries; i++)
        {
            if (!urgent && UnitCount(owner) >= wanted) return;
            // Keep about 5 turns of food after feeding one more warrior
            if (FoodStock(owner) + 5 * (FoodNet(owner) - tier1Wheat) < 5) return;

            UnitType type = PickUnitType(owner, urgent);
            if (type == null || !TrainUnit(owner, type)) return;
            Did(owner, "trained a " + type.name);
        }
    }

    // The affordable Tier 1 type that is most under its wanted share
    UnitType PickUnitType(string owner, bool urgent)
    {
        AiPlan plan = BrainFor(owner).plan;
        List<UnitType> tier1 = Tier1Types();
        int total = UnitCount(owner) + 1;
        float mixSum = plan.mix.Sum();

        UnitType best = null;
        float bestScore = float.MinValue;
        for (int i = 0; i < tier1.Count; i++)
        {
            UnitType t = tier1[i];
            if (!CanTrain(owner, t)) continue;
            float wantShare = i < plan.mix.Length ? plan.mix[i] / mixSum : 0.1f;
            int have = units.Count(u => u.owner == owner && (u.type == t || u.type.name == t.promotesTo));
            float score = wantShare - have / (float)total;
            if (urgent) score += t.attack * 0.02f;       // defending: hit hard
            if (score > bestScore) { bestScore = score; best = t; }
        }
        return best;
    }

    // Promote every Tier 1 warrior standing on our own land (army, defenders and gatherers).
    // Army warriors first, so the fighters get stronger before the gatherers.
    // While attacking, only warriors that haven't left home yet promote (CanPromote needs our own tile).
    void AiPromote(string owner)
    {
        AiBrain brain = BrainFor(owner);
        List<Unit> order = units.Where(u => u.owner == owner)
                                .OrderBy(u => brain.roles.ContainsKey(u) && brain.roles[u] == "Gather")
                                .ThenByDescending(u => u.type.attack).ToList();
        foreach (Unit u in order)
        {
            if (!CanPromote(u)) continue;
            if (Promote(u)) Did(owner, "promoted to " + u.type.name);
        }
    }

    // ---------- Move phase ----------

    void AiMovePhase(string owner)
    {
        AiBrain brain = BrainFor(owner);
        AiPlan plan = brain.plan;
        HexTile home = FindTown(owner);
        HexTile enemyTown = FindTown(Enemy(owner));
        if (home == null || enemyTown == null) return;

        // Forget warriors that died
        foreach (Unit dead in brain.roles.Keys.Where(u => !units.Contains(u)).ToList()) brain.roles.Remove(dead);

        List<Unit> mine = units.Where(u => u.owner == owner).ToList();
        List<Unit> threats = Threats(owner);

        // --- Pick gatherers: keep the old ones, then Scouts, then the weakest fighters
        int wantGather = mine.Count >= 2 ? Mathf.Max(1, Mathf.RoundToInt(mine.Count * plan.gathererShare)) : 0;
        if (threats.Count > 0) wantGather = Mathf.Min(wantGather, 1);
        List<Unit> order = mine.OrderByDescending(u => brain.roles.ContainsKey(u) && brain.roles[u] == "Gather")
                               .ThenByDescending(u => u.type.name == "Scout" || u.type.name == "Ranger")
                               .ThenBy(u => u.type.attack).ThenBy(u => u.type.tier).ToList();
        for (int i = 0; i < order.Count; i++) brain.roles[order[i]] = i < wantGather ? "Gather" : "Army";

        List<Unit> army = mine.Where(u => brain.roles[u] == "Army").ToList();
        List<Unit> gatherers = mine.Where(u => brain.roles[u] == "Gather").ToList();

        // --- Army state
        float myStr = army.Sum(u => Strength(u));
        float theirStr = ArmyStrength(Enemy(owner));
        if (threats.Count > 0) brain.state = "Defend";
        else if (brain.state == "Defend") brain.state = "Build up";

        if (brain.state == "Build up")
        {
            bool strongEnough = army.Count >= plan.minAttackers && myStr >= plan.attackRatio * theirStr && turn >= plan.earliestAttack;
            bool lateGame = turn >= plan.forceAttackRound && army.Count >= 2;
            if (strongEnough || lateGame) { brain.state = "Attack"; Did(owner, "is attacking"); }
        }
        else if (brain.state == "Attack" && army.Count < Mathf.Max(2, plan.minAttackers / 2))
        {
            brain.state = "Build up";
            Did(owner, "pulls back to regroup");
        }

        // --- Gatherers walk to the resource tiles this side needs most
        HashSet<HexTile> claimed = new HashSet<HexTile>();
        foreach (Unit g in gatherers)
        {
            if (gameOver || !units.Contains(g) || g.movesLeft <= 0) continue;
            HexTile spot = PickGatherTile(g, home, claimed);
            if (spot == null) { AiMoveTo(g, home, 2); continue; }
            claimed.Add(spot);
            if (spot != g.tile) AiMoveTo(g, spot, 0);
            g.movesLeft = 0;
        }

        // --- Army
        if (brain.rally == null || !map.IsWalkable(brain.rally.type)) brain.rally = PickRally(owner, home, enemyTown);
        List<Unit> marching = army.OrderBy(u => Dist(u.tile, enemyTown)).ToList();
        int median = marching.Count > 0 ? Dist(marching[marching.Count / 2].tile, enemyTown) : 0;

        foreach (Unit u in marching)
        {
            if (gameOver || !units.Contains(u) || u.movesLeft <= 0) continue;
            if (FindTargets(u).Any(t => GoodTarget(u, t))) { u.movesLeft = 0; continue; }   // stay and fight

            if (brain.state == "Defend")
            {
                Unit near = threats.Where(e => units.Contains(e)).OrderBy(e => Dist(e.tile, u.tile)).FirstOrDefault();
                if (near != null) AiMoveTo(u, near.tile, u.type.range);
            }
            else if (brain.state == "Attack")
            {
                // Stay together: a warrior ahead of the middle of the army, with no friends close by,
                // waits one turn for the others to catch up (new recruits at home don't hold anyone back)
                int d = Dist(u.tile, enemyTown);
                int friends = army.Count(o => o != u && units.Contains(o) && Dist(o.tile, u.tile) <= 2);
                if (d < median - 1 && friends < 2 && d > u.type.range + 1) { u.movesLeft = 0; continue; }

                // Fight warriors in the way, otherwise go for the capital
                Unit block = units.Where(e => e.owner != owner && Dist(e.tile, u.tile) <= 3)
                                  .OrderBy(e => e.hp).FirstOrDefault();
                if (block != null) AiMoveTo(u, block.tile, u.type.range);
                else AiMoveTo(u, enemyTown, u.type.range);
            }
            else
            {
                // Build up: wait near the rally point. Hit enemy gatherers that come close and are weaker.
                Unit prey = units.Where(e => e.owner != owner && Dist(e.tile, u.tile) <= MovesFor(u) + u.type.range
                                             && Strength(e) < Strength(u)).OrderBy(e => e.hp).FirstOrDefault();
                if (prey != null) AiMoveTo(u, prey.tile, u.type.range);
                else if (brain.rally != null && Dist(u.tile, brain.rally) > 1) AiMoveTo(u, brain.rally, 1);
            }
            if (units.Contains(u)) u.movesLeft = 0;
        }
    }

    // Best resource tile for a gatherer: needed resource, close, safe, not taken by a friend
    HexTile PickGatherTile(Unit g, HexTile home, HashSet<HexTile> claimed)
    {
        Dictionary<HexTile, HexTile> cameFrom;
        Dictionary<HexTile, int> cost = CostsFrom(g.tile, g.owner, false, out cameFrom, g.type);

        HexTile best = null;
        float bestScore = float.MinValue, hereScore = float.MinValue;
        foreach (KeyValuePair<HexTile, int> kv in cost)
        {
            HexTile t = kv.Key;
            if (t.resource == ResourceType.None || t.town != "" || claimed.Contains(t)) continue;
            if (!gatherOutsideTerritory && t.owner != g.owner) continue;
            if (Dist(t, home) > aiGatherRadius) continue;
            if (t != g.tile && !CanEnter(t, g.owner)) continue;

            int danger = units.Count(e => e.owner != g.owner && Dist(e.tile, t) <= 2);
            float score = Need(g.owner, t.resource) * 10f - kv.Value * 1.2f - danger * 15f;
            if (t.owner == g.owner) score += 2f;
            if (t.owner == Enemy(g.owner)) score -= 3f;
            if (score > bestScore) { bestScore = score; best = t; }
            if (t == g.tile) hereScore = score;
        }
        // Already standing on a good tile: stay (it gathers again at the next Upkeep)
        if (hereScore > float.MinValue && hereScore >= bestScore - 3f) return g.tile;
        return best;
    }

    // A meeting point inside our land, 2-3 tiles from the capital, on the side facing the enemy
    HexTile PickRally(string owner, HexTile home, HexTile enemyTown)
    {
        Dictionary<HexTile, HexTile> cameFrom;
        Dictionary<HexTile, int> cost = CostsFrom(home, owner, true, out cameFrom);
        return cost.Keys.Where(t => t.town == "" && Dist(t, home) >= 2 && Dist(t, home) <= 3)
                        .OrderBy(t => Dist(t, enemyTown)).ThenBy(t => t.q).ThenBy(t => t.r).FirstOrDefault();
    }

    // Walks a warrior along the cheapest path until it is within 'range' of the target
    // (range 0 = onto the target tile). Gets as close as it can if the target can't be reached.
    void AiMoveTo(Unit u, HexTile target, int range)
    {
        if (u.movesLeft <= 0 || target == null) return;
        Dictionary<HexTile, HexTile> cameFrom;
        Dictionary<HexTile, int> cost = CostsFrom(u.tile, u.owner, false, out cameFrom, u.type);
        HexTile goal = ClosestGoal(cost, target, range, u);
        if (goal == null)
        {
            cost = CostsFrom(u.tile, u.owner, true, out cameFrom, u.type);   // plan through blockers
            goal = ClosestGoal(cost, target, range, u);
        }
        if (goal == null)
        {
            // Can't get in range: just get closer
            goal = cost.Keys.Where(t => CanEnter(t, u.owner) || t == u.tile)
                            .OrderBy(t => Dist(t, target)).ThenBy(t => cost[t]).FirstOrDefault();
        }
        if (goal == null || goal == u.tile) return;

        List<HexTile> path = new List<HexTile>();
        for (HexTile t = goal; t != u.tile; t = cameFrom[t]) path.Insert(0, t);

        int budget = u.movesLeft;
        bool firstStep = true;
        HexTile stop = null;
        foreach (HexTile t in path)
        {
            int c = MoveCost(u.type, t);
            if (c > budget && !firstStep) break;
            budget -= c;
            firstStep = false;

            Unit other = UnitAt(t);
            if (other != null && other.owner != u.owner) break;
            if (CanEnter(t, u.owner)) stop = t;
            if (budget <= 0) break;
        }

        // Stuck behind friends in a narrow gap (no free tile on the path)? Take any free tile that gets closer.
        // (Only then: a real path sometimes has to bend away from the target, around cliffs or water.)
        if (stop == null)
        {
            Dictionary<HexTile, HexTile> around;
            Dictionary<HexTile, int> near = CostsFrom(u.tile, u.owner, false, out around, u.type);
            HexTile side = near.Where(kv => kv.Value <= u.movesLeft && kv.Key != u.tile && CanEnter(kv.Key, u.owner))
                               .Select(kv => kv.Key)
                               .OrderBy(t => Dist(t, target)).ThenBy(t => near[t]).FirstOrDefault();
            if (side != null && Dist(side, target) < Dist(u.tile, target)) stop = side;
        }
        if (stop != null) PlaceUnit(u, stop);
    }

    HexTile ClosestGoal(Dictionary<HexTile, int> cost, HexTile target, int range, Unit u)
    {
        HexTile best = null;
        int bestCost = int.MaxValue;
        foreach (KeyValuePair<HexTile, int> kv in cost)
        {
            if (Dist(kv.Key, target) > range) continue;
            if (kv.Key != u.tile && !CanEnter(kv.Key, u.owner)) continue;
            if (kv.Value < bestCost) { bestCost = kv.Value; best = kv.Key; }
        }
        return best;
    }

    // ---------- Fight phase ----------

    void AiFightPhase(string owner)
    {
        AiBrain brain = BrainFor(owner);
        // Ranged warriors shoot first (no damage back), then melee
        List<Unit> mine = units.Where(u => u.owner == owner).OrderByDescending(u => u.type.range).ToList();
        foreach (Unit u in mine)
        {
            if (gameOver) break;
            if (!units.Contains(u) || u.hasAttacked) continue;

            HexTile best = null;
            float bestScore = float.MinValue;
            foreach (HexTile t in FindTargets(u))
            {
                float sc = TargetScore(u, t);
                if (sc > bestScore) { bestScore = sc; best = t; }
            }
            if (best == null) continue;

            string role;
            bool gatherer = brain.roles.TryGetValue(u, out role) && role == "Gather";
            float needed = gatherer ? 5f : brain.state == "Defend" ? -10f : brain.state == "Attack" ? -5f : 0f;
            if (bestScore >= needed) Attack(u, best);
        }
    }

    bool GoodTarget(Unit u, HexTile t)
    {
        return TargetScore(u, t) >= 0f;
    }

    // Higher = better attack. Capturing the capital beats everything; dying for nothing is bad.
    float TargetScore(Unit a, HexTile t)
    {
        int dmg, back;
        PreviewAttack(a, t, out dmg, out back);
        Unit d = UnitAt(t);
        if (d == null || d.owner == a.owner)
        {
            if (t.town == "" || t.owner == a.owner || !townHP.ContainsKey(t)) return float.MinValue;
            return townHP[t] - dmg <= 0 ? 10000f : 75f + dmg * 4f;   // the capital is the real goal
        }
        bool kill = d.hp - dmg <= 0;
        bool die = a.hp - back <= 0;
        float score = dmg * 2f - back * 1.5f;
        if (kill) score += 40f + d.type.tier * 10f;
        if (die) score -= kill ? 15f : 50f;
        HexTile home = FindTown(a.owner);
        if (home != null && Dist(d.tile, home) <= aiThreatRadius) score += 10f;   // protect the capital
        return score;
    }

    // ---------- On screen (AI vs AI / Play) ----------

    Rect AiStatusRect()
    {
        return new Rect(10, infoRect.yMax + 10, 270, 128);
    }

    void DrawAiStatus()
    {
        Rect r = AiStatusRect();
        GUI.Box(r, "");
        GUIStyle st = new GUIStyle(GUI.skin.label);
        st.fontSize = 11;
        st.wordWrap = true;
        float y = r.y + 4;
        foreach (string owner in new[] { "Player", "Enemy" })
        {
            if (owner == "Player" && !autoPlay) continue;
            AiBrain b = BrainFor(owner);
            Side s = sides[owner];
            int gatherers = b.roles.Count(kv => kv.Value == "Gather" && units.Contains(kv.Key));
            st.fontStyle = FontStyle.Bold;
            st.normal.textColor = SideColor(owner);
            GUI.Label(new Rect(r.x + 8, y, r.width - 16, 16), SideLabel(owner) + " (" + b.strategy + "): " + b.state, st);
            y += 15;
            st.fontStyle = FontStyle.Normal;
            st.normal.textColor = Color.white;
            GUI.Label(new Rect(r.x + 8, y, r.width - 16, 16),
                      "Warriors " + UnitCount(owner) + " (" + gatherers + " gathering)   Buildings " +
                      buildings.Values.Count(x => x.owner == owner) + "   Lv " + s.townLevel, st);
            y += 15;
            GUI.Label(new Rect(r.x + 8, y, r.width - 16, 16),
                      "Wh " + s.stock.wheat + " Me " + s.stock.meat + " Wd " + s.stock.wood + " Mi " + s.stock.mineral +
                      " Wa " + s.stock.water + " Ma " + s.stock.mana, st);
            y += 15;
            if (b.lastAction != "")
            {
                GUI.Label(new Rect(r.x + 8, y, r.width - 16, 16), "Last: " + b.lastAction, st);
                y += 15;
            }
            y += 3;
        }
    }
}
