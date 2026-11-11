using UnityEngine;

// The six resources in the order they are shown
public static class Res
{
    public static readonly ResourceType[] All = {
        ResourceType.Wheat, ResourceType.Meat, ResourceType.Wood,
        ResourceType.Mineral, ResourceType.Water, ResourceType.Mana
    };
}

// An amount of each resource: a price, an income, or a stockpile. You can edit these in the Inspector.
[System.Serializable]
public class Cost
{
    public int wheat, meat, wood, mineral, water, mana;

    public int Get(ResourceType r)
    {
        switch (r)
        {
            case ResourceType.Wheat: return wheat;
            case ResourceType.Meat: return meat;
            case ResourceType.Wood: return wood;
            case ResourceType.Mineral: return mineral;
            case ResourceType.Water: return water;
            case ResourceType.Mana: return mana;
            default: return 0;
        }
    }

    public void Add(ResourceType r, int amount)
    {
        switch (r)
        {
            case ResourceType.Wheat: wheat += amount; break;
            case ResourceType.Meat: meat += amount; break;
            case ResourceType.Wood: wood += amount; break;
            case ResourceType.Mineral: mineral += amount; break;
            case ResourceType.Water: water += amount; break;
            case ResourceType.Mana: mana += amount; break;
        }
    }

    public void Set(ResourceType r, int amount)
    {
        Add(r, amount - Get(r));
    }

    public Cost Copy()
    {
        return new Cost { wheat = wheat, meat = meat, wood = wood, mineral = mineral, water = water, mana = mana };
    }

    // For example "(15 Wood, 5 Water)"
    public string Text()
    {
        string s = "";
        foreach (ResourceType r in Res.All)
        {
            if (Get(r) == 0) continue;
            if (s != "") s += ", ";
            s += Get(r) + " " + r;
        }
        return "(" + (s == "" ? "free" : s) + ")";
    }
}

// One kind of building
public class BuildingType
{
    public string name;
    public Cost cost;
    public int needsTownLevel = 1;
    public bool onePerSide;
    public ResourceType needsResource = ResourceType.None;  // extraction buildings must sit on this resource tile
    public ResourceType produces = ResourceType.None;
    public int amount;              // produced per turn
    public int popCap;              // extra population cap
    public int storage;             // extra storage cap for every resource
    public int wheatUpkeep;         // wheat paid per turn
    public int claimRadius;         // territory claimed around it (Outpost)
    public bool borderOnly;         // must touch unclaimed land (Outpost)
    public PrimitiveType shape;
    public Vector3 size;
    public Color color;
}

// A building placed on the map
public class Building
{
    public BuildingType type;
    public string owner;
    public HexTile tile;
}

// Everything one side owns besides its warriors
public class Side
{
    public string name;             // "Player" or "Enemy"
    public Cost stock;
    public int citizens;
    public int townLevel = 1;
    public bool trainedThisTurn, builtThisTurn;
    public int starveTurns;         // turns in a row without enough wheat
    public bool starving;           // -25% attack
    public bool dehydrated;         // -1 move
    public int meatEaten;           // Meat eaten at the last Upkeep because Wheat ran short
}

