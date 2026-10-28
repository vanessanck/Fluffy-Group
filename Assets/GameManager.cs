using UnityEngine;
using System.Collections.Generic;
using System.Linq;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class GameManager : MonoBehaviour
{
    public UnitType[] unitTypes = new UnitType[]
    {
        new UnitType { name = "Knight",  letter = "K", maxHP = 30, attack = 8, defense = 6,
                       moves = 1, range = 1, size = new Vector3(0.40f, 0.40f, 0.40f),
                       cost = new Cost { wheat = 8, wood = 4, mineral = 8 } },
        new UnitType { name = "Cavalry", letter = "C", maxHP = 22, attack = 7, defense = 3,
                       moves = 3, range = 1, size = new Vector3(0.45f, 0.30f, 0.45f),
                       cost = new Cost { wheat = 10, wood = 6, mineral = 4 } },
        new UnitType { name = "Scout",   letter = "S", maxHP = 12, attack = 3, defense = 2,
                       moves = 3, range = 1, size = new Vector3(0.25f, 0.25f, 0.25f),
                       cost = new Cost { wheat = 5, wood = 3 } },
        new UnitType { name = "Archer",  letter = "A", maxHP = 15, attack = 6, defense = 2,
                       moves = 2, range = 2, size = new Vector3(0.30f, 0.35f, 0.30f),
                       cost = new Cost { wheat = 6, wood = 8, mineral = 2 } },
    };

    [Header("Towns and combat")]
    public int townMaxHP = 40;
    public int townDefense = 4;
    public int woodsCoverBonus = 2;     // extra defense against ranged attacks in Woods and Taiga

    [Header("Start")]
    public Cost startStock = new Cost { wheat = 40, meat = 15, wood = 40, mineral = 25, water = 30, mana = 0 };
    public int startCitizens = 3;

    [Header("Town Center")]
    public Cost townLevel1Output = new Cost { wheat = 2, water = 2 };
    public Cost townLevel2Output = new Cost { wheat = 4, water = 4, mana = 1 };
    public Cost upgradeCost = new Cost { wood = 30, mineral = 25, mana = 5 };
    public int upgradedTownRadius = 3;
    public int upgradedTownHP = 70;

    [Header("Population and storage")]
    public int basePopCap = 10;         // citizens + warriors allowed with a Level 1 town
    public int level2PopBonus = 5;
    public int baseStorage = 100;       // most you can keep of each resource
    public int growthSurplus = 5;       // wheat left over per turn needed to gain 1 citizen

    [Header("Upkeep per turn")]
    public int citizenWheat = 1;
    public int citizenWater = 1;
    public int tier1Wheat = 1;
    public int tier2Wheat = 2;
    public int tier2Water = 1;

    [Header("Deficit penalties")]
    [Range(0f, 1f)] public float starvationAttackPenalty = 0.25f;
    public int starvationTurnsToLose = 2;   // turns of starvation before a citizen and a warrior are lost
    public int dehydrationMovePenalty = 1;

    HexMap map;
    Camera cam;
    bool ready = false;
    int turn = 1;

    BuildingType[] buildingTypes;
    Dictionary<string, Side> sides = new Dictionary<string, Side>();
    Dictionary<HexTile, Building> buildings = new Dictionary<HexTile, Building>();

    List<Unit> units = new List<Unit>();
    Dictionary<GameObject, Unit> unitLookup = new Dictionary<GameObject, Unit>();
    Dictionary<HexTile, int> townHP = new Dictionary<HexTile, int>();
    Dictionary<HexTile, int> townMax = new Dictionary<HexTile, int>();
    List<HexTile> reachable = new List<HexTile>();
    Dictionary<HexTile, int> moveCosts = new Dictionary<HexTile, int>();
    List<HexTile> targets = new List<HexTile>();

    Unit selectedUnit;
    HexTile selectedTile;

    string message = "";
    bool gameOver = false;
    string resultText = "";

    Rect infoRect = new Rect(10, 10, 340, 170);
    GUIStyle letterStyle, bigStyle, warnStyle;

    void Start()
    {
        map = FindFirstObjectByType<HexMap>();
        cam = Camera.main;
        buildingTypes = BuildingCatalog.Create();
    }

    void Update()
    {
        if (map == null || map.tiles == null || map.tiles.Count == 0 || map.tiles[0].obj == null) return;
        if (!ready)
        {
            Random.InitState(map.usedSeed);   // same map seed = same enemy choices

            foreach (HexTile t in map.tiles)
            {
                if (t.town == "") continue;
                townHP[t] = townMaxHP;
                townMax[t] = townMaxHP;
            }

            foreach (string name in new string[] { "Player", "Enemy" })
            {
                sides[name] = new Side { name = name, stock = startStock.Copy(), citizens = startCitizens };
                SpawnArmy(name);
            }
            ready = true;
        }
        if (gameOver) return;

        bool click = false;
        Vector2 mousePos = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        Mouse mouse = Mouse.current;
        if (mouse != null)
        {
            click = mouse.leftButton.wasPressedThisFrame;
            mousePos = mouse.position.ReadValue();
        }
#else
        click = Input.GetMouseButtonDown(0);
        mousePos = Input.mousePosition;
#endif

        if (click && !OverUI(mousePos)) HandleClick(mousePos);
    }

    // ---------- Creating warriors ----------

    // Free tiles near the town that can be walked to (not across cliffs or deep water), closest first
    List<HexTile> FreeSpotsNear(HexTile town, string owner, int maxRing)
    {
        Dictionary<HexTile, HexTile> cameFrom;
        Dictionary<HexTile, int> cost = CostsFrom(town, owner, true, out cameFrom);
        return cost.Keys
            .Where(t => t != town && Dist(t, town) <= maxRing && CanEnter(t, owner))
            .OrderBy(t => Dist(t, town)).ThenBy(t => t.q).ThenBy(t => t.r)
            .ToList();
    }

    void SpawnArmy(string owner)
    {
        HexTile town = FindTown(owner);
        if (town == null) return;

        List<HexTile> spots = FreeSpotsNear(town, owner, 3);
        for (int i = 0; i < unitTypes.Length && i < spots.Count; i++)
            CreateUnit(owner, unitTypes[i], spots[i]);
    }

    Unit CreateUnit(string owner, UnitType type, HexTile tile)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        go.name = owner + " " + type.name;
        go.transform.localScale = type.size;

        Unit u = new Unit { owner = owner, type = type, obj = go };
        u.hp = type.maxHP;
        u.movesLeft = type.moves;
        units.Add(u);
        unitLookup[go] = u;
        SetUnitColor(u, false);
        PlaceUnit(u, tile);
        return u;
    }

    void PlaceUnit(Unit u, HexTile tile)
    {
        u.tile = tile;
        float top = tile.obj.transform.localScale.y;
        u.obj.transform.position = tile.obj.transform.position + Vector3.up * (top + u.type.size.y);
    }

    void SetUnitColor(Unit u, bool isTarget)
    {
        Color c = u.owner == "Player" ? map.playerColor : map.enemyColor;
        if (isTarget) c = Color.yellow;
        u.obj.GetComponent<Renderer>().material.color = c;
    }

    void Kill(Unit u)
    {
        units.Remove(u);
        unitLookup.Remove(u.obj);
        Destroy(u.obj);
        if (selectedUnit == u) selectedUnit = null;
    }

    int UnitCount(string owner)
    {
        int n = 0;
        foreach (Unit u in units) if (u.owner == owner) n++;
        return n;
    }

    // ---------- Pathfinding ----------

    int MoveCost(HexTile t)
    {
        return map.MoveCost(t.type);
    }

    // Can a path step from one tile into the next? (cliffs block, own warriors can be walked through, enemies block)
    bool Passable(HexTile from, HexTile to, string owner, bool ignoreUnits)
    {
        if (!map.CanStep(from, to)) return false;
        if (to.town != "" && to.owner != owner) return false;
        if (!ignoreUnits)
        {
            Unit other = UnitAt(to);
            if (other != null && other.owner != owner) return false;
        }
        return true;
    }

    // Dijkstra: the cheapest move cost from start to every reachable tile
    Dictionary<HexTile, int> CostsFrom(HexTile start, string owner, bool ignoreUnits,
                                       out Dictionary<HexTile, HexTile> cameFrom)
    {
        Dictionary<HexTile, int> cost = new Dictionary<HexTile, int>();
        cameFrom = new Dictionary<HexTile, HexTile>();
        List<HexTile> open = new List<HexTile>();

        cost[start] = 0;
        open.Add(start);

        while (open.Count > 0)
        {
            // Take the open tile with the lowest cost
            HexTile cur = open[0];
            foreach (HexTile t in open)
                if (cost[t] < cost[cur]) cur = t;
            open.Remove(cur);

            foreach (HexTile n in map.Neighbors(cur))
            {
                if (!Passable(cur, n, owner, ignoreUnits)) continue;
                int c = cost[cur] + MoveCost(n);
                if (!cost.ContainsKey(n) || c < cost[n])
                {
                    cost[n] = c;
                    cameFrom[n] = cur;
                    if (!open.Contains(n)) open.Add(n);
                }
            }
        }
        return cost;
    }

    // ---------- Economy ----------

    int PopCap(string owner)
    {
        int cap = basePopCap + (sides[owner].townLevel >= 2 ? level2PopBonus : 0);
        foreach (Building b in buildings.Values)
            if (b.owner == owner) cap += b.type.popCap;
        return cap;
    }

    int PopUsed(string owner)
    {
        return sides[owner].citizens + UnitCount(owner);
    }

    int StorageCap(string owner)
    {
        int cap = baseStorage;
        foreach (Building b in buildings.Values)
            if (b.owner == owner) cap += b.type.storage;
        return cap;
    }

    // Phase 1, step 1: what the town and the extraction buildings produce
    Cost Harvest(string owner)
    {
        Cost h = (sides[owner].townLevel >= 2 ? townLevel2Output : townLevel1Output).Copy();
        foreach (Building b in buildings.Values)
        {
            if (b.owner != owner || b.type.produces == ResourceType.None) continue;
            if (b.tile.owner != owner) continue;    // only inside your own territory
            int amount = b.type.amount;
            if (b.type.produces == ResourceType.Mana && b.tile.type == TileType.MagicForest) amount *= 3;
            h.Add(b.type.produces, amount);
        }
        return h;
    }

    // Phase 1, steps 2 and 3: what citizens, warriors and outposts eat and drink
    Cost Upkeep(string owner)
    {
        Side s = sides[owner];
        Cost u = new Cost();
        u.wheat += s.citizens * citizenWheat;
        u.water += s.citizens * citizenWater;
        foreach (Unit w in units)
        {
            if (w.owner != owner) continue;
            if (w.type.tier >= 2) { u.wheat += tier2Wheat; u.water += tier2Water; }
            else u.wheat += tier1Wheat;
        }
        foreach (Building b in buildings.Values)
            if (b.owner == owner) u.wheat += b.type.wheatUpkeep;
        return u;
    }

    // Harvest minus upkeep, per resource (can be negative)
    Cost NetIncome(string owner)
    {
        Cost h = Harvest(owner);
        Cost u = Upkeep(owner);
        Cost net = new Cost();
        foreach (ResourceType r in Res.All) net.Set(r, h.Get(r) - u.Get(r));
        return net;
    }

    // Phase 1: harvest, pay upkeep, grow, and apply starvation and dehydration
    void RunUpkeep(string owner)
    {
        Side s = sides[owner];
        Cost net = NetIncome(owner);
        foreach (ResourceType r in Res.All) s.stock.Add(r, net.Get(r));

        bool wheatShort = s.stock.wheat < 0;
        bool waterShort = s.stock.water < 0;

        // No negative stockpiles, and anything above the storage cap is wasted
        int cap = StorageCap(owner);
        foreach (ResourceType r in Res.All)
            s.stock.Set(r, Mathf.Clamp(s.stock.Get(r), 0, cap));

        // Starvation
        s.starving = wheatShort;
        s.starveTurns = wheatShort ? s.starveTurns + 1 : 0;
        if (s.starveTurns >= starvationTurnsToLose)
        {
            if (s.citizens > 0) s.citizens--;
            Unit weakest = null;
            foreach (Unit u in units)
            {
                if (u.owner != owner) continue;
                if (weakest == null || u.type.tier < weakest.type.tier ||
                    (u.type.tier == weakest.type.tier && u.hp < weakest.hp)) weakest = u;
            }
            if (weakest != null) Kill(weakest);
            message = owner == "Player" ? "Starvation! You lost a citizen and a warrior."
                                        : "The enemy is starving and lost a warrior.";
            CheckGameOver();
        }

        // Dehydration
        s.dehydrated = waterShort;

        // Growth: enough wheat left over, no shortage, and room in the population cap
        if (!wheatShort && !waterShort && net.wheat >= growthSurplus && PopUsed(owner) < PopCap(owner))
            s.citizens++;
    }

    bool CanPay(string owner, Cost c)
    {
        Cost s = sides[owner].stock;
        foreach (ResourceType r in Res.All)
            if (s.Get(r) < c.Get(r)) return false;
        return true;
    }

    void Pay(string owner, Cost c)
    {
        Cost s = sides[owner].stock;
        foreach (ResourceType r in Res.All) s.Add(r, -c.Get(r));
    }

    // Training turns one citizen into a warrior
    bool CanTrain(string owner, UnitType type)
    {
        Side s = sides[owner];
        return !s.trainedThisTurn && s.citizens > 0 && CanPay(owner, type.cost);
    }

    bool TrainUnit(string owner, UnitType type)
    {
        if (!CanTrain(owner, type)) return false;

        HexTile town = FindTown(owner);
        if (town == null) return false;

        List<HexTile> spots = FreeSpotsNear(town, owner, 2);
        if (spots.Count == 0)
        {
            if (owner == "Player") message = "No free tile next to your town.";
            return false;
        }

        Pay(owner, type.cost);
        sides[owner].citizens--;
        Unit u = CreateUnit(owner, type, spots[0]);
        u.movesLeft = 0;       // new warriors can act next turn
        u.hasAttacked = true;
        sides[owner].trainedThisTurn = true;

        message = (owner == "Player" ? "You trained a " : "The enemy trained a ") + type.name;
        return true;
    }
    // ---------- Buildings ----------

    void ClaimAround(HexTile center, int radius, string owner)
    {
        foreach (HexTile t in map.tiles)
            if (t.owner == "" && Dist(t, center) <= radius) map.Claim(t, owner);
    }

    void AddModel(HexTile t, PrimitiveType shape, Vector3 size, Vector3 offset, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(shape);
        Destroy(go.GetComponent<Collider>());
        go.transform.parent = map.transform;
        go.transform.localScale = size;
        float halfHeight = shape == PrimitiveType.Cylinder ? size.y : size.y / 2f;
        float top = t.obj.transform.localScale.y;
        go.transform.position = t.obj.transform.position + offset + Vector3.up * (top + halfHeight);
        go.GetComponent<Renderer>().material.color = color;
    }

    Color SideColor(string owner)
    {
        return owner == "Player" ? map.playerColor : map.enemyColor;
    }

    // Can this building go on this tile? (ignores cost and the one-build-per-turn rule)
    bool FitsTile(BuildingType type, HexTile t, string owner)
    {
        if (t.owner != owner || t.town != "" || buildings.ContainsKey(t)) return false;
        if (!map.IsWalkable(t.type)) return false;

        if (type.needsResource != ResourceType.None)
            return t.resource == type.needsResource;    // extraction: only on its own resource tile

        if (t.type == TileType.Coast) return false;     // no houses, warehouses or outposts on water

        if (type.borderOnly)
        {
            foreach (HexTile n in map.Neighbors(t))
                if (n.owner == "") return true;
            return false;
        }
        return true;
    }

    bool CanBuild(BuildingType type, HexTile t, string owner)
    {
        return !sides[owner].builtThisTurn && FitsTile(type, t, owner) && CanPay(owner, type.cost);
    }

    bool Build(BuildingType type, HexTile t, string owner)
    {
        if (!CanBuild(type, t, owner)) return false;

        Pay(owner, type.cost);
        buildings[t] = new Building { type = type, owner = owner, tile = t };
        if (type.claimRadius > 0) ClaimAround(t, type.claimRadius, owner);

        Color c = type.name == "Outpost" ? Color.Lerp(SideColor(owner), Color.white, 0.3f) : type.color;
        AddModel(t, type.shape, type.size, new Vector3(0.35f, 0f, 0.3f), c);
        sides[owner].builtThisTurn = true;

        message = (owner == "Player" ? "You built a " : "The enemy built a ") + type.name + ".";
        return true;
    }

    bool CanUpgradeTown(string owner)
    {
        Side s = sides[owner];
        return !s.builtThisTurn && s.townLevel < 2 && CanPay(owner, upgradeCost);
    }

    bool UpgradeTown(string owner)
    {
        if (!CanUpgradeTown(owner)) return false;

        HexTile town = FindTown(owner);
        if (town == null) return false;

        Pay(owner, upgradeCost);
        sides[owner].townLevel = 2;
        townMax[town] = upgradedTownHP;
        townHP[town] = upgradedTownHP;
        ClaimAround(town, upgradedTownRadius, owner);
        AddModel(town, PrimitiveType.Cube, new Vector3(0.3f, 0.8f, 0.3f),
                 new Vector3(-0.05f, 0f, 0.45f), Color.Lerp(SideColor(owner), Color.black, 0.25f));
        sides[owner].builtThisTurn = true;

        message = owner == "Player" ? "Your town is now Level 2!" : "The enemy town is now Level 2!";
        return true;
    }

    BuildingType FindType(string name)
    {
        foreach (BuildingType b in buildingTypes) if (b.name == name) return b;
        return null;
    }

    // Tries to place a building of this kind anywhere in the territory. Tiles nearest the town go first.
    bool BuildAnywhere(string owner, string typeName)
    {
        BuildingType type = FindType(typeName);
        HexTile town = FindTown(owner);
        if (type == null || town == null) return false;

        List<HexTile> options = map.tiles.Where(t => CanBuild(type, t, owner))
                                         .OrderBy(t => Dist(t, town)).ThenBy(t => t.q).ThenBy(t => t.r).ToList();
        return options.Count > 0 && Build(type, options[0], owner);
    }

    // ---------- Enemy economy ----------

    void EnemyBuild()
    {
        Side s = sides["Enemy"];
        Cost net = NetIncome("Enemy");

        if (UpgradeTown("Enemy")) return;

        // Fix shortages first
        if (net.wheat <= 0 && BuildAnywhere("Enemy", "Farm")) return;
        if (net.water <= 0 && BuildAnywhere("Enemy", "Well")) return;
        if (PopUsed("Enemy") >= PopCap("Enemy") - 1 && BuildAnywhere("Enemy", "House")) return;

        // Then grow the economy
        foreach (string name in new string[] { "Farm", "Lumber Camp", "Mine Quarry", "Well",
                                               "Mana Sanctum", "Hunting Lodge" })
            if (BuildAnywhere("Enemy", name)) return;

        // Nothing left to harvest inside the territory: expand
        if (Random.value < 0.5f) BuildAnywhere("Enemy", "Outpost");
    }

    void EnemyTrain()
    {
        if (sides["Enemy"].stock.wheat < 15) return;   // keep enough food
        int start = Random.Range(0, unitTypes.Length);
        for (int i = 0; i < unitTypes.Length; i++)
        {
            UnitType type = unitTypes[(start + i) % unitTypes.Length];
            if (TrainUnit("Enemy", type)) return;
        }
    }

    // ---------- Helpers ----------

    int Dist(HexTile a, HexTile b)
    {
        return map.Distance(a, b);
    }

    Unit UnitAt(HexTile t)
    {
        foreach (Unit u in units)
            if (u.tile == t) return u;
        return null;
    }

    HexTile FindTown(string owner)
    {
        foreach (HexTile t in map.tiles)
            if (t.town != "" && t.owner == owner) return t;
        return null;
    }

    // Can a warrior STOP on this tile?
    bool CanEnter(HexTile t, string owner)
    {
        if (!map.IsWalkable(t.type)) return false;
        if (UnitAt(t) != null) return false;
        if (t.town != "" && t.owner != owner) return false;
        return true;
    }

    string Enemy(string owner)
    {
        return owner == "Player" ? "Enemy" : "Player";
    }

    string UnitName(Unit u)
    {
        return (u.owner == "Player" ? "Your " : "Enemy ") + u.type.name;
    }

    // ---------- Combat ----------

    // Starving armies hit 25% weaker
    int AttackOf(Unit u)
    {
        if (!sides[u.owner].starving) return u.type.attack;
        return Mathf.Max(1, Mathf.RoundToInt(u.type.attack * (1f - starvationAttackPenalty)));
    }

    // Dehydrated armies move 1 less (but always at least 1)
    int MovesFor(Unit u)
    {
        if (!sides[u.owner].dehydrated) return u.type.moves;
        return Mathf.Max(1, u.type.moves - dehydrationMovePenalty);
    }

    // Defense on the current tile. The attacker matters: Woods and Taiga only cover against ranged attacks.
    int DefenseOf(Unit u, Unit attacker)
    {
        int baseDef = u.type.defense;
        int d = baseDef;
        TileType t = u.tile.type;

        bool rangedAttack = attacker != null && attacker.type.range >= 2;
        if (rangedAttack && (t == TileType.Woods || t == TileType.Taiga)) d += woodsCoverBonus;

        if (t == TileType.MountainPass) d += Mathf.CeilToInt(baseDef * 0.25f);  // +25% on a pass
        if (t == TileType.Desert) d -= Mathf.CeilToInt(baseDef * 0.15f);        // -15% in the dunes
        if (t == TileType.Coast) d -= Mathf.CeilToInt(baseDef * 0.10f);         // -10% in shallow water
        return Mathf.Max(0, d);
    }

    // Ranged units on high ground (Hills or Plateau) shoot 1 tile further
    int RangeOf(Unit u)
    {
        bool highGround = u.tile.type == TileType.Hills || u.tile.type == TileType.Plateau;
        return u.type.range + (u.type.range >= 2 && highGround ? 1 : 0);
    }

    int Damage(int attack, int defense)
    {
        return Mathf.Max(1, attack - defense / 2);
    }

    void AttackUnit(Unit a, Unit d)
    {
        int distance = Dist(a.tile, d.tile);
        int dmg = Damage(AttackOf(a), DefenseOf(d, a));
        d.hp -= dmg;
        string log = UnitName(a) + " hit " + UnitName(d) + " for " + dmg;

        if (d.hp <= 0)
        {
            Kill(d);
            log += ". Destroyed!";
        }
        else if (distance == 1)
        {
            int back = Damage(AttackOf(d), DefenseOf(a, d));
            a.hp -= back;
            log += ", took " + back + " back";
            if (a.hp <= 0) { Kill(a); log += " and was destroyed!"; }
        }

        a.hasAttacked = true;
        a.movesLeft = 0;
        message = log;
        CheckGameOver();
    }

    void AttackTown(Unit a, HexTile town)
    {
        int dmg = Damage(AttackOf(a), townDefense);
        townHP[town] -= dmg;
        message = UnitName(a) + " hit " + town.town + " for " + dmg;
        a.hasAttacked = true;
        a.movesLeft = 0;

        if (townHP[town] <= 0)
        {
            message = town.town + " has been captured!";
            EndGame(a.owner == "Player" ? "You captured the enemy capital. You win!"
                                        : "Your capital was captured. You lose!");
        }
    }

    List<HexTile> FindTargets(Unit u)
    {
        List<HexTile> list = new List<HexTile>();
        if (u.hasAttacked) return list;

        int range = RangeOf(u);
        foreach (Unit other in units)
            if (other.owner != u.owner && Dist(u.tile, other.tile) <= range)
                list.Add(other.tile);

        HexTile enemyTown = FindTown(Enemy(u.owner));
        if (enemyTown != null && Dist(u.tile, enemyTown) <= range)
            list.Add(enemyTown);

        return list;
    }

    void Attack(Unit a, HexTile target)
    {
        Unit d = UnitAt(target);
        if (d != null && d.owner != a.owner) AttackUnit(a, d);
        else if (target.town != "" && target.owner != a.owner) AttackTown(a, target);
    }

    void CheckGameOver()
    {
        if (gameOver) return;
        bool playerAlive = false, enemyAlive = false;
        foreach (Unit u in units)
        {
            if (u.owner == "Player") playerAlive = true;
            else enemyAlive = true;
        }
        if (!enemyAlive) EndGame("All enemy warriors destroyed. You win!");
        else if (!playerAlive) EndGame("All your warriors were destroyed. You lose!");
    }

    void EndGame(string text)
    {
        gameOver = true;
        resultText = text;
        selectedUnit = null;
        reachable.Clear();
        targets.Clear();
        RefreshHighlights();
    }

    // ---------- Clicking ----------

    void HandleClick(Vector2 mousePos)
    {
        Ray ray = cam.ScreenPointToRay(mousePos);
        if (!Physics.Raycast(ray, out RaycastHit hit, 200f)) return;

        GameObject go = hit.collider.gameObject;
        Unit clickedUnit;
        unitLookup.TryGetValue(go, out clickedUnit);
        HexTile tile = clickedUnit != null ? clickedUnit.tile : map.GetTile(go);
        if (tile == null) return;

        // Attack a yellow target
        if (selectedUnit != null && targets.Contains(tile))
        {
            Unit attacker = selectedUnit;
            Attack(attacker, tile);
            selectedTile = tile;
            if (!gameOver && units.Contains(attacker)) SelectUnit(attacker);
            else { selectedUnit = null; reachable.Clear(); targets.Clear(); RefreshHighlights(); }
            return;
        }

        // Move to a lit tile (pays the path cost)
        if (selectedUnit != null && reachable.Contains(tile))
        {
            int cost = moveCosts.ContainsKey(tile) ? moveCosts[tile] : 1;
            selectedUnit.movesLeft = Mathf.Max(0, selectedUnit.movesLeft - cost);
            PlaceUnit(selectedUnit, tile);
            selectedTile = tile;
            SelectUnit(selectedUnit);
            return;
        }

        // Otherwise select what was clicked
        selectedTile = tile;
        if (clickedUnit == null) clickedUnit = UnitAt(tile);

        if (clickedUnit != null && clickedUnit.owner == "Player") SelectUnit(clickedUnit);
        else { selectedUnit = null; reachable.Clear(); targets.Clear(); RefreshHighlights(); }
    }

    void SelectUnit(Unit u)
    {
        selectedUnit = u;
        reachable.Clear();

        Dictionary<HexTile, HexTile> cameFrom;
        moveCosts = CostsFrom(u.tile, u.owner, false, out cameFrom);
        bool fullMoves = u.movesLeft > 0 && u.movesLeft == MovesFor(u);

        foreach (KeyValuePair<HexTile, int> kv in moveCosts)
        {
            HexTile t = kv.Key;
            if (t == u.tile || !CanEnter(t, u.owner)) continue;
            bool affordable = kv.Value <= u.movesLeft;
            bool oneStep = fullMoves && Dist(u.tile, t) == 1; // can always step one tile
            if (affordable || oneStep) reachable.Add(t);
        }

        targets = FindTargets(u);
        RefreshHighlights();
    }

    void RefreshHighlights()
    {
        foreach (HexTile t in map.tiles) map.SetHighlight(t, false);
        if (selectedTile != null) map.SetHighlight(selectedTile, true);
        foreach (HexTile t in reachable) map.SetHighlight(t, true);

        foreach (Unit u in units) SetUnitColor(u, targets.Contains(u.tile));
        foreach (HexTile t in targets)
            if (t.town != "") map.SetHighlight(t, true);
    }

    // ---------- Turns ----------

    void EndTurn()
    {
        if (gameOver) return;
        selectedUnit = null;
        reachable.Clear();
        targets.Clear();

        // Enemy turn
        HexTile playerTown = FindTown("Player");
        foreach (Unit u in new List<Unit>(units))
        {
            if (gameOver) break;
            if (u.owner != "Enemy" || !units.Contains(u)) continue;

            if (!EnemyTryAttack(u))
            {
                if (playerTown != null) EnemyMove(u, playerTown);
                EnemyTryAttack(u);
            }
        }
        if (!gameOver)
        {
            EnemyBuild();
            EnemyTrain();
        }

        // New turn, Phase 1: harvest, upkeep, growth, deficits
        if (!gameOver)
        {
            RunUpkeep("Player");
            RunUpkeep("Enemy");
        }
        foreach (Side s in sides.Values)
        {
            s.trainedThisTurn = false;
            s.builtThisTurn = false;
        }

        foreach (Unit u in units)
        {
            u.movesLeft = MovesFor(u);
            u.hasAttacked = false;
        }
        turn++;
        RefreshHighlights();
    }

    bool EnemyTryAttack(Unit u)
    {
        List<HexTile> list = FindTargets(u);
        if (list.Count == 0) return false;

        HexTile best = null;
        int lowestHP = int.MaxValue;
        foreach (HexTile t in list)
        {
            Unit d = UnitAt(t);
            if (d != null && d.hp < lowestHP) { lowestHP = d.hp; best = t; }
        }
        if (best == null) best = list[0];

        Attack(u, best);
        return true;
    }

    // The cheapest free tile from where this warrior can attack the target
    HexTile BestGoal(Dictionary<HexTile, int> cost, HexTile target, Unit u)
    {
        HexTile best = null;
        int bestCost = int.MaxValue;
        foreach (KeyValuePair<HexTile, int> kv in cost)
        {
            if (Dist(kv.Key, target) > u.type.range) continue;
            if (!CanEnter(kv.Key, u.owner)) continue;
            if (kv.Value < bestCost) { bestCost = kv.Value; best = kv.Key; }
        }
        return best;
    }

    // Enemy follows the shortest path toward the target (around water and cliffs, through passes)
    void EnemyMove(Unit u, HexTile target)
    {
        Dictionary<HexTile, HexTile> cameFrom;
        Dictionary<HexTile, int> cost = CostsFrom(u.tile, u.owner, false, out cameFrom);
        HexTile goal = BestGoal(cost, target, u);

        // If your warriors block every path, plan as if they weren't there and walk up to them
        if (goal == null)
        {
            cost = CostsFrom(u.tile, u.owner, true, out cameFrom);
            goal = BestGoal(cost, target, u);
        }
        if (goal == null) return; // no path at all

        // Rebuild the path from the goal back to the warrior
        List<HexTile> path = new List<HexTile>();
        for (HexTile t = goal; t != u.tile; t = cameFrom[t]) path.Insert(0, t);

        // Walk along the path as far as the move points allow
        int budget = MovesFor(u);
        bool firstStep = true;
        HexTile stop = null;
        foreach (HexTile t in path)
        {
            int c = MoveCost(t);
            if (c > budget && !firstStep) break;
            budget -= c;
            firstStep = false;

            Unit other = UnitAt(t);
            if (other != null && other.owner != u.owner) break; // blocked by your warrior
            if (CanEnter(t, u.owner)) stop = t;
            if (budget <= 0) break;
        }

        if (stop != null) PlaceUnit(u, stop);
    }

    // ---------- Screen ----------

    Rect EndTurnRect()
    {
        return new Rect(Screen.width - 170, Screen.height - 65, 160, 50);
    }

    Rect ResourceRect()
    {
        return new Rect(Screen.width - 260, 10, 250, 215);
    }

    bool TownPanelVisible()
    {
        return !gameOver && selectedTile != null && selectedTile.town != "" && selectedTile.owner == "Player";
    }

    Rect TownRect()
    {
        return new Rect(10, 190, 380, 35 + (unitTypes.Length + 1) * 35);
    }

    // Buildings that fit the selected tile
    List<BuildingType> BuildOptions()
    {
        List<BuildingType> list = new List<BuildingType>();
        if (gameOver || selectedTile == null) return list;
        foreach (BuildingType b in buildingTypes)
            if (FitsTile(b, selectedTile, "Player")) list.Add(b);
        return list;
    }

    Rect BuildRect(int count)
    {
        return new Rect(10, 190, 380, 30 + count * 35);
    }

    bool OverUI(Vector2 mousePos)
    {
        Vector2 p = new Vector2(mousePos.x, Screen.height - mousePos.y);
        if (infoRect.Contains(p) || EndTurnRect().Contains(p) || ResourceRect().Contains(p)) return true;
        if (TownPanelVisible() && TownRect().Contains(p)) return true;
        int n = BuildOptions().Count;
        if (n > 0 && BuildRect(n).Contains(p)) return true;
        return false;
    }

    string OwnerName(string owner)
    {
        if (owner == "Player") return "You";
        if (owner == "Enemy") return "Enemy";
        return "Nobody";
    }

    string Signed(int v)
    {
        return v >= 0 ? "+" + v : v.ToString();
    }

    void OnGUI()
    {
        if (!ready) return;

        if (letterStyle == null)
        {
            letterStyle = new GUIStyle(GUI.skin.label);
            letterStyle.alignment = TextAnchor.MiddleCenter;
            letterStyle.fontStyle = FontStyle.Bold;
            letterStyle.fontSize = 14;
            letterStyle.normal.textColor = Color.white;

            bigStyle = new GUIStyle(GUI.skin.box);
            bigStyle.fontSize = 24;
            bigStyle.fontStyle = FontStyle.Bold;
            bigStyle.alignment = TextAnchor.MiddleCenter;
            bigStyle.wordWrap = true;

            warnStyle = new GUIStyle(GUI.skin.label);
            warnStyle.fontStyle = FontStyle.Bold;
            warnStyle.normal.textColor = new Color(1f, 0.4f, 0.3f);
        }

        // Letter and HP above each warrior
        foreach (Unit u in units)
        {
            Vector3 p = cam.WorldToScreenPoint(u.obj.transform.position + Vector3.up * (u.type.size.y + 0.25f));
            if (p.z > 0)
                GUI.Label(new Rect(p.x - 30, Screen.height - p.y - 12, 60, 24),
                          u.type.letter + " " + u.hp, letterStyle);
        }

        // Info box
        GUI.Box(infoRect, "");
        float y = 15f;
        GUI.Label(new Rect(20, y, 320, 25), "Turn " + turn); y += 20f;

        if (selectedTile == null)
        {
            GUI.Label(new Rect(20, y, 320, 25), "Click a tile, a warrior, or your town");
        }
        else
        {
            GUI.Label(new Rect(20, y, 320, 25),
                HexMap.TypeLabel(selectedTile.type) + "   Owner: " + OwnerName(selectedTile.owner)); y += 20f;
            if (selectedTile.resource != ResourceType.None)
            {
                GUI.Label(new Rect(20, y, 320, 25), "Resource tile: " + selectedTile.resource); y += 20f;
            }
            if (selectedTile.town != "" && townHP.ContainsKey(selectedTile))
            {
                int level = sides[selectedTile.owner].townLevel;
                GUI.Label(new Rect(20, y, 320, 25), selectedTile.town + " (Level " + level + ")   HP " +
                          townHP[selectedTile] + "/" + townMax[selectedTile]); y += 20f;
            }
            Building b;
            if (buildings.TryGetValue(selectedTile, out b))
            {
                string what = b.type.produces != ResourceType.None ? "  (+" + b.type.amount + " " + b.type.produces + ")" : "";
                GUI.Label(new Rect(20, y, 320, 25), "Building: " + b.type.name + what); y += 20f;
            }

            Unit u = UnitAt(selectedTile);
            if (u != null)
            {
                GUI.Label(new Rect(20, y, 320, 25), UnitName(u) + "   HP " + u.hp + "/" + u.type.maxHP); y += 20f;
                GUI.Label(new Rect(20, y, 320, 25),
                    "ATK " + AttackOf(u) + "   DEF " + DefenseOf(u, null) + "   Range " + RangeOf(u)); y += 20f;
                GUI.Label(new Rect(20, y, 320, 25),
                    "Moves " + u.movesLeft + "/" + MovesFor(u) + (u.hasAttacked ? "   (done)" : ""));
            }
        }

        // Resources: stockpile and change per turn
        Side me = sides["Player"];
        Cost net = NetIncome("Player");
        Rect r = ResourceRect();
        GUI.Box(r, "Your resources (storage " + StorageCap("Player") + ")");
        float ry = r.y + 22;
        foreach (ResourceType res in Res.All)
        {
            GUI.Label(new Rect(r.x + 10, ry, 230, 22), res + ":  " + me.stock.Get(res) + "   (" + Signed(net.Get(res)) + ")");
            ry += 20;
        }
        GUI.Label(new Rect(r.x + 10, ry, 230, 22),
            "Pop: " + me.citizens + " citizens + " + UnitCount("Player") + " warriors / " + PopCap("Player"));
        ry += 20;
        if (me.starving) { GUI.Label(new Rect(r.x + 10, ry, 230, 22), "STARVING: -25% attack", warnStyle); ry += 20; }
        if (me.dehydrated) { GUI.Label(new Rect(r.x + 10, ry, 230, 22), "DEHYDRATED: -1 move", warnStyle); }

        // Town panel: train warriors and upgrade
        if (TownPanelVisible())
        {
            Rect tr = TownRect();
            GUI.Box(tr, "Your town (Level " + me.townLevel + ")   Citizens: " + me.citizens);
            for (int i = 0; i < unitTypes.Length; i++)
            {
                UnitType type = unitTypes[i];
                GUI.enabled = CanTrain("Player", type);
                if (GUI.Button(new Rect(tr.x + 10, tr.y + 25 + i * 35, 360, 30),
                    "Train " + type.name + "  " + type.cost.Text()))
                    TrainUnit("Player", type);
            }

            float by = tr.y + 25 + unitTypes.Length * 35;
            if (me.townLevel < 2)
            {
                GUI.enabled = CanUpgradeTown("Player");
                if (GUI.Button(new Rect(tr.x + 10, by, 360, 30), "Upgrade to Level 2  " + upgradeCost.Text()))
                    UpgradeTown("Player");
            }
            else
            {
                GUI.enabled = true;
                GUI.Label(new Rect(tr.x + 10, by, 360, 30), "Town is fully upgraded");
            }
            GUI.enabled = true;
        }

        // Build panel: every building that fits the selected tile
        List<BuildingType> options = BuildOptions();
        if (options.Count > 0 && !TownPanelVisible())
        {
            Rect br = BuildRect(options.Count);
            GUI.Box(br, me.builtThisTurn ? "Build (already built this turn)" : "Build");
            for (int i = 0; i < options.Count; i++)
            {
                BuildingType bt = options[i];
                GUI.enabled = CanBuild(bt, selectedTile, "Player");
                if (GUI.Button(new Rect(br.x + 10, br.y + 25 + i * 35, 360, 30), bt.name + "  " + bt.cost.Text()))
                    Build(bt, selectedTile, "Player");
            }
            GUI.enabled = true;
        }

        // Last message
        if (message != "")
            GUI.Label(new Rect(10, Screen.height - 55, 700, 25), message);

        if (GUI.Button(EndTurnRect(), "End Turn")) EndTurn();

        GUI.Label(new Rect(10, Screen.height - 30, 1000, 25),
            "WASD = camera   Scroll = zoom   Click warrior to move/attack   Click town to train/upgrade   Click your own tile to build");

        if (gameOver)
        {
            GUI.Box(new Rect(Screen.width / 2f - 250, Screen.height / 2f - 75, 500, 150),
                    resultText + "\n\nStop and press Play to play again.", bigStyle);
        }
    }
}