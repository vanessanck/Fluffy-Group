using UnityEngine;
using System.Collections.Generic;
using System.IO;
using System.Globalization;

// One entry per row of the biome table (Base Biome / Feature Overlay)
public enum TileType
{
    Ocean,          // deep water, impassable
    Coast,          // Ocean / Shallow
    Grassland,      // Grassland / Open
    Woods,          // Grassland / Woods
    Rainforest,     // Grassland / Rainforest
    Plains,         // Plains / Open
    Hills,          // Plains / Hills
    Desert,         // Desert / Open
    Tundra,         // Tundra / Open
    Taiga,          // Tundra / Taiga
    MagicForest,    // Forest / Magic Forest
    MountainPass,   // Mountain / Pass (ramp)
    Plateau         // Mountain / Plateau (high ground)
}

public enum ResourceType
{
    None, Wheat, Meat, Wood, Mineral, Water, Mana
}

public class HexMap : MonoBehaviour
{
    [Header("Map size (rectangle)")]
    public int columns = 24;
    public int rows = 16;
    public float hexSize = 1f;

    [Header("Noise heatmaps")]
    public int seed = 0;
    public float noiseScale = 0.15f;
    [Range(1, 6)] public int octaves = 3;
    public float elevationContrast = 1.6f;  // higher = more mountains and sea
    public float falloffStrength = 1.5f;    // how strongly the edges sink into the ocean
    public float falloffPower = 4f;         // higher = flatter middle, sharper coast
    public float rareNoiseScale = 0.25f;    // Leyline / Mana layer
    public float rareContrast = 2f;
    public float resNoiseScale = 0.35f;     // Resource vein layer
    public float resContrast = 1f;

    [Header("Elevation slicing")]
    public float waterLevel = 0.25f;
    public float shallowLevel = 0.15f;      // water at least this high and next to land is walkable Coast
    public float hillsLevel = 0.62f;
    public float mountainLevel = 0.70f;

    [Header("Features")]
    public float magicForestLevel = 0.82f;  // H_rare needed for a Magic Forest
    public float resourceThreshold = 0.60f; // H_res needed for a resource tile

    [Header("Mountain pass carving")]
    [Range(0f, 1f)] public float passCarveChance = 0.25f;

    [Header("Random map (new map every Play)")]
    [Tooltip("On: a new random seed every time you press Play or New Map. Off: always use the Seed above.")]
    public bool randomizeEveryRun = true;
    [Tooltip("On: each attempt also nudges the settings above by up to +/- these amounts (rolled from its own seed).")]
    public bool randomizeParameters = true;
    public float noiseScaleVariation = 0.03f;
    public float waterLevelVariation = 0.03f;
    public float mountainLevelVariation = 0.04f;
    public float passCarveChanceVariation = 0.10f;

    [Header("SBPCG validation")]
    public int maxAttempts = 50;
    public float maxGini = 0.15f;
    public int fairnessRadius = 2;
    [Tooltip("The biggest connected piece of walkable land must cover at least this share of ALL tiles.")]
    [Range(0f, 1f)] public float minContinentShare = 0.35f;
    [Tooltip("The two capitals must be at least this many tiles apart.")]
    public int minStartDistance = 8;

    [Header("Towns")]
    public int townRadius = 2;
    public Color playerColor = new Color(0.2f, 0.4f, 1f);
    public Color enemyColor = new Color(1f, 0.25f, 0.2f);

    [Header("Research export")]
    public bool exportCsv = true;

    [Header("Results (filled in when you press Play)")]
    public int usedSeed;
    public int attemptsUsed;
    public int totalTiles;
    public int walkableTiles;
    public int plateauTiles;
    public int passCount;
    public float passDensity;
    public int resourceTiles;
    public int playerStartNodes;
    public int enemyStartNodes;
    public float startGini;
    public int continentTiles;      // size of the biggest connected walkable region
    public float continentShare;    // continentTiles / totalTiles
    public int startDistance;       // tiles between the two capitals

    [Header("Settings used for this map (rolled per attempt)")]
    public float usedNoiseScale;
    public float usedWaterLevel;
    public float usedHillsLevel;
    public float usedMountainLevel;
    public float usedPassCarveChance;

    public List<HexTile> tiles;

    Dictionary<Vector2Int, HexTile> byCoord = new Dictionary<Vector2Int, HexTile>();
    HexTile startPlayer, startEnemy;
    HashSet<HexTile> continent = new HashSet<HexTile>();

    // Seed kept across a scene reload, so Replay gives the same map. -1 = roll a new one.
    public static int replaySeed = -1;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { replaySeed = -1; }