public static class BuildingCatalog
{
    // Farm, Lumber Camp, Mine Quarry, Mana Sanctum, House, Warehouse and Outpost follow the spec.
    // Well and Hunting Lodge are extra: the spec has Water and Meat tiles but no building to harvest them.
    public static BuildingType[] Create()
    {
        return new BuildingType[]
        {
            new BuildingType { name = "Farm", cost = new Cost { wood = 15, water = 5 },
                needsResource = ResourceType.Wheat, produces = ResourceType.Wheat, amount = 4,
                shape = PrimitiveType.Cube, size = new Vector3(0.45f, 0.08f, 0.45f), color = new Color(0.95f, 0.85f, 0.3f) },
            new BuildingType { name = "Lumber Camp", cost = new Cost { mineral = 10 },
                needsResource = ResourceType.Wood, produces = ResourceType.Wood, amount = 4,
                shape = PrimitiveType.Cylinder, size = new Vector3(0.2f, 0.2f, 0.2f), color = new Color(0.5f, 0.3f, 0.15f) },
            new BuildingType { name = "Mine Quarry", cost = new Cost { wood = 15, mineral = 5 },
                needsResource = ResourceType.Mineral, produces = ResourceType.Mineral, amount = 3,
                shape = PrimitiveType.Cube, size = new Vector3(0.3f, 0.25f, 0.3f), color = new Color(0.45f, 0.45f, 0.5f) },
            new BuildingType { name = "Mana Sanctum", cost = new Cost { wood = 20, mineral = 15 },
                needsResource = ResourceType.Mana, produces = ResourceType.Mana, amount = 1,
                shape = PrimitiveType.Sphere, size = new Vector3(0.3f, 0.3f, 0.3f), color = new Color(0.7f, 0.3f, 1f) },
            new BuildingType { name = "Well", cost = new Cost { wood = 10, mineral = 5 },
                needsResource = ResourceType.Water, produces = ResourceType.Water, amount = 3,
                shape = PrimitiveType.Cylinder, size = new Vector3(0.25f, 0.12f, 0.25f), color = new Color(0.3f, 0.8f, 1f) },
            new BuildingType { name = "Hunting Lodge", cost = new Cost { wood = 10, mineral = 5 },
                needsResource = ResourceType.Meat, produces = ResourceType.Meat, amount = 3,
                shape = PrimitiveType.Cube, size = new Vector3(0.3f, 0.2f, 0.25f), color = new Color(0.75f, 0.3f, 0.3f) },
            new BuildingType { name = "House", cost = new Cost { wood = 10, mineral = 5 }, popCap = 3,
                shape = PrimitiveType.Cube, size = new Vector3(0.3f, 0.3f, 0.3f), color = new Color(0.95f, 0.95f, 0.9f) },
            new BuildingType { name = "Warehouse", cost = new Cost { wood = 15, mineral = 10 }, storage = 50,
                shape = PrimitiveType.Cube, size = new Vector3(0.5f, 0.25f, 0.35f), color = new Color(0.75f, 0.6f, 0.4f) },
            new BuildingType { name = "Outpost", cost = new Cost { wood = 20, mineral = 10 }, wheatUpkeep = 1,
                claimRadius = 1, borderOnly = true,
                shape = PrimitiveType.Cylinder, size = new Vector3(0.25f, 0.35f, 0.25f), color = Color.white },
            // Barracks: an extra place to train Tier 1 warriors (the capital can train too)
            new BuildingType { name = "Barracks", cost = new Cost { wood = 20, mineral = 10 },
                shape = PrimitiveType.Cube, size = new Vector3(0.45f, 0.2f, 0.3f), color = new Color(0.55f, 0.2f, 0.2f) },
            // Transmutation Lab: lets Tier 1 warriors promote to Tier 2
            new BuildingType { name = "Transmutation Lab", cost = new Cost { wood = 20, mineral = 20, mana = 5 },
                needsTownLevel = 2, onePerSide = true,
                shape = PrimitiveType.Sphere, size = new Vector3(0.35f, 0.35f, 0.35f), color = new Color(0.3f, 0.9f, 0.8f) },
        };
    }
}

// The stats for one kind of warrior. You can edit these in the Inspector.
[System.Serializable]
public class UnitType
{
    public string name;
    public string letter;   // shown above the unit
    public int tier = 1;    // 1 or 2, decides the upkeep
    public int maxHP;
    public int attack;
    public int defense;
    public int moves;       // move points per turn
    public int range;       // 1 = melee, 2 or more = ranged
    public Vector3 size;    // capsule size
    public Cost cost;
    public string promotesTo;
    public Cost promoteCost;
    public bool heavyArmor;
    public bool forestWalker;
    public int forestBonus;
    public bool plainsCharge;
}

// One warrior on the map
public class Unit
{
    public string owner;    // "Player" or "Enemy"
    public UnitType type;
    public HexTile tile;
    public int hp;
    public int movesLeft;
    public bool hasAttacked;
    public GameObject obj;
}