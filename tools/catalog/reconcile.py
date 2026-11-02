"""Compare shared source keys and fields without merging either source record."""
import json

# These client datasets are published under different names in CCP's export.
ALIASES = {
    "graphics": "graphicids", "icons": "iconids", "typeLists": "typelist",
    "mapRegions": "regions", "mapConstellations": "constellations", "mapSolarSystems": "systems",
    "planetSchematics": "schematics", "industryActivities": "industry_activities",
    "industryAssemblyLines": "industry_assembly_lines", "industryInstallationTypes": "industry_installation_types",
    "industryModifierSources": "industry_activity_modifier_sources", "industryTargetFilters": "industry_activity_target_filters",
    "skinrComponentCategories": "ship_skin_design_component_categories",
    "skinrComponentPointValues": "ship_skin_design_component_point_values",
    "skinrComponentRarities": "ship_skin_design_component_rarities", "skinrComponents": "ship_skin_design_components",
    "skinrSlotCategories": "ship_cosmetic_slot_categories", "skinrSlotConfigurations": "ship_cosmetic_slot_configurations",
    "skinrSlotNames": "ship_cosmetic_slot_names", "skinrSlots": "ship_cosmetic_slots",
    "skinrTierThresholds": "ship_skin_design_tier_thresholds",
}


def compare(db):
    names = {name for name, in db.execute("SELECT Name FROM SourceDataset")}
    for export in sorted(n for n in names if n.startswith("sde/")):
        name = export[4:]
        client = "client/" + ALIASES.get(name, name.lower())
        if name in ("mapMoons", "mapPlanets", "mapStars", "mapAsteroidBelts", "mapStargates"):
            client = "map/celestials"
        if name == "npcStations":
            client = "map/npcStations"
        if client not in names:
            continue
        paired = db.execute("""
            SELECT s.Key,s.Data,c.Data FROM SourceRecord s JOIN SourceRecord c
            ON c.Dataset=? AND c.Key IN (s.Key,json_quote(CAST(json_extract(s.Key,'$') AS TEXT)))
            WHERE s.Dataset=?
            """, (client, export))
        count = different = 0
        for key, export_json, client_json in paired:
            count += 1
            left, right = json.loads(export_json), json.loads(client_json)
            if not isinstance(left, dict) or not isinstance(right, dict):
                fields = [] if left == right else ["$"]
            else:
                if "position" in left and all(k in right for k in ("x", "y", "z")):
                    right["position"] = {k: right[k] for k in ("x", "y", "z")}
                fields = sorted(k for k in left.keys() & right.keys() if left[k] != right[k])
            if fields:
                different += 1
                db.execute("INSERT INTO SourceDifference(Export,Client,Key,Fields) VALUES (?,?,?,?)", (export, client, key, json.dumps(fields)))
        db.execute("INSERT INTO SourceOverlap(Export,Client,SharedKeys,DifferentRows) VALUES (?,?,?,?)", (export, client, count, different))
    db.commit()