    Dictionary<GameObject, HexTile> lookup = new Dictionary<GameObject, HexTile>();
    Mesh hexMesh;
    Material baseMat;

    static readonly Vector2Int[] directions = {
        new Vector2Int(1, 0), new Vector2Int(1, -1), new Vector2Int(0, -1),
        new Vector2Int(-1, 0), new Vector2Int(-1, 1), new Vector2Int(0, 1)
    };

    void Start()
    {
        GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        baseMat = temp.GetComponent<Renderer>().sharedMaterial;
        Destroy(temp);
        hexMesh = BuildHexMesh();

        // Pick the base seed: Replay reuses the last one, otherwise roll a new one (or keep the Inspector seed)
        if (replaySeed >= 0) seed = replaySeed;
        else if (randomizeEveryRun) seed = new System.Random().Next(1, 2000000);
        replaySeed = -1;

        // SBPCG loop: generate, validate, and resample until a map passes
        bool accepted = false;
        int fallbackAttempt = -1;   // first attempt whose capitals were at least connected
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            int s = AttemptSeed(attempt);
            string result = GenerateAndValidate(s);
            attemptsUsed = attempt + 1;

            ExportAttempt(attempt + 1, s, result);
            if (result == "accepted") { accepted = true; break; }
            if (fallbackAttempt < 0 && (result == "rejected_unfair_gini" || result == "rejected_small_continent"))
                fallbackAttempt = attempt;
        }

        if (!accepted)
        {
            int use = fallbackAttempt >= 0 ? fallbackAttempt : 0;
            Debug.LogWarning("No map passed validation in " + maxAttempts + " attempts. Using attempt " + (use + 1) + " anyway.");
            string result = GenerateAndValidate(AttemptSeed(use));
            if (result == "rejected_no_land") { Debug.LogError("No land for the towns. Try another seed."); return; }
        }

        BuildTiles();
        PlaceTown(startPlayer, "Player", "Your Capital");
        PlaceTown(startEnemy, "Enemy", "Enemy Capital");

