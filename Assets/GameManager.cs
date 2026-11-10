using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.SceneManagement;
using System.IO;
using System.Globalization;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public partial class GameManager : MonoBehaviour
{
    // Every round has 4 phases, always in this order
    public enum Phase { Upkeep, Building, Move, Fight }

    // ---------- Main menu ----------
    // The menu shows before the game starts. Single Player = you play Blue against the computer.
    // AI vs AI = the computer plays both sides (for research). Static, so Replay / New Map keep the mode.
    public enum GameMode { SinglePlayer, AiVsAi }
    public static bool showMenu = true;
    public static GameMode mode = GameMode.SinglePlayer;

    // Every new press of Unity's Play button starts at the menu again (also with domain reload turned off)
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetMenu()
    {
        showMenu = true; mode = GameMode.SinglePlayer;
        speedIndex = 1; autoNextMatch = false;
        matchesPlayed = 0; blueWins = 0; redWins = 0; draws = 0;
        blueStrategy = AiStrategy.Balanced; redStrategy = AiStrategy.Balanced;
    }

    // ---------- Screen size in GUI units ----------
    // All panels are laid out in "GUI units". On a big or Retina screen one unit is more than one pixel,
    // so the HUD stays readable on a MacBook (2880x1800) as well as on a Windows 1080p monitor.
    static float uiScaleNow = 1f;
    static float SW { get { return Screen.width / uiScaleNow; } }
    static float SH { get { return Screen.height / uiScaleNow; } }

    void UpdateUiScale()
    {
        float s = uiScale > 0.01f ? uiScale : (Screen.height > 1200 ? Screen.height / 1080f : 1f);
        uiScaleNow = Mathf.Clamp(s, 0.5f, 3f);
    }

    // ---------- Game speed (for testing) ----------
    // Speeds up phase timers in Play / AI vs AI and the Upkeep pause. Max runs as many phases per frame as fit.
    static readonly float[] speedSteps = { 0.5f, 1f, 2f, 4f, 8f, 16f, 50f };
    static int speedIndex = 1;                 // 1x. Static: kept by Replay / New Map
    static bool autoNextMatch = false;         // AI vs AI: start a new map by itself when a match ends
    static int matchesPlayed, blueWins, redWins, draws;
    float autoAccum = 0f;                      // seconds of auto play not yet turned into phases
    float gameOverTime = -1f;

    float GameSpeed { get { return speedSteps[Mathf.Clamp(speedIndex, 0, speedSteps.Length - 1)]; } }
    string SpeedLabel(int i) { return speedSteps[i] >= 50f ? "Max" : speedSteps[i] + "x"; }

    [Header("Main menu")]
    public string gameTitle = "Fluffy War";
    public bool logMatches = true;          // adds one row per finished match to ResearchExports/Match_Results.csv

    bool menuPickMode = false;              // true after pressing Play: shows Single Player / AI vs AI
    string menuMessage = "";
    Texture2D menuBackground;
    GUIStyle titleStyle, menuButtonStyle, menuTextStyle;
    StrategyCamera strategyCamera;

    [Header("Rounds and phases")]
    public float upkeepSeconds = 1.5f;      // how long the Upkeep phase stays on screen before Building starts
    public float autoPhaseSeconds = 1f;     // Play mode: seconds per phase (smaller = faster)
    public int maxRounds = 100;             // the match ends in a draw after this many rounds

    // Tier 1 warriors are trained at the capital or a Barracks. Tier 2 warriors only come from promotion at a Transmutation Lab.
    public UnitType[] unitTypes = DefaultUnitTypes();

    // The real unit list with costs. Also used to repair an old list saved in the scene (see FixUnitTypes).
    static UnitType[] DefaultUnitTypes()
    {
        return new UnitType[]
    {
        // ----- Tier 1 -----
        new UnitType { name = "Squire",  letter = "Sq", tier = 1, maxHP = 20, attack = 5, defense = 4,
                       moves = 2, range = 1, size = new Vector3(0.32f, 0.32f, 0.32f),
                       cost = new Cost { wheat = 6, wood = 2, mineral = 6 },
                       promotesTo = "Knight", promoteCost = new Cost { mineral = 8, mana = 6 } },
        new UnitType { name = "Hunter",  letter = "Hu", tier = 1, maxHP = 12, attack = 4, defense = 1,
                       moves = 2, range = 2, size = new Vector3(0.26f, 0.30f, 0.26f),
                       cost = new Cost { wheat = 5, wood = 6 },
                       promotesTo = "Archer", promoteCost = new Cost { wood = 6, mana = 5 } },
        new UnitType { name = "Scout",   letter = "Sc", tier = 1, maxHP = 10, attack = 2, defense = 1,
                       moves = 3, range = 1, size = new Vector3(0.22f, 0.22f, 0.22f),
                       cost = new Cost { wheat = 4, wood = 2 },
                       promotesTo = "Ranger", promoteCost = new Cost { wood = 4, mana = 5 }, forestWalker = true },
        new UnitType { name = "Rider",   letter = "Ri", tier = 1, maxHP = 16, attack = 5, defense = 2,
                       moves = 3, range = 1, size = new Vector3(0.38f, 0.26f, 0.38f),
                       cost = new Cost { wheat = 6, meat = 4, wood = 4 },
                       promotesTo = "Cavalry", promoteCost = new Cost { meat = 6, mana = 6 }, plainsCharge = true },
        // ----- Tier 2 -----
        new UnitType { name = "Knight",  letter = "K", tier = 2, maxHP = 32, attack = 9, defense = 7,
                       moves = 2, range = 1, size = new Vector3(0.40f, 0.40f, 0.40f), heavyArmor = true },
        new UnitType { name = "Archer",  letter = "A", tier = 2, maxHP = 16, attack = 7, defense = 2,
                       moves = 2, range = 2, size = new Vector3(0.30f, 0.35f, 0.30f) },
        new UnitType { name = "Ranger",  letter = "R", tier = 2, maxHP = 16, attack = 5, defense = 3,
                       moves = 4, range = 1, size = new Vector3(0.28f, 0.30f, 0.28f),
                       forestWalker = true, forestBonus = 2 },
        new UnitType { name = "Cavalry", letter = "C", tier = 2, maxHP = 24, attack = 8, defense = 4,
                       moves = 4, range = 1, size = new Vector3(0.45f, 0.30f, 0.45f), plainsCharge = true },
    };
    }

    // SampleScene.unity still holds an old 4-unit list (wheatCost/woodCost/mineralCost) from before the
    // Cost class existed. Unity loads it with every cost = 0, so warriors were free and Knights trainable.
    // If the list looks like that, use the real list from DefaultUnitTypes instead.
    void FixUnitTypes()
    {
        bool stale = unitTypes == null || unitTypes.Length == 0 || !unitTypes.Any(t => t != null && t.tier == 2);
        if (unitTypes != null)
            foreach (UnitType t in unitTypes)
                if (t == null || t.cost == null || (t.tier == 1 && t.cost.Text() == "(free)")) stale = true;
        if (!stale) return;
        Debug.LogWarning("GameManager: the unit list saved in the scene is old (no costs). Using the built-in list. " +
                         "To clear it for good, right-click the GameManager component and choose Reset.");
        unitTypes = DefaultUnitTypes();
    }

    [Header("Towns and combat")]
    public int townMaxHP = 40;
    public int townDefense = 4;
    public int woodsCoverBonus = 2;     // extra defense against ranged attacks in Woods, Taiga and Magic Forest
    [Range(0f, 1f)] public float plainsChargeBonus = 0.5f;  // Rider/Cavalry attack bonus from Plains or Grassland

    [Header("Start")]
    public Cost startStock = new Cost { wheat = 40, meat = 15, wood = 40, mineral = 25, water = 30, mana = 0 };
    public int startCitizens = 3;

    [Header("Town Center")]
    public Cost townLevel1Output = new Cost { wheat = 2, water = 2 };
    public Cost townLevel2Output = new Cost { wheat = 4, water = 4, mana = 1 };
    public Cost upgradeCost = new Cost { wood = 30, mineral = 25, mana = 5 };
    public int upgradedTownRadius = 3;
    public int upgradedTownHP = 70;

    // Small steady income from the capital itself, so a new game can always afford its first buildings.
    // (New field names on purpose: the old Town Center values saved in the scene stay as they are.)
    [Header("Capital starter output (on top of the Town Center output)")]
    public Cost capitalOutputLevel1 = new Cost { wood = 2, mineral = 1 };
    public Cost capitalOutputLevel2 = new Cost { wood = 3, mineral = 2 };

    [Header("City food (Upkeep)")]
    [Tooltip("Food the city itself eats every Upkeep, on top of citizens and warriors. Level 2 eats more.")]
    public int cityFoodLevel1 = 1;
    public int cityFoodLevel2 = 2;
    [Tooltip("On: food is paid with Wheat first, then Meat. You only starve when Wheat AND Meat run out.")]
    public bool meatCountsAsFood = true;

    [Header("Warriors gather resources")]
    [Tooltip("A warrior standing on a resource tile when Upkeep starts collects this much of that resource.")]
    public int unitGatherAmount = 2;
    [Tooltip("On: warriors can also gather on neutral or enemy land (raiding). Off: only inside your territory.")]
    public bool gatherOutsideTerritory = true;

    [Header("Screen")]
    [Tooltip("0 = automatic (bigger on Retina Macs and 1440p/4K screens). Or set a fixed size, e.g. 1.5.")]
    [Range(0f, 3f)] public float uiScale = 0f;

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
    int turn = 1;                // the round number

    Phase phase = Phase.Upkeep;
    float phaseTimer = 0f;       // seconds since this phase began
    float bannerStart = -10f;    // when the big "Round X / Phase" text appeared
    bool autoPlay = false;       // true after pressing Play: the computer plays both sides

    BuildingType[] buildingTypes;
    Dictionary<string, Side> sides = new Dictionary<string, Side>();
    Dictionary<HexTile, Building> buildings = new Dictionary<HexTile, Building>();
    HashSet<HexTile> trainedHere = new HashSet<HexTile>();   // capitals and Barracks that already trained this turn

    List<Unit> units = new List<Unit>();
    Dictionary<GameObject, Unit> unitLookup = new Dictionary<GameObject, Unit>();
    Dictionary<HexTile, int> townHP = new Dictionary<HexTile, int>();
    Dictionary<HexTile, int> townMax = new Dictionary<HexTile, int>();
    List<HexTile> reachable = new List<HexTile>();
    Dictionary<HexTile, int> moveCosts = new Dictionary<HexTile, int>();
    List<HexTile> targets = new List<HexTile>();

    Unit selectedUnit;
    HexTile selectedTile;

    // ---------- Build spot highlights ----------
    // In your Building phase, tiles where you can build are tinted:
    // green = you can build there right now, yellow = it fits, but you can't pay or already built this turn.
    [Header("Build Spot Highlights")]
    public bool showBuildSpots = true;
    public Color buildNowColor = new Color(0.2f, 1f, 0.35f);
    public Color buildLaterColor = new Color(1f, 0.8f, 0.2f);
    [Range(0f, 1f)] public float buildTintStrength = 0.45f;
    public float buildPulseSpeed = 3f;

    List<HexTile> buildSpots = new List<HexTile>();     // tiles where the chosen building fits
    HashSet<HexTile> buildNow = new HashSet<HexTile>(); // ...and you could build it this second
    int buildFocus = -1;                                // -1 = any building, otherwise index into buildingTypes
    string buildSpotsKey = "";

    string message = "";
    bool gameOver = false;
    string resultText = "";

    Rect infoRect = new Rect(10, 10, 270, 175);   // top-left info box (compact)
    GUIStyle letterStyle, bigStyle, warnStyle, roundStyle, phaseOnStyle, phaseOffStyle, announceStyle;

    void Start()
    {
        map = FindAnyObjectByType<HexMap>();
        cam = Camera.main;
        buildingTypes = BuildingCatalog.Create();
        FixUnitTypes();

        // No camera moving behind the menu
        strategyCamera = FindFirstObjectByType<StrategyCamera>();
        if (strategyCamera != null) strategyCamera.enabled = !showMenu;
    }

    void Update()
    {
        UpdateUiScale();
        if (showMenu) return;                // the game waits until a mode is picked in the main menu
        if (map == null || map.tiles == null || map.tiles.Count == 0 || map.tiles[0].obj == null) return;
        map.FadeIconsUnder(new HashSet<HexTile>(units.Select(x => x.tile)));   // warriors show through resource icons
        if (!ready)
        {
            Random.InitState(map.usedSeed);   // same map seed = same enemy choices
            autoPlay = mode == GameMode.AiVsAi;
            if (autoPlay) message = "AI vs AI: the computer plays both sides. Stop = take over Blue.";
            SetupBrains();

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
            BeginPhase(Phase.Upkeep);    // round 1 starts
        }
        if (gameOver) return;

        // Upkeep moves on by itself. In Play mode every phase moves on by itself.
        // The speed buttons make both go faster. At high speed several phases can run in one frame
        // (stopping after ~25 ms so the window stays responsive).
        float dt = Time.unscaledDeltaTime * GameSpeed;
        phaseTimer += dt;
        if (autoPlay)
        {
            autoAccum += dt;
            float step = Mathf.Max(0.01f, autoPhaseSeconds);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (autoPlay && !gameOver && autoAccum >= step && watch.ElapsedMilliseconds < 25)
            {
                autoAccum -= step;
                NextPhase();
            }
            if (autoAccum > step * 4f) autoAccum = step * 4f;   // don't build up a huge backlog
        }
        else if (phase == Phase.Upkeep && phaseTimer >= upkeepSeconds) NextPhase();
        if (gameOver) { UpdateAutoNextMatch(); return; }

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

        // No camera zoom while the mouse wheel is scrolling a panel
        if (strategyCamera != null) strategyCamera.blockZoom = OverUI(mousePos);

        if (click && !OverUI(mousePos)) HandleClick(mousePos);

        UpdateBuildSpots();
        if (buildSpots.Count > 0) ApplyBuildTint();   // keeps the gentle pulse going
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

        // One of each Tier 1 warrior
        List<HexTile> spots = FreeSpotsNear(town, owner, 3);
        int n = 0;
        foreach (UnitType type in unitTypes)
        {
            if (type.tier != 1 || n >= spots.Count) continue;
            CreateUnit(owner, type, spots[n]);
            n++;
        }
    }

    Unit CreateUnit(string owner, UnitType type, HexTile tile)
    {
        GameObject go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        HexMap.UseBaseMaterial(go);
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

    public const int StopHere = 99;     // move cost that uses up all moves (Knights in Rainforest)

    static bool IsForest(TileType t)
    {
        return t == TileType.Woods || t == TileType.Rainforest || t == TileType.Taiga || t == TileType.MagicForest;
    }

    // Move points for this kind of warrior to enter a tile (type can be null = normal cost)
    int MoveCost(UnitType type, HexTile t)
    {
        if (type != null && type.heavyArmor && t.type == TileType.Rainforest) return StopHere;
        if (type != null && type.forestWalker && IsForest(t.type)) return 1;
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
                                       out Dictionary<HexTile, HexTile> cameFrom, UnitType type = null)
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
                int c = cost[cur] + MoveCost(type, n);
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
        if (FindTown(owner) != null)
        {
            Cost starter = sides[owner].townLevel >= 2 ? capitalOutputLevel2 : capitalOutputLevel1;
            foreach (ResourceType r in Res.All) h.Add(r, starter.Get(r));
        }
        Cost g = Gathered(owner);
        foreach (ResourceType r in Res.All) h.Add(r, g.Get(r));
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

    // ---------- Warriors gathering ----------

    // Can this warrior gather where it stands right now?
    bool GathersHere(Unit u)
    {
        HexTile t = u.tile;
        if (t == null || t.resource == ResourceType.None || t.town != "" || unitGatherAmount <= 0) return false;
        return gatherOutsideTerritory || t.owner == u.owner;
    }

    // What this side's warriors collect at the next Upkeep: every warrior standing on a resource tile
    // gathers unitGatherAmount of that tile's resource.
    Cost Gathered(string owner)
    {
        Cost g = new Cost();
        foreach (Unit u in units)
            if (u.owner == owner && GathersHere(u)) g.Add(u.tile.resource, unitGatherAmount);
        return g;
    }

    // "+2 Wood" text that floats above a gathering warrior for a moment after Upkeep
    struct Popup { public Vector3 pos; public string text; public float start; public Color color; }
    List<Popup> popups = new List<Popup>();

    void ShowGatherPopups(string owner)
    {
        foreach (Unit u in units)
        {
            if (u.owner != owner || !GathersHere(u)) continue;
            popups.Add(new Popup {
                pos = u.obj.transform.position + Vector3.up * (u.type.size.y + 0.6f),
                text = "+" + unitGatherAmount + " " + u.tile.resource,
                start = Time.unscaledTime,
                color = HexMap.ResourceColor(u.tile.resource)
            });
        }
    }

    void DrawPopups()
    {
        float life = 1.6f / Mathf.Min(GameSpeed, 4f);
        popups.RemoveAll(p => Time.unscaledTime - p.start > life);
        if (popups.Count == 0) return;
        GUIStyle st = new GUIStyle(GUI.skin.label);
        st.fontStyle = FontStyle.Bold;
        st.fontSize = 13;
        st.alignment = TextAnchor.MiddleCenter;
        foreach (Popup p in popups)
        {
            float age = (Time.unscaledTime - p.start) / life;
            Vector3 sp = cam.WorldToScreenPoint(p.pos + Vector3.up * age * 0.6f);
            if (sp.z <= 0) continue;
            sp /= uiScaleNow;
            st.normal.textColor = new Color(p.color.r, p.color.g, p.color.b, 1f - age);
            GUI.Label(new Rect(sp.x - 50, SH - sp.y - 10, 100, 20), p.text, st);
        }
    }

    // Food the city itself eats each Upkeep
    int CityFood(string owner)
    {
        if (FindTown(owner) == null) return 0;
        return sides[owner].townLevel >= 2 ? cityFoodLevel2 : cityFoodLevel1;
    }

    // Total food needed this Upkeep (paid with Wheat first, then Meat)
    int FoodNeed(string owner)
    {
        return Upkeep(owner).wheat;
    }

    // Phase 1, steps 2 and 3: what the city, citizens, warriors and outposts eat and drink
    Cost Upkeep(string owner)
    {
        Side s = sides[owner];
        Cost u = new Cost();
        u.wheat += CityFood(owner);
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
        Cost gathered = Gathered(owner);
        foreach (ResourceType r in Res.All) BrainFor(owner).totalGathered += gathered.Get(r);
        foreach (ResourceType r in Res.All) s.stock.Add(r, net.Get(r));

        // Food is Wheat first; whatever Wheat can't cover is eaten as Meat
        s.meatEaten = 0;
        if (meatCountsAsFood && s.stock.wheat < 0 && s.stock.meat > 0)
        {
            int take = Mathf.Min(-s.stock.wheat, s.stock.meat);
            s.stock.meat -= take;
            s.stock.wheat += take;
            s.meatEaten = take;
        }

        // Only starving when Wheat AND Meat together can't feed everyone
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

        // Tell the player what their warriors gathered and if Meat was eaten
        if (owner == "Player" && !gameOver && !wheatShort)
        {
            string got = gathered.Text();
            string note = got != "(free)" ? "Warriors gathered " + got.Trim('(', ')') + "." : "";
            if (s.meatEaten > 0) note += (note != "" ? "  " : "") + "Not enough Wheat: the city ate " + s.meatEaten + " Meat.";
            if (note != "") message = note;
        }
        ShowGatherPopups(owner);
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

    // Training at the capital or a Barracks turns one citizen into a Tier 1 warrior.
    // The capital and each Barracks can train 1 warrior per turn.
    // trainAt = a specific capital/Barracks; null = any of this side's that hasn't trained yet.
    bool CanTrain(string owner, UnitType type, HexTile trainAt = null)
    {
        Side s = sides[owner];
        if (type.tier != 1 || s.citizens <= 0 || !CanPay(owner, type.cost)) return false;
        if (trainAt != null) return !trainedHere.Contains(trainAt) && !BarracksBlocked(trainAt);
        return TrainingTiles(owner).Any(t => !trainedHere.Contains(t));
    }

    bool HasBuilding(string owner, string typeName)
    {
        foreach (Building b in buildings.Values)
            if (b.owner == owner && b.type.name == typeName) return true;
        return false;
    }

    List<HexTile> BarracksTiles(string owner)
    {
        return buildings.Values.Where(b => b.owner == owner && b.type.name == "Barracks")
                               .Select(b => b.tile).OrderBy(t => t.q).ThenBy(t => t.r).ToList();
    }

    // Places that can train: the capital first, then every Barracks
    List<HexTile> TrainingTiles(string owner)
    {
        List<HexTile> list = new List<HexTile>();
        HexTile town = FindTown(owner);
        if (town != null) list.Add(town);
        list.AddRange(BarracksTiles(owner));
        return list;
    }

    bool TrainUnit(string owner, UnitType type, HexTile trainAt = null)
    {
        if (!CanTrain(owner, type, trainAt)) return false;

        // Use the given capital or Barracks, or the first one that hasn't trained yet and has free space around it
        List<HexTile> spots = new List<HexTile>();
        HexTile usedTile = null;
        List<HexTile> options = trainAt != null ? new List<HexTile> { trainAt }
                                                : TrainingTiles(owner).Where(t => !trainedHere.Contains(t)).ToList();
        foreach (HexTile b in options)
        {
            spots = TrainSpots(b, owner);
            if (spots.Count > 0) { usedTile = b; break; }
        }
        if (options.Count == 0)
        {
            if (owner == "Player") message = "You have no capital or Barracks to train at.";
            return false;
        }
        if (spots.Count == 0)
        {
            if (owner == "Player")
                message = trainAt == null ? "No free place to train right now."
                        : trainAt.town != "" ? "No free tile next to your capital."
                        : "Move the unit off the Barracks first.";
            return false;
        }

        Pay(owner, type.cost);
        sides[owner].citizens--;
        Unit u = CreateUnit(owner, type, spots[0]);
        u.movesLeft = 0;       // new warriors can act next turn
        u.hasAttacked = true;
        trainedHere.Add(usedTile);
        sides[owner].trainedThisTurn = true;   // "trained at least once this turn" (kept for info only)

        string where = usedTile.town != "" ? " at the capital" : " at a Barracks";
        message = (owner == "Player" ? "You trained a " : "The enemy trained a ") + type.name + where + ".";
        return true;
    }

    // Where a new warrior appears.
    // Barracks: only ON the Barracks tile. If a warrior stands there, the Barracks can't train until it moves off.
    // Capital: the nearest free tile around it (up to 2 steps).
    List<HexTile> TrainSpots(HexTile trainAt, string owner)
    {
        if (trainAt.town != "") return FreeSpotsNear(trainAt, owner, 2);
        return CanEnter(trainAt, owner) ? new List<HexTile> { trainAt } : new List<HexTile>();
    }

    bool BarracksBlocked(HexTile t)
    {
        return t != null && t.town == "" && UnitAt(t) != null;
    }

    // ---------- Promotion (Tier 1 to Tier 2) ----------

    UnitType FindUnitType(string name)
    {
        foreach (UnitType t in unitTypes) if (t.name == name) return t;
        return null;
    }

    // The warrior must stand in its own territory, not have acted this turn, and the side needs a Transmutation Lab
    bool CanPromote(Unit u)
    {
        if (u.type.tier != 1 || FindUnitType(u.type.promotesTo) == null) return false;
        if (u.tile.owner != u.owner || u.hasAttacked || u.movesLeft < MovesFor(u)) return false;
        return HasBuilding(u.owner, "Transmutation Lab") && CanPay(u.owner, u.type.promoteCost);
    }

    bool Promote(Unit u)
    {
        if (!CanPromote(u)) return false;
        UnitType next = FindUnitType(u.type.promotesTo);
        Pay(u.owner, u.type.promoteCost);

        string oldName = u.type.name;
        u.hp = Mathf.Min(next.maxHP, u.hp + (next.maxHP - u.type.maxHP));  // keeps its damage
        u.type = next;
        u.obj.name = u.owner + " " + next.name;
        u.obj.transform.localScale = next.size;
        PlaceUnit(u, u.tile);
        u.movesLeft = 0;            // promotion uses the warrior's turn
        u.hasAttacked = true;

        message = (u.owner == "Player" ? "Your " : "Enemy ") + oldName + " became a " + next.name + "!";
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
        HexMap.UseBaseMaterial(go);
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
        if (sides[owner].townLevel < type.needsTownLevel) return false;
        if (type.onePerSide && HasBuilding(owner, type.name)) return false;

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

    // Attack on the current tile. Plains charge: Rider/Cavalry hit harder from Plains or Grassland.
    // Ranger forest bonus. Starving armies hit 25% weaker.
    int AttackOf(Unit u)
    {
        float a = u.type.attack;
        TileType t = u.tile.type;
        if (u.type.plainsCharge && (t == TileType.Plains || t == TileType.Grassland)) a *= 1f + plainsChargeBonus;
        if (IsForest(t)) a += u.type.forestBonus;
        if (sides[u.owner].starving) a *= 1f - starvationAttackPenalty;
        return Mathf.Max(1, Mathf.RoundToInt(a));
    }

    // Short list of the terrain effects on this warrior right now, for the info box
    string TerrainNote(Unit u)
    {
        TileType t = u.tile.type;
        List<string> notes = new List<string>();
        if (u.type.plainsCharge && (t == TileType.Plains || t == TileType.Grassland)) notes.Add("Charge +" + Mathf.RoundToInt(plainsChargeBonus * 100) + "% ATK");
        if (u.type.forestBonus > 0 && IsForest(t)) notes.Add("Forest +" + u.type.forestBonus + " ATK/DEF");
        if (t == TileType.Woods || t == TileType.Taiga || t == TileType.MagicForest) notes.Add("Cover vs ranged");
        if (u.type.range >= 2 && (t == TileType.Hills || t == TileType.Plateau)) notes.Add("High ground +1 range");
        if (t == TileType.MountainPass) notes.Add("Pass +25% DEF");
        if (t == TileType.Desert) notes.Add("Desert -15% DEF");
        if (t == TileType.Coast) notes.Add("Coast -10% DEF");
        return string.Join(", ", notes.ToArray());
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
        if (rangedAttack && (t == TileType.Woods || t == TileType.Taiga || t == TileType.MagicForest)) d += woodsCoverBonus;
        if (IsForest(t)) d += u.type.forestBonus;     // Ranger

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

    // Note: "defense / 2" is integer division, so DEF 2 and DEF 3 both take off 1.
    // Left as it was on purpose - changing it shifts the whole balance and only moves
    // which pair of defence values collide. Fix it together with a rebalance, not alone.
    int Damage(int attack, int defense)
    {
        return Mathf.Max(1, attack - defense / 2);
    }

    // A melee clash is simultaneous: both blows are worked out from the state before anyone is
    // hurt, then applied together. A defender that dies still lands its hit, so striking first is
    // no longer a free kill. Ranged attacks from 2 or more tiles away still draw no reply.
    void AttackUnit(Unit a, Unit d)
    {
        int distance = Dist(a.tile, d.tile);

        int dmg = Damage(AttackOf(a), DefenseOf(d, a));
        int back = distance == 1 ? Damage(AttackOf(d), DefenseOf(a, d)) : 0;

        string aName = UnitName(a), dName = UnitName(d);

        d.hp -= dmg;
        a.hp -= back;

        a.hasAttacked = true;
        a.movesLeft = 0;

        string log = aName + " hit " + dName + " for " + dmg;
        if (back > 0) log += ", took " + back + " back";

        bool defenderDied = d.hp <= 0;
        bool attackerDied = a.hp <= 0;
        if (defenderDied) Kill(d);
        if (attackerDied) Kill(a);

        if (defenderDied && attackerDied) log += ". Both were destroyed!";
        else if (defenderDied) log += ". " + dName + " destroyed!";
        else if (attackerDied) log += ". " + aName + " destroyed!";

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
                                        : "Your capital was captured. You lose!",
                    a.owner, "captured_capital");
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

    // Destroying every enemy warrior no longer wins the game: a side with no warriors can still train more.
    // The match ends only when a capital is captured (AttackTown) or the round limit is reached (draw).
    // Kept as an empty hook so a new end rule can be added here later.
    void CheckGameOver()
    {
    }

    // winner = "Player", "Enemy" or "" for a draw. reason goes into the research CSV.
    void EndGame(string text, string winner, string reason)
    {
        if (gameOver) return;
        gameOver = true;
        resultText = text;
        if (mode == GameMode.AiVsAi)
        {
            string how = reason == "captured_capital" ? "captured the enemy capital"
                       : "round " + maxRounds + " reached";
            resultText = winner == "" ? "Draw: " + how + "." : SideLabel(winner) + " wins: " + how + ".";
        }
        LogMatch(winner, reason);
        matchesPlayed++;
        if (winner == "Player") blueWins++; else if (winner == "Enemy") redWins++; else draws++;
        gameOverTime = Time.unscaledTime;
        selectedUnit = null;
        reachable.Clear();
        targets.Clear();
        RefreshHighlights();
    }

    // ---------- Attack confirmation ----------

    // Clicking an enemy only arms an attack. Nothing happens until the Attack button is pressed,
    // so simply standing next to someone can never cost you a warrior by accident.
    Unit pendingAttacker;
    HexTile pendingTarget;

    bool ConfirmVisible()
    {
        return pendingAttacker != null && pendingTarget != null
               && units.Contains(pendingAttacker) && PlayerCanAct(Phase.Fight);
    }

    Rect ConfirmRect()
    {
        return new Rect(SW / 2f - 175, SH - 265, 350, 145);
    }

    void CancelPending()
    {
        pendingAttacker = null;
        pendingTarget = null;
    }

    void ConfirmPending()
    {
        Unit a = pendingAttacker;
        HexTile t = pendingTarget;
        CancelPending();
        if (a == null || t == null || !units.Contains(a)) return;

        Attack(a, t);
        selectedTile = t;
        if (!gameOver && units.Contains(a)) SelectUnit(a);
        else { selectedUnit = null; reachable.Clear(); targets.Clear(); RefreshHighlights(); }
    }

    // Exactly the numbers AttackUnit will use, worked out without changing anything
    void PreviewAttack(Unit a, HexTile target, out int dmg, out int back)
    {
        Unit d = UnitAt(target);
        if (d != null)
        {
            dmg = Damage(AttackOf(a), DefenseOf(d, a));
            back = Dist(a.tile, target) == 1 ? Damage(AttackOf(d), DefenseOf(a, d)) : 0;
        }
        else
        {
            dmg = Damage(AttackOf(a), townDefense);
            back = 0;
        }
    }

    void DrawAttackConfirm()
    {
        Rect cr = ConfirmRect();
        Unit a = pendingAttacker;
        Unit d = UnitAt(pendingTarget);

        int dmg, back;
        PreviewAttack(a, pendingTarget, out dmg, out back);

        GUI.Box(cr, "");

        GUIStyle head = new GUIStyle(GUI.skin.label);
        head.fontStyle = FontStyle.Bold;
        head.fontSize = 15;
        head.normal.textColor = new Color(1f, 0.85f, 0.3f);

        float cy = cr.y + 8f;
        GUI.Label(new Rect(cr.x + 12, cy, cr.width - 24, 22),
                  d != null ? "Attack " + UnitName(d) + "?" : "Attack " + pendingTarget.town + "?", head);
        cy += 25f;

        if (d != null)
        {
            GUI.Label(new Rect(cr.x + 12, cy, cr.width - 24, 20),
                      "You deal " + dmg + "   -   " + UnitName(d) + " " + d.hp + " to " + Mathf.Max(0, d.hp - dmg));
            cy += 20f;
            GUI.Label(new Rect(cr.x + 12, cy, cr.width - 24, 20), back > 0
                      ? "You take " + back + " back   -   you " + a.hp + " to " + Mathf.Max(0, a.hp - back)
                      : "No reply: you are shooting from range");
            cy += 20f;

            string warn = "";
            bool youDie = a.hp - back <= 0;
            bool theyDie = d.hp - dmg <= 0;
            if (youDie && theyDie) warn = "Both warriors die";
            else if (youDie) warn = "Your warrior dies";
            else if (theyDie) warn = UnitName(d) + " dies";

            if (warn != "")
            {
                GUIStyle ws = new GUIStyle(GUI.skin.label);
                ws.fontStyle = FontStyle.Bold;
                ws.normal.textColor = youDie ? new Color(1f, 0.45f, 0.40f) : new Color(0.45f, 1f, 0.55f);
                GUI.Label(new Rect(cr.x + 12, cy, cr.width - 24, 20), warn, ws);
            }
        }
        else
        {
            GUI.Label(new Rect(cr.x + 12, cy, cr.width - 24, 20), "You deal " + dmg + " to the town");
        }

        if (GUI.Button(new Rect(cr.x + 12, cr.yMax - 36, 158, 28), "Attack")) ConfirmPending();
        if (GUI.Button(new Rect(cr.x + 180, cr.yMax - 36, 158, 28), "Cancel")) CancelPending();
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

        // Any click on the map drops an attack that was waiting for an answer
        CancelPending();

        // Placement: a building is chosen in the "Build spots" bar and you click a green tile
        if (PlayerCanAct(Phase.Building) && buildFocus >= 0 && clickedUnit == null && buildNow.Contains(tile))
        {
            selectedTile = tile;
            Build(buildingTypes[buildFocus], tile, "Player");
            buildFocus = -1;          // back to showing every spot
            RefreshHighlights();
            return;
        }

        // Attack a yellow target: clicking it only asks the question, it does not swing
        if (selectedUnit != null && targets.Contains(tile))
        {
            pendingAttacker = selectedUnit;
            pendingTarget = tile;
            selectedTile = tile;
            RefreshHighlights();
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
        moveCosts = CostsFrom(u.tile, u.owner, false, out cameFrom, u.type);
        bool fullMoves = u.movesLeft > 0 && u.movesLeft == MovesFor(u);

        foreach (KeyValuePair<HexTile, int> kv in moveCosts)
        {
            HexTile t = kv.Key;
            if (t == u.tile || !CanEnter(t, u.owner)) continue;
            bool affordable = kv.Value <= u.movesLeft;
            // Can always step one tile, but only straight across an edge that is not a cliff
            // (Dist == 1 alone let a warrior jump from lowland straight up onto a Plateau)
            bool oneStep = fullMoves && Dist(u.tile, t) == 1 && Passable(u.tile, t, u.owner, false);
            if (affordable || oneStep) reachable.Add(t);
        }

        targets = FindTargets(u);

        // Only show what this phase allows
        if (!PlayerCanAct(Phase.Move)) reachable.Clear();
        if (!PlayerCanAct(Phase.Fight)) targets.Clear();
        RefreshHighlights();
    }

    void RefreshHighlights()
    {
        foreach (HexTile t in map.tiles) map.SetHighlight(t, false);
        if (selectedTile != null) map.SetHighlight(selectedTile, true);
        ApplyBuildTint();
        foreach (HexTile t in reachable) map.SetHighlight(t, true);

        foreach (Unit u in units) SetUnitColor(u, targets.Contains(u.tile));
        foreach (HexTile t in targets)
            if (t.town != "") map.SetHighlight(t, true);
    }

    // ---------- Build spots ----------

    // Works out which tiles to tint. Uses the same FitsTile / CanBuild rules as the Build panel,
    // and only recalculates when something that changes the answer has changed.
    void UpdateBuildSpots()
    {
        string key = "";
        if (showBuildSpots && PlayerCanAct(Phase.Building))
        {
            Side me = sides["Player"];
            key = buildFocus + "|" + buildings.Count + "|" + me.builtThisTurn + "|" + me.townLevel + "|" + me.stock.Text();
        }
        if (key == buildSpotsKey) return;
        buildSpotsKey = key;

        buildSpots.Clear();
        buildNow.Clear();
        if (key != "")
        {
            foreach (HexTile t in map.tiles)
            {
                bool fits = false, now = false;
                for (int i = 0; i < buildingTypes.Length; i++)
                {
                    if (buildFocus >= 0 && i != buildFocus) continue;
                    if (!FitsTile(buildingTypes[i], t, "Player")) continue;
                    fits = true;
                    if (CanBuild(buildingTypes[i], t, "Player")) { now = true; break; }
                }
                if (fits) buildSpots.Add(t);
                if (now) buildNow.Add(t);
            }
        }
        RefreshHighlights();
    }

    // Paints the build spots (with a soft pulse). The selected tile stays brighter on top.
    void ApplyBuildTint()
    {
        if (!PlayerCanAct(Phase.Building)) return;
        float pulse = 0.75f + 0.25f * Mathf.Sin(Time.time * buildPulseSpeed);
        foreach (HexTile t in buildSpots)
        {
            Color tint = buildNow.Contains(t) ? buildNowColor : buildLaterColor;
            map.SetBuildTint(t, tint, buildTintStrength * pulse, t == selectedTile);
        }
    }

    // Steps through "any building" and each building type
    void CycleBuildFocus(int dir)
    {
        int n = buildingTypes.Length + 1;
        int i = (buildFocus + 1 + dir + n) % n;
        buildFocus = i - 1;
    }

    // Wide enough for the longest name + cost ("Transmutation Lab (20 Wood, 20 Mineral, 5 Mana)").
    // On a narrow screen it shrinks to fit beside the buttons, and the text wraps onto a second line.
    // When a building is picked, the bar grows to list its build conditions (2 per row).
    Rect BuildSpotsRect()
    {
        float w = Mathf.Max(380, Mathf.Min(620, SW - ControlsRect().width - 30));
        int rows = buildFocus >= 0 && buildingTypes != null
                 ? (BuildConditionCount(buildingTypes[buildFocus]) + 1) / 2 : 0;
        float h = 68 + rows * 17 + (rows > 0 ? 4 : 0);
        return new Rect(10, SH - 122 - h, w, h);
    }

    // How many lines BuildConditions makes (cheap, used for the bar size)
    int BuildConditionCount(BuildingType type)
    {
        return 4 + (type.borderOnly ? 1 : 0) + (type.needsTownLevel > 1 ? 1 : 0) + (type.onePerSide ? 1 : 0);
    }

    // Every rule a building must pass (same rules as FitsTile / CanBuild), and whether you meet it right now.
    // The tile rules are checked one after another on your own tiles, so the first "NO" is the one blocking you.
    List<KeyValuePair<string, bool>> BuildConditions(BuildingType type)
    {
        List<KeyValuePair<string, bool>> list = new List<KeyValuePair<string, bool>>();
        Side me = sides["Player"];

        IEnumerable<HexTile> tiles = map.tiles.Where(t => t.owner == "Player" && t.town == "" && !buildings.ContainsKey(t)
                                                        && map.IsWalkable(t.type));
        list.Add(new KeyValuePair<string, bool>("Empty land tile in your territory", tiles.Any()));

        if (type.needsResource != ResourceType.None)
        {
            tiles = tiles.Where(t => t.resource == type.needsResource);
            list.Add(new KeyValuePair<string, bool>("On a " + type.needsResource + " resource tile", tiles.Any()));
        }
        else
        {
            tiles = tiles.Where(t => t.type != TileType.Coast);
            list.Add(new KeyValuePair<string, bool>("Not on Coast", tiles.Any()));
        }
        if (type.borderOnly)
        {
            tiles = tiles.Where(t => map.Neighbors(t).Any(n => n.owner == ""));
            list.Add(new KeyValuePair<string, bool>("Touching unclaimed land (border)", tiles.Any()));
        }
        if (type.needsTownLevel > 1)
            list.Add(new KeyValuePair<string, bool>("Town Level " + type.needsTownLevel + " (yours: " + me.townLevel + ")",
                                                    me.townLevel >= type.needsTownLevel));
        if (type.onePerSide)
            list.Add(new KeyValuePair<string, bool>("Only 1 allowed (you have " + (HasBuilding("Player", type.name) ? "1" : "0") + ")",
                                                    !HasBuilding("Player", type.name)));
        list.Add(new KeyValuePair<string, bool>("Nothing built yet this turn", !me.builtThisTurn));
        list.Add(new KeyValuePair<string, bool>("Can pay " + type.cost.Text(), CanPay("Player", type.cost)));
        return list;
    }

    void DrawBuildSpotsBar()
    {
        if (!showBuildSpots || !PlayerCanAct(Phase.Building)) return;
        Rect r = BuildSpotsRect();
        GUI.Box(r, "");
        if (GUI.Button(new Rect(r.x + 5, r.y + 6, 30, 24), "<")) CycleBuildFocus(-1);
        if (GUI.Button(new Rect(r.xMax - 35, r.y + 6, 30, 24), ">")) CycleBuildFocus(1);

        string label = buildFocus < 0
            ? "Build spots: any building"
            : "Build spots: " + buildingTypes[buildFocus].name + "  " + buildingTypes[buildFocus].cost.Text();
        GUIStyle wrap = new GUIStyle(GUI.skin.label);
        wrap.wordWrap = true;
        wrap.fontSize = 12;
        wrap.alignment = TextAnchor.MiddleCenter;
        GUI.Label(new Rect(r.x + 40, r.y + 2, r.width - 80, 30), label, wrap);

        string hint = buildSpots.Count == 0 ? "No spot fits right now"
                    : buildFocus >= 0 ? "Click a green tile to build it. Yellow = can't pay / built this turn"
                    : "Green = build now, yellow = can't pay / built this turn";
        if (buildFocus < 0) hint += ".  Press < > to pick a building and see what it needs.";
        GUIStyle hintStyle = new GUIStyle(wrap);
        hintStyle.alignment = TextAnchor.MiddleLeft;
        hintStyle.richText = true;

        // Conditions for the picked building: OK (green) = met, NO (red) = this is what stops you
        float cy = r.y + 32;
        if (buildFocus >= 0)
        {
            List<KeyValuePair<string, bool>> conds = BuildConditions(buildingTypes[buildFocus]);
            float colW = (r.width - 20) / 2f;
            for (int i = 0; i < conds.Count; i++)
            {
                string mark = conds[i].Value ? "<color=#66ff66><b>OK</b></color>  " : "<color=#ff6666><b>NO</b></color>  ";
                GUI.Label(new Rect(r.x + 10 + (i % 2) * colW, cy + (i / 2) * 17, colW, 18), mark + conds[i].Key, hintStyle);
            }
            cy += ((conds.Count + 1) / 2) * 17 + 4;
        }
        GUI.Label(new Rect(r.x + 10, cy, r.width - 20, 34), hint, hintStyle);
    }

    // ---------- Turns ----------

    // Names shown on screen
    string PhaseName(Phase p)
    {
        switch (p)
        {
            case Phase.Upkeep: return "Upkeep Phase";
            case Phase.Building: return "Building Phase";
            case Phase.Move: return "Move Phase";
            default: return "Fight Phase";
        }
    }

    // What you can do in each phase
    string PhaseHint(Phase p)
    {
        if (autoPlay) return "Auto play: the computer plays both sides";
        switch (p)
        {
            case Phase.Upkeep: return "Harvest, food and water upkeep, growth";
            case Phase.Building: return "Build, upgrade the town, train and promote";
            case Phase.Move: return "Move your warriors";
            default: return "Attack with your warriors";
        }
    }

    // Can the player click to do things in this phase? (Not while the computer is playing for you)
    bool PlayerCanAct(Phase p)
    {
        return !gameOver && !autoPlay && phase == p;
    }

    // The sides the computer controls right now
    List<string> ComputerSides()
    {
        List<string> list = new List<string>();
        if (autoPlay) list.Add("Player");
        list.Add("Enemy");
        // AI vs AI: who acts first swaps every round, so neither color gets a first-move edge
        if (list.Count == 2 && turn % 2 == 0) list.Reverse();
        return list;
    }

    void ClearSelection()
    {
        selectedUnit = null;
        reachable.Clear();
        targets.Clear();
        CancelPending();
    }

    // Starts a phase. The computer sides act at the start of each phase, then you get your turn in it.
    void BeginPhase(Phase p)
    {
        phase = p;
        phaseTimer = 0f;
        bannerStart = Time.time;
        ClearSelection();

        if (p == Phase.Upkeep)
        {
            // Phase 1: harvest, upkeep, growth, deficits. Then everyone gets fresh moves.
            RunUpkeep("Player");
            RunUpkeep("Enemy");
            foreach (Side s in sides.Values)
            {
                s.trainedThisTurn = false;
                s.builtThisTurn = false;
            }
            trainedHere.Clear();
            foreach (Unit u in units)
            {
                u.movesLeft = MovesFor(u);
                u.hasAttacked = false;
            }
        }
        else
        {
            foreach (string owner in ComputerSides())
            {
                if (gameOver) break;
                if (p == Phase.Building) AiBuildingPhase(owner);
                else if (p == Phase.Move) AiMovePhase(owner);
                else AiFightPhase(owner);
            }
        }
        RefreshHighlights();
    }

    // The "Next Phase" button, and Play mode, call this
    void NextPhase()
    {
        if (gameOver) return;
        if (phase == Phase.Fight)
        {
            if (turn >= maxRounds)
            {
                EndGame("Round " + maxRounds + " reached. The match is a draw.", "", "max_rounds");
                return;
            }
            turn++;
            BeginPhase(Phase.Upkeep);
        }
        else
        {
            BeginPhase(phase + 1);
        }
    }

    // ---------- Play, Stop, Replay ----------

    void PressPlay()
    {
        if (gameOver) return;
        autoPlay = true;
        autoPlayUsedForBlue = true;
        autoAccum = 0f;
        phaseTimer = 0f;
        ClearSelection();
        RefreshHighlights();
        message = "Auto play on. Press Stop to take control again.";
    }

    void PressStop()
    {
        autoPlay = false;
        phaseTimer = 0f;
        message = "Paused. You control your side again.";
    }

    // Loads the scene again with the same map seed, so you get the same map and the same enemy choices.
    void PressReplay()
    {
        HexMap.replaySeed = map != null ? map.seed : -1;
        ReloadScene();
    }

    // Loads the scene again with a brand new random map (if Randomize Every Run is on in HexMap).
    void PressNewMap()
    {
        HexMap.replaySeed = -1;
        ReloadScene();
    }

    // Back to the main menu. The next game gets a new random map.
    void PressMenu()
    {
        showMenu = true;
        HexMap.replaySeed = -1;
        ReloadScene();
    }

    void ReloadScene()
    {
        Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
        // Works even if the scene isn't in the Build Profiles list
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(scene.buildIndex);
#endif
    }

    // ---------- Screen ----------

    // Bottom right: Play / Stop / Replay / New Map / Menu and Next Phase.
    // Wide enough that every button shows its whole word (about 86 px per button).
    Rect ControlsRect()
    {
        float w = Mathf.Min(470, SW - 20);
        return new Rect(SW - w - 10, SH - 115, w, 105);
    }

    // Top middle: round number and the 4 phases
    Rect BannerRect()
    {
        return new Rect(SW / 2f - 200, 10, 400, 82);
    }

    // Top right: a compact resource box
    Rect ResourceRect()
    {
        return new Rect(SW - 210, 10, 200, 186);
    }

    bool TownPanelVisible()
    {
        return PlayerCanAct(Phase.Building) && selectedTile != null && selectedTile.town != "" && selectedTile.owner == "Player";
    }

    // Height of everything inside the town panel (Upgrade button, Train label, Train buttons)
    float TownContentHeight()
    {
        return 60 + Tier1Types().Count * 35;
    }

    // Left-side panels (town, Barracks, Build) stop above the Build spots bar (or the help box).
    // If their buttons don't fit, they scroll with the mouse wheel or the scrollbar.
    Rect PanelRect(float contentHeight)
    {
        float bottom = showBuildSpots ? BuildSpotsRect().y : SH - 115;
        float top = infoRect.yMax + 10;
        float maxH = Mathf.Max(90, bottom - 10 - top);
        return new Rect(10, top, 380, Mathf.Min(24 + contentHeight + 6, maxH));
    }

    // Starts the scroll area under a panel's title. Returns the button width (narrower when a scrollbar shows).
    // Draw the buttons from x = 6, y = 0, then call GUI.EndScrollView().
    float BeginPanelScroll(Rect panel, ref Vector2 scroll, float contentHeight)
    {
        Rect view = new Rect(panel.x + 4, panel.y + 24, panel.width - 8, panel.height - 28);
        bool needsScroll = contentHeight > view.height;
        scroll = GUI.BeginScrollView(view, scroll, new Rect(0, 0, view.width - (needsScroll ? 20 : 0), contentHeight));
        return needsScroll ? 340 : 360;
    }

    Rect TownRect()
    {
        return PanelRect(TownContentHeight());
    }
    Vector2 townScroll = Vector2.zero, barracksScroll = Vector2.zero, buildScroll = Vector2.zero;

    // Each row is a 30 px button with a 5 px gap
    float ButtonsHeight(int count)
    {
        return Mathf.Max(0, count * 35 - 5);
    }

    // Clicking your own Barracks opens the training panel
    bool BarracksPanelVisible()
    {
        Building b;
        return PlayerCanAct(Phase.Building) && selectedTile != null && buildings.TryGetValue(selectedTile, out b)
               && b.owner == "Player" && b.type.name == "Barracks";
    }

    List<UnitType> Tier1Types()
    {
        return unitTypes.Where(t => t.tier == 1).ToList();
    }

    Rect BarracksRect()
    {
        return PanelRect(ButtonsHeight(Tier1Types().Count));
    }

    // Promote panel for your selected Tier 1 warrior
    bool PromotePanelVisible()
    {
        return PlayerCanAct(Phase.Building) && selectedUnit != null && selectedUnit.owner == "Player"
               && selectedUnit.type.tier == 1 && FindUnitType(selectedUnit.type.promotesTo) != null;
    }

    Rect PromoteRect()
    {
        return new Rect(SW - 260, ResourceRect().yMax + 10, 250, 75);
    }

    // Why the promote button is grey
    string PromoteHint(Unit u)
    {
        if (!HasBuilding("Player", "Transmutation Lab")) return "Needs a Transmutation Lab";
        if (u.tile.owner != "Player") return "Must stand in your territory";
        if (u.hasAttacked || u.movesLeft < MovesFor(u)) return "Must not have moved this turn";
        if (!CanPay("Player", u.type.promoteCost)) return "Not enough resources";
        return "Ready";
    }

    // Buildings that fit the selected tile
    List<BuildingType> BuildOptions()
    {
        List<BuildingType> list = new List<BuildingType>();
        if (!PlayerCanAct(Phase.Building) || selectedTile == null) return list;
        foreach (BuildingType b in buildingTypes)
            if (FitsTile(b, selectedTile, "Player")) list.Add(b);
        return list;
    }

    Rect BuildRect(int count)
    {
        return PanelRect(ButtonsHeight(count));
    }

    bool OverUI(Vector2 mousePos)
    {
        Vector2 p = new Vector2(mousePos.x / uiScaleNow, SH - mousePos.y / uiScaleNow);   // pixels -> GUI units
        if (infoRect.Contains(p) || ControlsRect().Contains(p) || ResourceRect().Contains(p)) return true;
        if (ConfirmVisible() && ConfirmRect().Contains(p)) return true;
        if (BannerRect().Contains(p) || SpeedRect().Contains(p)) return true;
        if (autoPlay && AiStatusRect().Contains(p)) return true;
        if (TownPanelVisible() && TownRect().Contains(p)) return true;
        if (BarracksPanelVisible() && BarracksRect().Contains(p)) return true;
        if (PromotePanelVisible() && PromoteRect().Contains(p)) return true;
        if (showBuildSpots && PlayerCanAct(Phase.Building) && BuildSpotsRect().Contains(p)) return true;
        int n = BuildOptions().Count;
        if (n > 0 && BuildRect(n).Contains(p)) return true;
        return false;
    }

    string OwnerName(string owner)
    {
        if (mode == GameMode.AiVsAi && owner != "") return SideLabel(owner);
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
        UpdateUiScale();
        GUI.matrix = Matrix4x4.Scale(new Vector3(uiScaleNow, uiScaleNow, 1f));
        if (showMenu) { DrawMainMenu(); return; }
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

            roundStyle = new GUIStyle(GUI.skin.label);
            roundStyle.fontSize = 20;
            roundStyle.fontStyle = FontStyle.Bold;
            roundStyle.alignment = TextAnchor.MiddleCenter;
            roundStyle.normal.textColor = Color.white;

            phaseOnStyle = new GUIStyle(GUI.skin.box);
            phaseOnStyle.fontStyle = FontStyle.Bold;
            phaseOnStyle.alignment = TextAnchor.MiddleCenter;
            phaseOnStyle.normal.textColor = Color.black;
            phaseOnStyle.normal.background = MakeTexture(new Color(1f, 0.85f, 0.3f));

            phaseOffStyle = new GUIStyle(GUI.skin.box);
            phaseOffStyle.alignment = TextAnchor.MiddleCenter;
            phaseOffStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);

            announceStyle = new GUIStyle(GUI.skin.label);
            announceStyle.fontSize = 40;
            announceStyle.fontStyle = FontStyle.Bold;
            announceStyle.alignment = TextAnchor.MiddleCenter;
            announceStyle.normal.textColor = Color.white;
        }

        // Letter and HP above each warrior
        foreach (Unit u in units)
        {
            Vector3 p = cam.WorldToScreenPoint(u.obj.transform.position + Vector3.up * (u.type.size.y + 0.25f));
            p /= uiScaleNow;
            if (p.z > 0)
                GUI.Label(new Rect(p.x - 30, SH - p.y - 12, 60, 24),
                          u.type.letter + " " + u.hp, letterStyle);
        }

        // Info box
        GUI.Box(infoRect, "");
        GUIStyle infoStyle = new GUIStyle(GUI.skin.label);
        infoStyle.fontSize = 11;
        GUIStyle infoNoteStyle = new GUIStyle(infoStyle);
        infoNoteStyle.wordWrap = true;       // terrain notes can be long: allow 2 lines
        float y = 12f;
        GUI.Label(new Rect(18, y, 250, 18), "Round " + turn + "   " + PhaseName(phase), infoStyle); y += 16f;

        if (selectedTile == null)
        {
            GUI.Label(new Rect(18, y, 250, 18), "Click a tile, a warrior, or your town", infoStyle);
        }
        else
        {
            GUI.Label(new Rect(18, y, 250, 18),
                HexMap.TypeLabel(selectedTile.type) + "   Owner: " + OwnerName(selectedTile.owner), infoStyle); y += 16f;
            if (selectedTile.resource != ResourceType.None)
            {
                GUI.Label(new Rect(18, y, 250, 18), "Resource tile: " + selectedTile.resource +
                          "  (warrior here: +" + unitGatherAmount + " at Upkeep)", infoStyle); y += 16f;
            }
            if (selectedTile.town != "" && townHP.ContainsKey(selectedTile))
            {
                int level = sides[selectedTile.owner].townLevel;
                GUI.Label(new Rect(18, y, 250, 18), selectedTile.town + " (Level " + level + ")   HP " +
                          townHP[selectedTile] + "/" + townMax[selectedTile], infoStyle); y += 16f;
            }
            Building b;
            if (buildings.TryGetValue(selectedTile, out b))
            {
                string what = b.type.produces != ResourceType.None ? "  (+" + b.type.amount + " " + b.type.produces + ")" : "";
                GUI.Label(new Rect(18, y, 250, 18), "Building: " + b.type.name + what, infoStyle); y += 16f;
            }

            Unit u = UnitAt(selectedTile);
            if (u != null)
            {
                GUI.Label(new Rect(18, y, 250, 18), UnitName(u) + "   HP " + u.hp + "/" + u.type.maxHP, infoStyle); y += 16f;
                GUI.Label(new Rect(18, y, 250, 18),
                    "ATK " + AttackOf(u) + "   DEF " + DefenseOf(u, null) + "   Range " + RangeOf(u), infoStyle); y += 16f;
                GUI.Label(new Rect(18, y, 250, 18),
                    "Moves " + u.movesLeft + "/" + MovesFor(u) + (u.hasAttacked ? "   (done)" : ""), infoStyle); y += 16f;
                string note = TerrainNote(u);
                if (GathersHere(u))
                    note = "Gathering +" + unitGatherAmount + " " + u.tile.resource + " at Upkeep" + (note != "" ? ", " + note : "");
                if (note != "") GUI.Label(new Rect(18, y, 250, 30), note, infoNoteStyle);
            }
        }

        // Resources: stockpile and change per turn
        Side me = sides["Player"];
        Cost net = NetIncome("Player");
        Rect r = ResourceRect();
        GUI.Box(r, "Resources (max " + StorageCap("Player") + ")");
        GUIStyle hudStyle = new GUIStyle(GUI.skin.label);
        hudStyle.fontSize = 11;
        GUIStyle hudWarn = new GUIStyle(warnStyle);
        hudWarn.fontSize = 11;
        float lw = r.width - 16;
        float ry = r.y + 20;
        foreach (ResourceType res in Res.All)
        {
            GUI.Label(new Rect(r.x + 8, ry, lw, 18), res + ":  " + me.stock.Get(res) + "   (" + Signed(net.Get(res)) + ")", hudStyle);
            ry += 16;
        }
        GUI.Label(new Rect(r.x + 8, ry, lw, 18),
            "Food: eats " + FoodNeed("Player") + "/turn" + (meatCountsAsFood ? " (Wheat, then Meat)" : ""), hudStyle);
        ry += 16;
        GUI.Label(new Rect(r.x + 8, ry, lw, 18),
            "Pop: " + me.citizens + " people + " + UnitCount("Player") + " units / " + PopCap("Player"), hudStyle);
        ry += 16;
        if (me.starving) { GUI.Label(new Rect(r.x + 8, ry, lw, 18), "STARVING: -25% attack", hudWarn); ry += 16; }
        if (me.dehydrated) { GUI.Label(new Rect(r.x + 8, ry, lw, 18), "DEHYDRATED: -1 move", hudWarn); }

        // Town panel: upgrade
        if (TownPanelVisible())
        {
            Rect tr = TownRect();
            GUI.Box(tr, "Your town (Level " + me.townLevel + ")   Citizens: " + me.citizens);

            // Scrollable area under the title: mouse wheel or the scrollbar on the right
            float bw = BeginPanelScroll(tr, ref townScroll, TownContentHeight());
            float by = 0;
            if (me.townLevel < 2)
            {
                GUI.enabled = CanUpgradeTown("Player");
                if (GUI.Button(new Rect(6, by, bw, 30), "Upgrade to Level 2  " + upgradeCost.Text()))
                    UpgradeTown("Player");
            }
            else
            {
                GUI.enabled = true;
                GUI.Label(new Rect(6, by, bw, 30), "Town is fully upgraded");
            }
            GUI.enabled = true;

            // Train Tier 1 warriors at the capital (same rules as a Barracks)
            by += 38;
            GUI.Label(new Rect(6, by, bw, 22),
                trainedHere.Contains(selectedTile) ? "Train (capital already trained this turn)" : "Train a warrior (1 per turn here, uses 1 citizen)");
            by += 22;
            List<UnitType> townTrainable = Tier1Types();
            for (int i = 0; i < townTrainable.Count; i++)
            {
                UnitType type = townTrainable[i];
                GUI.enabled = CanTrain("Player", type, selectedTile);
                if (GUI.Button(new Rect(6, by + i * 35, bw, 30),
                    "Train " + type.name + "  " + type.cost.Text()))
                    TrainUnit("Player", type, selectedTile);
            }
            GUI.enabled = true;
            GUI.EndScrollView();
        }

        // Barracks panel: train Tier 1 warriors
        if (BarracksPanelVisible())
        {
            Rect tr = BarracksRect();
            List<UnitType> trainable = Tier1Types();
            string barracksTitle = trainedHere.Contains(selectedTile) ? "Barracks (already trained this turn)"
                                 : BarracksBlocked(selectedTile) ? "Barracks: move the unit off it first"
                                 : "Barracks (1 warrior per turn)";
            GUI.Box(tr, barracksTitle + "   Citizens: " + me.citizens);
            float bw = BeginPanelScroll(tr, ref barracksScroll, ButtonsHeight(trainable.Count));
            for (int i = 0; i < trainable.Count; i++)
            {
                UnitType type = trainable[i];
                GUI.enabled = CanTrain("Player", type, selectedTile);
                if (GUI.Button(new Rect(6, i * 35, bw, 30),
                    "Train " + type.name + "  " + type.cost.Text()))
                    TrainUnit("Player", type, selectedTile);
            }
            GUI.enabled = true;
            GUI.EndScrollView();
        }

        // Promote panel: Tier 1 to Tier 2 with Mana
        if (PromotePanelVisible())
        {
            Unit pu = selectedUnit;
            Rect pr = PromoteRect();
            GUI.Box(pr, "Promote to " + pu.type.promotesTo);
            GUI.enabled = CanPromote(pu);
            if (GUI.Button(new Rect(pr.x + 10, pr.y + 22, 230, 28), "Promote  " + pu.type.promoteCost.Text()))
            {
                if (Promote(pu)) SelectUnit(pu);
            }
            GUI.enabled = true;
            GUI.Label(new Rect(pr.x + 10, pr.y + 51, 230, 22), PromoteHint(pu));
        }

        // Build panel: every building that fits the selected tile
        List<BuildingType> options = BuildOptions();
        if (options.Count > 0 && !TownPanelVisible() && !BarracksPanelVisible())
        {
            Rect br = BuildRect(options.Count);
            GUI.Box(br, me.builtThisTurn ? "Build (already built this turn)" : "Build");
            float bw = BeginPanelScroll(br, ref buildScroll, ButtonsHeight(options.Count));
            for (int i = 0; i < options.Count; i++)
            {
                BuildingType bt = options[i];
                GUI.enabled = CanBuild(bt, selectedTile, "Player");
                if (GUI.Button(new Rect(6, i * 35, bw, 30), bt.name + "  " + bt.cost.Text()))
                    Build(bt, selectedTile, "Player");
            }
            GUI.enabled = true;
            GUI.EndScrollView();
        }

        // Last message
        // Bottom left: last message + how to play, on a dark box so it's easy to read
        float helpW = Mathf.Max(200, Mathf.Min(560, SW - ControlsRect().width - 40));
        Rect help = new Rect(10, SH - 115, helpW, 105);
        GUI.Box(help, "");

        GUIStyle msgStyle = new GUIStyle(GUI.skin.label);
        msgStyle.fontStyle = FontStyle.Bold;
        msgStyle.fontSize = 15;
        msgStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
        GUI.Label(new Rect(help.x + 10, help.y + 6, helpW - 20, 24),
                  message != "" ? message : "Your turn", msgStyle);

        GUIStyle helpStyle = new GUIStyle(GUI.skin.label);
        helpStyle.fontSize = 13;
        helpStyle.normal.textColor = Color.white;
        float hy = help.y + 32;
        GUI.Label(new Rect(help.x + 10, hy, helpW - 20, 20), "<b>Camera:</b>  WASD = move,  Scroll = zoom", helpStyle); hy += 18;
        GUI.Label(new Rect(help.x + 10, hy, helpW - 20, 20), "<b>Building:</b>  click your town to upgrade or train, a Barracks, or your tile", helpStyle); hy += 18;
        GUI.Label(new Rect(help.x + 10, hy, helpW - 20, 20), "<b>Move:</b>  click a warrior, then a lit tile. Stand on a resource tile to gather it", helpStyle); hy += 18;
        GUI.Label(new Rect(help.x + 10, hy, helpW - 20, 20), "<b>Fight:</b>  click a warrior, then a yellow target", helpStyle);

        DrawBuildSpotsBar();
        if (ConfirmVisible()) DrawAttackConfirm();

        DrawPopups();
        DrawRoundBanner();
        DrawSpeedBar();
        DrawControls();
        if (autoPlay) DrawAiStatus();

        if (gameOver)
        {
            GUI.Box(new Rect(SW / 2f - 250, SH / 2f - 85, 500, 170),
                    resultText + "\n\nReplay = same map,  New Map = a new random map,\nMenu = back to the main menu.", bigStyle);
        }
    }

    // Round number, the 4 phases (current one in yellow), and a hint
    void DrawRoundBanner()
    {
        Rect b = BannerRect();
        GUI.Box(b, "");
        GUI.Label(new Rect(b.x, b.y + 2, b.width, 28),
                  "Round " + turn + " / " + maxRounds + (autoPlay ? "   (auto play)" : ""), roundStyle);

        Phase[] all = { Phase.Upkeep, Phase.Building, Phase.Move, Phase.Fight };
        float w = (b.width - 20) / all.Length;
        for (int i = 0; i < all.Length; i++)
        {
            string label = (i + 1) + ". " + PhaseName(all[i]).Replace(" Phase", "");
            GUI.Box(new Rect(b.x + 10 + i * w, b.y + 32, w - 4, 24), label,
                    all[i] == phase ? phaseOnStyle : phaseOffStyle);
        }
        GUI.Label(new Rect(b.x + 10, b.y + 58, b.width - 20, 22), PhaseHint(phase));

        // Big text in the middle of the screen for 2 seconds when a phase starts
        float age = Time.time - bannerStart;
        if (age < 2f && !gameOver)
        {
            Color old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(2f - age));
            string text = phase == Phase.Upkeep ? "Round " + turn + "\n" + PhaseName(phase) : PhaseName(phase);
            GUI.Label(new Rect(0, SH * 0.25f - 60, SW, 120), text, announceStyle);
            GUI.color = old;
        }
    }

    // Play, Stop, Replay, New Map, Menu and Next Phase buttons
    void DrawControls()
    {
        Rect c = ControlsRect();
        GUI.Box(c, "");
        float bw = (c.width - 40) / 5f;

        GUI.enabled = !autoPlay && !gameOver;
        if (GUI.Button(new Rect(c.x + 10, c.y + 10, bw, 35), "Play")) PressPlay();
        GUI.enabled = autoPlay;
        if (GUI.Button(new Rect(c.x + 15 + bw, c.y + 10, bw, 35), "Stop")) PressStop();
        GUI.enabled = true;
        if (GUI.Button(new Rect(c.x + 20 + bw * 2, c.y + 10, bw, 35), "Replay")) PressReplay();
        if (GUI.Button(new Rect(c.x + 25 + bw * 3, c.y + 10, bw, 35), "New Map")) PressNewMap();
        if (GUI.Button(new Rect(c.x + 30 + bw * 4, c.y + 10, bw, 35), "Menu")) PressMenu();

        GUI.enabled = !autoPlay && !gameOver && phase != Phase.Upkeep;
        string next = phase == Phase.Fight ? "End Round" : "Next: " + PhaseName(phase + 1);
        if (GUI.Button(new Rect(c.x + 10, c.y + 52, c.width - 20, 43), next)) NextPhase();
        GUI.enabled = true;
    }

    // ---------- Speed bar (under the round banner) ----------

    bool autoPlayUsedForBlue = false;   // true if the computer played Blue at any point (for the CSV)

    Rect SpeedRect()
    {
        bool aiMode = mode == GameMode.AiVsAi;
        return new Rect(SW / 2f - 200, BannerRect().yMax + 4, 400, aiMode ? 54 : 30);
    }

    // Speed: 0.5x 1x 2x 4x 8x 16x Max. In AI vs AI also: auto next match + the score so far.
    void DrawSpeedBar()
    {
        Rect r = SpeedRect();
        GUI.Box(r, "");
        GUI.Label(new Rect(r.x + 8, r.y + 4, 50, 22), "Speed");
        float bw = (r.width - 66) / speedSteps.Length;
        for (int i = 0; i < speedSteps.Length; i++)
        {
            bool on = i == speedIndex;
            Color old = GUI.backgroundColor;
            if (on) GUI.backgroundColor = new Color(1f, 0.85f, 0.3f);
            if (GUI.Button(new Rect(r.x + 58 + i * bw, r.y + 4, bw - 2, 22), SpeedLabel(i)))
            {
                speedIndex = i;
                autoAccum = 0f;
            }
            GUI.backgroundColor = old;
        }

        if (mode == GameMode.AiVsAi)
        {
            autoNextMatch = GUI.Toggle(new Rect(r.x + 8, r.y + 30, 150, 20), autoNextMatch, " Auto next match");
            GUI.Label(new Rect(r.x + 160, r.y + 30, r.width - 168, 20),
                      "Games " + matchesPlayed + ":  Blue " + blueWins + "  Red " + redWins + "  Draw " + draws);
        }
    }

    // AI vs AI with "Auto next match": a short pause on the result, then a brand new map
    void UpdateAutoNextMatch()
    {
        if (mode != GameMode.AiVsAi || !autoNextMatch || gameOverTime < 0f) return;
        if (Time.unscaledTime - gameOverTime >= 2.5f / Mathf.Min(GameSpeed, 5f)) PressNewMap();
    }

    // ---------- Main menu ----------

    // In AI vs AI both sides are computers, so they are named by color
    string SideLabel(string owner)
    {
        return owner == "Player" ? "Blue AI" : owner == "Enemy" ? "Red AI" : "Nobody";
    }

    void DrawMainMenu()
    {
        if (titleStyle == null)
        {
            menuBackground = MakeTexture(new Color(0.08f, 0.10f, 0.16f, 1f));

            titleStyle = new GUIStyle(GUI.skin.label);
            titleStyle.fontSize = 64;
            titleStyle.fontStyle = FontStyle.Bold;
            titleStyle.alignment = TextAnchor.MiddleCenter;
            titleStyle.normal.textColor = new Color(1f, 0.85f, 0.35f);

            menuButtonStyle = new GUIStyle(GUI.skin.button);
            menuButtonStyle.fontSize = 22;
            menuButtonStyle.fontStyle = FontStyle.Bold;

            menuTextStyle = new GUIStyle(GUI.skin.label);
            menuTextStyle.fontSize = 14;
            menuTextStyle.alignment = TextAnchor.UpperCenter;
            menuTextStyle.wordWrap = true;
            menuTextStyle.normal.textColor = new Color(0.85f, 0.88f, 0.95f);
        }

        // Solid background hides the map until the game starts
        GUI.DrawTexture(new Rect(0, 0, SW, SH), menuBackground);

        float cx = SW / 2f;
        float y = SH * 0.18f;
        GUI.Label(new Rect(0, y, SW, 90), gameTitle, titleStyle);
        y += 90;
        GUI.Label(new Rect(cx - 250, y, 500, 24), "A turn-based hex strategy game", menuTextStyle);
        y += 50;


        if (!menuPickMode)
        {
            if (GUI.Button(MenuRow(ref y), "Play", menuButtonStyle)) { menuPickMode = true; menuMessage = ""; }
            if (GUI.Button(MenuRow(ref y), "Export CSV (research)", menuButtonStyle)) menuMessage = ExportResearchCsv();
            if (GUI.Button(MenuRow(ref y), "Quit", menuButtonStyle)) QuitGame();
        }
        else
        {
            if (GUI.Button(MenuRow(ref y), "Single Player", menuButtonStyle)) StartGame(GameMode.SinglePlayer);
            if (GUI.Button(MenuRow(ref y), "AI vs AI (research)", menuButtonStyle)) StartGame(GameMode.AiVsAi);

            // AI plans: click to cycle Balanced / Aggressive / Economic / Random
            Rect half = new Rect(SW / 2f - 160, y, 156, 36);
            if (GUI.Button(half, "Blue AI: " + blueStrategy)) blueStrategy = NextStrategy(blueStrategy);
            half.x += 164;
            if (GUI.Button(half, "Red AI: " + redStrategy)) redStrategy = NextStrategy(redStrategy);
            y += 46;

            if (GUI.Button(MenuRow(ref y), "Back", menuButtonStyle)) menuPickMode = false;
            GUI.Label(new Rect(cx - 260, y, 520, 80),
                "Single Player: you are Blue, the computer is Red.\n" +
                "AI vs AI: the computer plays both sides and every result is saved to the CSV.\n" +
                "Blue AI is used in AI vs AI (and after you press Play in a game).", menuTextStyle);
            y += 60;
        }

        if (menuMessage != "")
            GUI.Label(new Rect(cx - 300, y + 10, 600, 80), menuMessage, menuTextStyle);
    }

    // The next menu button's place, then moves y down for the one after it
    static Rect MenuRow(ref float y)
    {
        Rect r = new Rect(SW / 2f - 160, y, 320, 50);
        y += 64;
        return r;
    }

    void StartGame(GameMode chosen)
    {
        mode = chosen;
        showMenu = false;
        menuPickMode = false;
        if (strategyCamera != null) strategyCamera.enabled = true;
        // Update() sets the game up on the next frame (the map was already made behind the menu)
    }

    void QuitGame()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // ---------- Research export (CSV) ----------

    // Same folder HexMap writes Map_Generation_Log.csv into: <project>/ResearchExports
    // Editor: <project>/ResearchExports. Built game: the per-user data folder (see HexMap.ResearchFolder).
    static string ResearchFolder()
    {
        return HexMap.ResearchFolder();
    }

    // Copies the research logs into ResearchExports/Export_<date_time>/ and opens that folder.
    // Returns the text to show under the menu buttons.
    string ExportResearchCsv()
    {
        try
        {
            string folder = ResearchFolder();
            string[] logs = { "Map_Generation_Log.csv", "Match_Results.csv" };
            string target = Path.Combine(folder, "Export_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            int copied = 0;
            foreach (string name in logs)
            {
                string src = Path.Combine(folder, name);
                if (!File.Exists(src)) continue;
                Directory.CreateDirectory(target);
                File.Copy(src, Path.Combine(target, name), true);
                copied++;
            }
            if (copied == 0) return "No CSV yet. Play a game first (each map and each finished match is logged).";

#if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(target);
#else
            Application.OpenURL(new System.Uri(target).AbsoluteUri);   // file:///C:/... or file:///Users/... (spaces escaped)
#endif
            Debug.Log("Research CSV exported to " + target);
            return "Exported " + copied + " CSV file(s) to:\n" + target;
        }
        catch (System.Exception e)
        {
            Debug.LogError("CSV export failed: " + e.Message);
            return "Export failed: " + e.Message;
        }
    }

    const string MatchHeader =
        "timestamp,mode,base_seed,map_seed,rounds,winner,reason," +
        "player_citizens,enemy_citizens,player_units,enemy_units,player_buildings,enemy_buildings," +
        "player_town_level,enemy_town_level,player_town_hp,enemy_town_hp," +
        "continent_share,start_gini,start_distance,blue_strategy,red_strategy,player_gathered,enemy_gathered";

    // One row per finished match in ResearchExports/Match_Results.csv
    void LogMatch(string winner, string reason)
    {
        if (!logMatches || map == null) return;
        try
        {
            string folder = ResearchFolder();
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "Match_Results.csv");

            // A log with different columns is kept under a new name, so no data is lost
            if (File.Exists(file))
            {
                string firstLine;
                using (StreamReader rd = new StreamReader(file)) firstLine = rd.ReadLine();
                if (firstLine != MatchHeader)
                    File.Move(file, Path.Combine(folder, "Match_Results_old_" +
                              System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"));
            }
            bool newFile = !File.Exists(file);

            CultureInfo inv = CultureInfo.InvariantCulture;
            int pUnits = 0, eUnits = 0, pBuild = 0, eBuild = 0;
            foreach (Unit u in units) { if (u.owner == "Player") pUnits++; else eUnits++; }
            foreach (Building b in buildings.Values) { if (b.owner == "Player") pBuild++; else if (b.owner == "Enemy") eBuild++; }
            HexTile pTown = FindTown("Player"), eTown = FindTown("Enemy");
            int pHP = pTown != null && townHP.ContainsKey(pTown) ? townHP[pTown] : 0;
            int eHP = eTown != null && townHP.ContainsKey(eTown) ? townHP[eTown] : 0;

            using (StreamWriter w = new StreamWriter(file, true))
            {
                if (newFile) w.WriteLine(MatchHeader);
                w.WriteLine(string.Join(",", new string[] {
                    System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                    mode == GameMode.AiVsAi ? "ai_vs_ai" : "single_player",
                    map.seed.ToString(inv), map.usedSeed.ToString(inv), turn.ToString(inv),
                    winner == "" ? "draw" : winner, reason,
                    sides["Player"].citizens.ToString(inv), sides["Enemy"].citizens.ToString(inv),
                    pUnits.ToString(inv), eUnits.ToString(inv), pBuild.ToString(inv), eBuild.ToString(inv),
                    sides["Player"].townLevel.ToString(inv), sides["Enemy"].townLevel.ToString(inv),
                    pHP.ToString(inv), eHP.ToString(inv),
                    map.continentShare.ToString("F4", inv), map.startGini.ToString("F4", inv),
                    map.startDistance.ToString(inv),
                    autoPlayUsedForBlue ? StrategyFor("Player").ToString() : "human",
                    StrategyFor("Enemy").ToString(),
                    BrainFor("Player").totalGathered.ToString(inv), BrainFor("Enemy").totalGathered.ToString(inv)
                }));
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("Could not write Match_Results.csv: " + e.Message);
        }
    }

    // A 1x1 colored texture for the yellow "current phase" box
    static Texture2D MakeTexture(Color color)
    {
        Texture2D t = new Texture2D(1, 1);
        t.SetPixel(0, 0, color);
        t.Apply();
        return t;
    }
}