        Debug.Log("Map accepted: seed " + usedSeed + ", attempts " + attemptsUsed +
                  ", tiles " + totalTiles + ", walkable " + walkableTiles +
                  ", plateau " + plateauTiles + ", mountain passes " + passCount +
                  ", resource tiles " + resourceTiles +
                  ", start nodes " + playerStartNodes + " vs " + enemyStartNodes +
                  ", start Gini " + startGini.ToString("F3") +
                  ", continent " + continentTiles + " tiles (" + (continentShare * 100f).ToString("F0") + "%)" +
                  ", capitals " + startDistance + " apart" +
                  (exportCsv ? "   CSV saved in: " + ExportFolder() : ""));
    }

    // Each attempt gets its own seed. Base seeds never share attempt seeds (as long as maxAttempts < 1000).
    int AttemptSeed(int attempt)
    {
        return seed * 1000 + attempt;
    }

    // Runs stages 2-5 for one seed and returns "accepted" or the reason it was rejected
    string GenerateAndValidate(int s)
    {
        usedSeed = s;
        startGini = -1f;
        playerStartNodes = 0;
        enemyStartNodes = 0;
        startDistance = 0;

        GenerateLayers(s);
        CountStats();

        // BFS: the biggest connected piece of walkable land (the "continent")
        FindLargestRegion();

        // Capitals are only placed on the continent, so they can always reach each other
        if (!FindStarts()) return "rejected_no_land";
        startDistance = Distance(startPlayer, startEnemy);
        playerStartNodes = StartNodes(startPlayer);
        enemyStartNodes = StartNodes(startEnemy);
        if (!IsConnected(startPlayer, startEnemy)) return "rejected_not_connected";
        if (startDistance < minStartDistance) return "rejected_starts_too_close";

        startGini = StartGini(playerStartNodes, enemyStartNodes);
        if (continentShare < minContinentShare) return "rejected_small_continent";
        return startGini > maxGini ? "rejected_unfair_gini" : "accepted";
    }

    // Rolls this attempt's noise and level settings from its own seed, so one seed always gives one map
    void RollParameters(System.Random rng)
    {
        usedNoiseScale = noiseScale;
        usedWaterLevel = waterLevel;
        usedHillsLevel = hillsLevel;
        usedMountainLevel = mountainLevel;
        usedPassCarveChance = passCarveChance;
        if (!randomizeParameters) return;

        float hillsGap = mountainLevel - hillsLevel;   // keep the Hills band the same width
        usedNoiseScale = Mathf.Max(0.01f, Roll(rng, noiseScale, noiseScaleVariation));
        usedWaterLevel = Roll(rng, waterLevel, waterLevelVariation);
        usedMountainLevel = Roll(rng, mountainLevel, mountainLevelVariation);
        usedHillsLevel = Mathf.Max(usedWaterLevel + 0.05f, usedMountainLevel - hillsGap);
        usedPassCarveChance = Mathf.Clamp01(Roll(rng, passCarveChance, passCarveChanceVariation));
    }

    // A random value between baseValue - variation and baseValue + variation
    static float Roll(System.Random rng, float baseValue, float variation)
    {
        return baseValue + variation * ((float)rng.NextDouble() * 2f - 1f);
    }

    // ---------- Research export (CSV) ----------

    string ExportFolder()
    {
        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ResearchExports"));
    }

    const string LogHeader =
        "timestamp,base_seed,attempt,seed,result,columns,rows,total_tiles,walkable_tiles,plateau_tiles," +
        "mountain_passes,pass_density,resource_tiles,player_start_nodes,enemy_start_nodes,start_gini," +
        "pass_carve_chance,resource_threshold,max_gini," +
        "noise_scale,water_level,mountain_level,continent_tiles,continent_share,start_distance";

    // Adds one row per map attempt to Map_Generation_Log.csv
    void ExportAttempt(int attempt, int s, string result)
    {
        if (!exportCsv) return;

        string folder = ExportFolder();
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "Map_Generation_Log.csv");

        // An older log with different columns is kept under a new name, so no data is lost
        if (File.Exists(file))
        {
            string firstLine;
            using (StreamReader rd = new StreamReader(file)) firstLine = rd.ReadLine();
            if (firstLine != LogHeader)
                File.Move(file, Path.Combine(folder, "Map_Generation_Log_old_" +
                          System.DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"));
        }

        bool newFile = !File.Exists(file);
        CultureInfo inv = CultureInfo.InvariantCulture;

        using (StreamWriter w = new StreamWriter(file, true))
        {
            if (newFile) w.WriteLine(LogHeader);

            w.WriteLine(string.Join(",", new string[] {
                System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", inv),
                seed.ToString(inv), attempt.ToString(inv), s.ToString(inv), result,
                columns.ToString(inv), rows.ToString(inv), totalTiles.ToString(inv), walkableTiles.ToString(inv),
                plateauTiles.ToString(inv), passCount.ToString(inv), passDensity.ToString("F4", inv),
                resourceTiles.ToString(inv), playerStartNodes.ToString(inv), enemyStartNodes.ToString(inv),
                startGini < 0f ? "" : startGini.ToString("F4", inv),
                usedPassCarveChance.ToString("F4", inv), resourceThreshold.ToString(inv), maxGini.ToString(inv),
                usedNoiseScale.ToString("F4", inv), usedWaterLevel.ToString("F4", inv), usedMountainLevel.ToString("F4", inv),
                continentTiles.ToString(inv), continentShare.ToString("F4", inv), startDistance.ToString(inv)
            }));
        }
    }

    // ---------- Stages 1-4: axial grid, noise layers, biomes, passes, resources ----------

    void GenerateLayers(int s)
    {
        System.Random rng = new System.Random(s);
        RollParameters(rng);
        Vector2 oElev = RandomOffset(rng), oMoist = RandomOffset(rng), oTemp = RandomOffset(rng);
        Vector2 oRare = RandomOffset(rng), oRes = RandomOffset(rng);

        tiles = new List<HexTile>();
        byCoord.Clear();

        Vector3 center = MapCenter();
        float halfW = columns * Mathf.Sqrt(3f) * hexSize / 2f;
        float halfH = rows * 1.5f * hexSize / 2f;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int q = OffsetToQ(col, row), r = row;
                Vector3 p = HexToWorld(q, r);

                // Elevation with contrast and the radial island mask
                float e = FBM(p.x, p.z, oElev, usedNoiseScale, octaves);
                e = Mathf.Clamp01((e - 0.5f) * elevationContrast + 0.5f);
                float dx = (p.x - center.x) / halfW;
                float dz = (p.z - center.z) / halfH;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                e *= Mathf.Exp(-falloffStrength * Mathf.Pow(d, falloffPower));

                // Moisture, and temperature (warm in the middle rows, cold at the top and bottom)
                float m = FBM(p.x, p.z, oMoist, usedNoiseScale, octaves);
                float latitude = 1f - Mathf.Abs((float)row / (rows - 1) - 0.5f) * 2f;
                float t = Mathf.Lerp(FBM(p.x, p.z, oTemp, usedNoiseScale, octaves), latitude, 0.35f);

                // Rare (Mana leylines) and resource veins
                float rare = Mathf.Clamp01((FBM(p.x, p.z, oRare, rareNoiseScale, 2) - 0.5f) * rareContrast + 0.5f);
                float res = Mathf.Clamp01((FBM(p.x, p.z, oRes, resNoiseScale, 2) - 0.5f) * resContrast + 0.5f);

                HexTile tile = new HexTile {
                    col = col, row = row, q = q, r = r, elevation = e, moisture = m, temperature = t, rare = rare, res = res
                };
                tile.type = Classify(e, m, t, rare);
                tiles.Add(tile);
                byCoord[new Vector2Int(q, r)] = tile;
            }
        }

        MarkCoasts();
        CarvePasses(rng);
        SpawnResources(rng);
    }

    Vector2 RandomOffset(System.Random rng)
    {
        return new Vector2((float)rng.NextDouble() * 1000f, (float)rng.NextDouble() * 1000f);
    }

    // Fractal Brownian Motion: several layers of Perlin noise added together
    float FBM(float x, float z, Vector2 offset, float scale, int layers)
    {
        float total = 0f, amplitude = 1f, frequency = scale, max = 0f;
        for (int o = 0; o < layers; o++)
        {
            total += Mathf.PerlinNoise(x * frequency + offset.x, z * frequency + offset.y) * amplitude;
            max += amplitude;
            amplitude *= 0.5f;
            frequency *= 2f;
        }
        return total / max;
    }

    // Elevation slicing plus the Whittaker biome table
    TileType Classify(float e, float m, float t, float rare)
    {
        if (e < usedWaterLevel) return TileType.Ocean;
        if (e >= usedMountainLevel) return TileType.Plateau;
        if (e >= usedHillsLevel) return TileType.Hills;

        if (t < 0.35f) return m >= 0.45f ? TileType.Taiga : TileType.Tundra;           // cold
        if (t > 0.65f) return m >= 0.35f ? TileType.Rainforest : TileType.Desert;       // hot
        if (m >= 0.55f) return rare >= magicForestLevel ? TileType.MagicForest : TileType.Woods;
        return m >= 0.40f ? TileType.Grassland : TileType.Plains;
    }

    // Shallow water next to land is Coast (walkable), the rest stays deep Ocean
    void MarkCoasts()
    {
        List<HexTile> coast = new List<HexTile>();
        foreach (HexTile t in tiles)
        {
            if (t.type != TileType.Ocean || t.elevation < shallowLevel) continue;
            foreach (HexTile n in Neighbors(t))
                if (n.type != TileType.Ocean) { coast.Add(t); break; }
        }
        foreach (HexTile t in coast) t.type = TileType.Coast;
    }

    // ---------- Stage 4: mountain pass carving ----------

    void CarvePasses(System.Random rng)
    {
        // Random ramps on plateau edges that touch 2 or more lowland tiles
        List<HexTile> carve = new List<HexTile>();
        foreach (HexTile t in tiles)
        {
            if (t.type != TileType.Plateau) continue;
            if (LowlandNeighbors(t) >= 2 && rng.NextDouble() < usedPassCarveChance) carve.Add(t);
        }
        foreach (HexTile t in carve) t.type = TileType.MountainPass;

        // Every plateau region gets at least one ramp, so no high ground is unreachable
        HashSet<HexTile> seen = new HashSet<HexTile>();
        foreach (HexTile t in tiles)
        {
            if (!IsMountain(t.type) || seen.Contains(t)) continue;

            List<HexTile> region = new List<HexTile>();
            Queue<HexTile> queue = new Queue<HexTile>();
            queue.Enqueue(t);
            seen.Add(t);
            while (queue.Count > 0)
            {
                HexTile cur = queue.Dequeue();
                region.Add(cur);
                foreach (HexTile n in Neighbors(cur))
                    if (IsMountain(n.type) && seen.Add(n)) queue.Enqueue(n);
            }

            bool hasPass = false;
            HexTile best = null;
            int bestCount = 0;
            foreach (HexTile m in region)
            {
                if (m.type == TileType.MountainPass) hasPass = true;
                int c = LowlandNeighbors(m);
                if (c > bestCount) { bestCount = c; best = m; }
            }
            if (!hasPass && best != null) best.type = TileType.MountainPass;
        }
    }

    int LowlandNeighbors(HexTile t)
    {
        int count = 0;
        foreach (HexTile n in Neighbors(t))
            if (IsLowland(n.type)) count++;
        return count;
    }

    public static bool IsMountain(TileType t)
    {
        return t == TileType.Plateau || t == TileType.MountainPass;
    }

    public static bool IsLowland(TileType t)
    {
        return t != TileType.Ocean && t != TileType.Coast && !IsMountain(t);
    }

    // ---------- Stage 4: resource nodes ----------

    static readonly ResourceType[] allResources = {
        ResourceType.Wheat, ResourceType.Meat, ResourceType.Wood,
        ResourceType.Mineral, ResourceType.Water, ResourceType.Mana
    };

    // Spawn weights per terrain, in the order of allResources. Unlisted share is split evenly.
    static Dictionary<TileType, float[]> spawnTable;

    static float[] Weights(params object[] pairs)
    {
        float[] w = new float[allResources.Length];
        float listed = 0f;
        int listedCount = 0;
        for (int i = 0; i < pairs.Length; i += 2)
        {
            int idx = System.Array.IndexOf(allResources, (ResourceType)pairs[i]);
            w[idx] = (float)pairs[i + 1];
            listed += w[idx];
            listedCount++;
        }
        float rest = (1f - listed) / (allResources.Length - listedCount);
        for (int i = 0; i < w.Length; i++) if (w[i] == 0f) w[i] = rest;
        return w;
    }

    static Dictionary<TileType, float[]> SpawnTable()
    {
        if (spawnTable != null) return spawnTable;
        spawnTable = new Dictionary<TileType, float[]> {
            { TileType.Grassland,    Weights(ResourceType.Wheat, 0.50f, ResourceType.Meat, 0.25f) },
            { TileType.Woods,        Weights(ResourceType.Wood, 0.50f, ResourceType.Meat, 0.20f) },
            { TileType.Rainforest,   Weights(ResourceType.Water, 0.40f, ResourceType.Wood, 0.30f) },
            { TileType.Plains,       Weights(ResourceType.Wheat, 0.35f, ResourceType.Meat, 0.30f) },
            { TileType.Hills,        Weights(ResourceType.Mineral, 0.45f, ResourceType.Wood, 0.20f) },
            { TileType.Desert,       Weights(ResourceType.Mana, 0.40f, ResourceType.Mineral, 0.35f) },
            { TileType.Tundra,       Weights(ResourceType.Meat, 0.40f, ResourceType.Mineral, 0.25f) },
            { TileType.Taiga,        Weights(ResourceType.Wood, 0.40f, ResourceType.Meat, 0.30f) },
            { TileType.MagicForest,  Weights(ResourceType.Mana, 0.50f, ResourceType.Water, 0.30f) },
            { TileType.MountainPass, Weights(ResourceType.Mineral, 0.60f, ResourceType.Mana, 0.20f) },
            { TileType.Plateau,      Weights(ResourceType.Mineral, 0.70f) },
            { TileType.Coast,        Weights(ResourceType.Water, 0.60f, ResourceType.Meat, 0.25f) }, // seafood counts as Meat
        };
        return spawnTable;
    }

    void SpawnResources(System.Random rng)
    {
        foreach (HexTile t in tiles)
        {
            t.resource = ResourceType.None;
            if (!IsWalkable(t.type) || t.res < resourceThreshold) continue;

            float[] w = SpawnTable()[t.type];
            float roll = (float)rng.NextDouble();
            float sum = 0f;
            t.resource = allResources[allResources.Length - 1];
            for (int i = 0; i < w.Length; i++)
            {
                sum += w[i];
                if (roll < sum) { t.resource = allResources[i]; break; }
            }
        }
    }
    // ---------- Stage 5: SBPCG validation ----------

    // Breadth-First Search over every walkable tile: keeps the biggest connected region.
    // Uses CanStep, so cliffs count: a plateau only joins the region through a mountain pass.
    void FindLargestRegion()
    {
        continent.Clear();
        HashSet<HexTile> seen = new HashSet<HexTile>();

        foreach (HexTile start in tiles)
        {
            if (!IsWalkable(start.type) || seen.Contains(start)) continue;

            List<HexTile> region = new List<HexTile>();
            Queue<HexTile> queue = new Queue<HexTile>();
            queue.Enqueue(start);
            seen.Add(start);
            while (queue.Count > 0)
            {
                HexTile cur = queue.Dequeue();
                region.Add(cur);
                foreach (HexTile n in Neighbors(cur))
                    if (!seen.Contains(n) && CanStep(cur, n)) { seen.Add(n); queue.Enqueue(n); }
            }

            if (region.Count > continent.Count) continent = new HashSet<HexTile>(region);
        }

        continentTiles = continent.Count;
        continentShare = tiles.Count > 0 ? (float)continentTiles / tiles.Count : 0f;
    }

    // Start tiles: the continent's lowland tiles closest to the left and right quarter of the map (mirrored)
    bool FindStarts()
    {
        int midRow = rows / 2;
        startPlayer = NearestLowland(OffsetToQ(columns / 4, midRow), midRow);
        startEnemy = NearestLowland(OffsetToQ(columns * 3 / 4, midRow), midRow);
        return startPlayer != null && startEnemy != null && startPlayer != startEnemy;
    }

    HexTile NearestLowland(int q, int r)
    {
        HexTile best = null;
        int bestDist = int.MaxValue;
        foreach (HexTile t in tiles)
        {
            if (!IsLowland(t.type) || !continent.Contains(t)) continue;
            int d = Distance(t.q, t.r, q, r);
            if (d < bestDist) { bestDist = d; best = t; }
        }
        return best;
    }

    // Breadth-First Search: is there a walkable path between the two capitals?
    bool IsConnected(HexTile from, HexTile to)
    {
        HashSet<HexTile> seen = new HashSet<HexTile>();
        Queue<HexTile> queue = new Queue<HexTile>();
        queue.Enqueue(from);
        seen.Add(from);

        while (queue.Count > 0)
        {
            HexTile cur = queue.Dequeue();
            if (cur == to) return true;

            foreach (HexTile n in Neighbors(cur))
            {
                if (seen.Contains(n) || !CanStep(cur, n)) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return false;
    }

    // Number of resource nodes within the fairness radius of a start tile
    int StartNodes(HexTile start)
    {
        int count = 0;
        foreach (HexTile t in tiles)
            if (t.resource != ResourceType.None && Distance(t, start) <= fairnessRadius) count++;
        return count;
    }

    // Gini coefficient of the two starting areas (0 = perfectly fair)
    float StartGini(int a, int b)
    {
        if (a + b <= 0) return 1f;
        return Mathf.Abs(a - b) / (2f * (a + b));
    }

    void CountStats()
    {
        totalTiles = tiles.Count;
        walkableTiles = 0;
        plateauTiles = 0;
        passCount = 0;
        resourceTiles = 0;
        foreach (HexTile t in tiles)
        {
            if (IsWalkable(t.type)) walkableTiles++;
            if (t.type == TileType.Plateau) plateauTiles++;
            if (t.type == TileType.MountainPass) passCount++;
            if (t.resource != ResourceType.None) resourceTiles++;
        }
        passDensity = walkableTiles > 0 ? (float)passCount / walkableTiles : 0f;
    }

    // ---------- Movement rules (used by GameManager too) ----------

    public bool IsWalkable(TileType t)
    {
        return t != TileType.Ocean;
    }

    // Cliffs: plateau tiles only connect to other plateau tiles and to passes
    public bool CanStep(HexTile from, HexTile to)
    {
        if (!IsWalkable(from.type) || !IsWalkable(to.type)) return false;
        if (from.type == TileType.Plateau && !IsMountain(to.type)) return false;
        if (to.type == TileType.Plateau && !IsMountain(from.type)) return false;
        return true;
    }

    // Move points to enter a tile
    public int MoveCost(TileType t)
    {
        switch (t)
        {
            case TileType.Rainforest:
            case TileType.Hills:
            case TileType.Tundra:
            case TileType.Taiga:
            case TileType.MountainPass:
            case TileType.Plateau:
            case TileType.Coast:
                return 2;
            default:
                return 1;
        }
    }

    // ---------- Names for the UI and CSVs ----------

    public static string BiomeName(TileType t)
    {
        switch (t)
        {
            case TileType.Ocean:
            case TileType.Coast: return "Ocean";
            case TileType.Grassland:
            case TileType.Woods:
            case TileType.Rainforest: return "Grassland";
            case TileType.Plains:
            case TileType.Hills: return "Plains";
            case TileType.Desert: return "Desert";
            case TileType.Tundra:
            case TileType.Taiga: return "Tundra";
            case TileType.MagicForest: return "Forest";
            default: return "Mountain";
        }
    }

    public static string FeatureName(TileType t)
    {
        switch (t)
        {
            case TileType.Ocean: return "Deep";
            case TileType.Coast: return "Shallow";
            case TileType.Woods: return "Woods";
            case TileType.Rainforest: return "Rainforest";
            case TileType.Hills: return "Hills";
            case TileType.Taiga: return "Taiga";
            case TileType.MagicForest: return "Magic Forest";
            case TileType.MountainPass: return "Pass";
            case TileType.Plateau: return "Plateau";
            default: return "Open";
        }
    }

    public static string TypeLabel(TileType t)
    {
        return BiomeName(t) + " / " + FeatureName(t);
    }

    // ---------- Building the 3D tiles ----------

    void BuildTiles()
    {
        foreach (HexTile t in tiles)
        {
            t.obj = new GameObject("Hex " + t.col + "," + t.row);
            t.obj.transform.parent = transform;
            t.obj.transform.position = HexToWorld(t.q, t.r);
            t.obj.transform.localScale = new Vector3(1f, TerrainHeight(t.type), 1f);

            t.obj.AddComponent<MeshFilter>().sharedMesh = hexMesh;
            t.rend = t.obj.AddComponent<MeshRenderer>();
            t.rend.material = new Material(baseMat);
            t.obj.AddComponent<MeshCollider>().sharedMesh = hexMesh;

            t.baseColor = TerrainColor(t.type);
            t.rend.material.color = t.baseColor;
            lookup[t.obj] = t;

            if (t.resource != ResourceType.None) BuildResourceMarker(t);
        }
    }

    // A small colored ball shows which resource a tile holds
    void BuildResourceMarker(HexTile t)
    {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        Destroy(b.GetComponent<Collider>());
        b.name = "Resource " + t.resource;
        b.transform.parent = transform;
        b.transform.localScale = Vector3.one * 0.22f;
        b.transform.position = t.obj.transform.position + new Vector3(-0.45f, t.obj.transform.localScale.y + 0.11f, -0.25f);
        b.GetComponent<Renderer>().material.color = ResourceColor(t.resource);
    }

    public static Color ResourceColor(ResourceType res)
    {
        switch (res)
        {
            case ResourceType.Wheat:   return new Color(1.00f, 0.85f, 0.20f);
            case ResourceType.Meat:    return new Color(0.85f, 0.35f, 0.35f);
            case ResourceType.Wood:    return new Color(0.55f, 0.33f, 0.15f);
            case ResourceType.Mineral: return new Color(0.70f, 0.70f, 0.75f);
            case ResourceType.Water:   return new Color(0.30f, 0.85f, 1.00f);
            case ResourceType.Mana:    return new Color(0.80f, 0.30f, 1.00f);
            default:                   return Color.white;
        }
    }

    float TerrainHeight(TileType type)
    {
        switch (type)
        {
            case TileType.Ocean: return 0.15f;
            case TileType.Coast: return 0.25f;
            case TileType.Woods:
            case TileType.Rainforest:
            case TileType.Taiga:
            case TileType.MagicForest: return 0.45f;
            case TileType.Hills: return 0.6f;
            case TileType.MountainPass: return 0.85f;
            case TileType.Plateau: return 1.2f;
            default: return 0.4f;
        }
    }

    Color TerrainColor(TileType type)
    {
        switch (type)
        {
            case TileType.Ocean:        return new Color(0.12f, 0.30f, 0.70f);
            case TileType.Coast:        return new Color(0.30f, 0.55f, 0.90f);
            case TileType.Grassland:    return new Color(0.45f, 0.75f, 0.30f);
            case TileType.Woods:        return new Color(0.15f, 0.45f, 0.20f);
            case TileType.Rainforest:   return new Color(0.05f, 0.35f, 0.15f);
            case TileType.Plains:       return new Color(0.75f, 0.78f, 0.40f);
            case TileType.Hills:        return new Color(0.60f, 0.60f, 0.35f);
            case TileType.Desert:       return new Color(0.90f, 0.80f, 0.50f);
            case TileType.Tundra:       return new Color(0.80f, 0.85f, 0.88f);
            case TileType.Taiga:        return new Color(0.25f, 0.42f, 0.35f);
            case TileType.MagicForest:  return new Color(0.45f, 0.25f, 0.65f);
            case TileType.MountainPass: return new Color(0.70f, 0.55f, 0.35f);
            default:                    return new Color(0.50f, 0.45f, 0.42f); // Plateau
        }
    }

    // ---------- Towns ----------

    void PlaceTown(HexTile town, string owner, string townName)
    {
        town.town = townName;

        foreach (HexTile t in tiles)
            if (Distance(t, town) <= townRadius)
                Claim(t, owner);

        BuildTownModel(town, owner == "Player" ? playerColor : enemyColor);
    }

    void BuildTownModel(HexTile t, Color color)
    {
        Vector3 top = t.obj.transform.position + Vector3.up * t.obj.transform.localScale.y;
        Vector3[] offsets = {
            new Vector3(0f, 0f, 0f), new Vector3(0.4f, 0f, 0.25f),
            new Vector3(-0.4f, 0f, 0.2f), new Vector3(0.1f, 0f, -0.4f)
        };
        float[] sizes = { 0.45f, 0.3f, 0.3f, 0.25f };

        for (int i = 0; i < offsets.Length; i++)
        {
            GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.name = t.town + " building";
            b.transform.parent = transform;
            b.transform.localScale = Vector3.one * sizes[i];
            b.transform.position = top + offsets[i] + Vector3.up * sizes[i] / 2f;
            b.GetComponent<Renderer>().material.color = color;
            lookup[b] = t;
        }
    }

    // ---------- Helpers ----------

    // Pointy-top axial coordinates to world position
    public Vector3 HexToWorld(int q, int r)
    {
        float x = hexSize * Mathf.Sqrt(3f) * (q + r / 2f);
        float z = hexSize * 1.5f * r;
        return new Vector3(x, 0f, z);
    }

    // Rectangle column to axial q (odd rows are shifted half a tile to the right)
    public int OffsetToQ(int col, int row)
    {
        return col - (row - (row & 1)) / 2;
    }

    public Vector3 MapCenter()
    {
        float w = Mathf.Sqrt(3f) * hexSize;
        return new Vector3(w * (columns - 0.5f) / 2f, 0f, 1.5f * hexSize * (rows - 1) / 2f);
    }

    public int Distance(int q1, int r1, int q2, int r2)
    {
        int dq = q1 - q2, dr = r1 - r2;
        return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2;
    }

    public int Distance(HexTile a, HexTile b)
    {
        return Distance(a.q, a.r, b.q, b.r);
    }

    public HexTile Get(int q, int r)
    {
        HexTile t;
        return byCoord.TryGetValue(new Vector2Int(q, r), out t) ? t : null;
    }

    // The six tiles around a tile (only ones inside the map)
    public List<HexTile> Neighbors(HexTile t)
    {
        List<HexTile> list = new List<HexTile>();
        foreach (Vector2Int d in directions)
        {
            HexTile n = Get(t.q + d.x, t.r + d.y);
            if (n != null) list.Add(n);
        }
        return list;
    }

    // Gives a tile to a side and colors it
    public void Claim(HexTile t, string owner)
    {
        t.owner = owner;
        Color ownerColor = owner == "Player" ? playerColor : enemyColor;
        t.baseColor = Color.Lerp(TerrainColor(t.type), ownerColor, 0.35f);
        t.rend.material.color = t.baseColor;
    }

    public HexTile GetTile(GameObject go)
    {
        HexTile t;
        return lookup.TryGetValue(go, out t) ? t : null;
    }

    public void SetHighlight(HexTile t, bool on)
    {
        t.rend.material.color = on ? Color.Lerp(t.baseColor, Color.white, 0.45f) : t.baseColor;
    }

    // Tints a tile to show you can build there. 'selected' makes it brighter, like SetHighlight.
    public void SetBuildTint(HexTile t, Color tint, float amount, bool selected)
    {
        Color c = Color.Lerp(t.baseColor, tint, amount);
        if (selected) c = Color.Lerp(c, Color.white, 0.45f);
        t.rend.material.color = c;
    }

    Mesh BuildHexMesh()
    {
        float rad = hexSize * 0.95f;
        Vector3[] c = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float a = (60f * i - 30f) * Mathf.Deg2Rad;
            c[i] = new Vector3(rad * Mathf.Cos(a), 0f, rad * Mathf.Sin(a));
        }

        List<Vector3> v = new List<Vector3>();
        List<int> tris = new List<int>();

        v.Add(Vector3.up);
        for (int i = 0; i < 6; i++) v.Add(c[i] + Vector3.up);
        for (int i = 0; i < 6; i++)
        {
            tris.Add(0); tris.Add(1 + (i + 1) % 6); tris.Add(1 + i);
        }

        for (int i = 0; i < 6; i++)
        {
            int s = v.Count;
            Vector3 a = c[i], b = c[(i + 1) % 6];
            v.Add(a + Vector3.up); v.Add(b + Vector3.up); v.Add(a); v.Add(b);
            tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
            tris.Add(s + 1); tris.Add(s + 3); tris.Add(s + 2);
        }

        Mesh mesh = new Mesh();
        mesh.vertices = v.ToArray();
        mesh.triangles = tris.ToArray();
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        return mesh;
    }
}

// Information about one hex tile
public class HexTile
{
    public int col, row;        // position in the rectangle
    public int q, r;            // axial coordinates (used for distances and neighbors)
    public TileType type;
    public float elevation, moisture, temperature, rare, res;   // the 5 heatmaps
    public ResourceType resource = ResourceType.None;           // resource node on this tile
    public string owner = "";   // "", "Player" or "Enemy"
    public string town = "";    // town name, empty if no town here
    public GameObject obj;
    public Renderer rend;
    public Color baseColor;
}