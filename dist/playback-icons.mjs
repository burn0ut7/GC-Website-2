// Names and artwork from Reforger's EditableEntities.imageset. Never interpret a URL from telemetry.
export const iconNames = new Set([
  "EditableEntity_Character_AmmoBearer",
  "EditableEntity_Character_AntiTank",
  "EditableEntity_Character_Custom",
  "EditableEntity_Character_Dead",
  "EditableEntity_Character_Grenadier",
  "EditableEntity_Character_Leader",
  "EditableEntity_Character_MachineGunner",
  "EditableEntity_Character_Medic",
  "EditableEntity_Character_Player",
  "EditableEntity_Character_RadioOperator",
  "EditableEntity_Character_Rifleman",
  "EditableEntity_Character_Sharpshooter",
  "EditableEntity_Character_Spotter",
  "EditableEntity_Character_Unarmed",
  "EditableEntity_Vehicle_Apc",
  "EditableEntity_Vehicle_Civilian",
  "EditableEntity_Vehicle_Helicopter",
  "EditableEntity_Vehicle_Offroad",
  "EditableEntity_Vehicle_Offroad_Armed",
  "EditableEntity_Vehicle_Offroad_Medical",
  "EditableEntity_Vehicle_Truck",
  "EditableEntity_Vehicle_Truck_Command",
  "EditableEntity_Vehicle_Truck_Fuel",
  "EditableEntity_Vehicle_Turret_MachineGun",
]);
const roleNames = new Set([
  "EditableEntity_Character_Ammo_Bearer",
  "EditableEntity_Character_Anti_Tank",
  "EditableEntity_Character_Crew",
  "EditableEntity_Character_Custom",
  "EditableEntity_Character_Dead",
  "EditableEntity_Character_Driver",
  "EditableEntity_Character_Empty",
  "EditableEntity_Character_Engineer",
  "EditableEntity_Character_Grenadier",
  "EditableEntity_Character_Helicopter_Crew",
  "EditableEntity_Character_Helicopter_Pilot",
  "EditableEntity_Character_Leader_Squad",
  "EditableEntity_Character_Leader_Team",
  "EditableEntity_Character_Machinegunner",
  "EditableEntity_Character_Medic",
  "EditableEntity_Character_Mortar",
  "EditableEntity_Character_Officer",
  "EditableEntity_Character_Player",
  "EditableEntity_Character_Radio_Operator",
  "EditableEntity_Character_Radio_Scout",
  "EditableEntity_Character_Rifleman",
  "EditableEntity_Character_Sapper",
  "EditableEntity_Character_Scout",
  "EditableEntity_Character_Sharpshooter",
  "EditableEntity_Character_Special_Forces",
  "EditableEntity_Character_Spotter",
  "EditableEntity_Character_SquadLeaderALT",
  "EditableEntity_Character_Unnarmed",
]);
export function actorIcon(data) {
  const resource = (data.iconResource ?? "").replace(/^\{[A-Fa-f0-9]+\}/, "");
  let name = data.iconName,
    prefix = "";
  if (
    resource === "UI/Textures/DeployMenu/LoadoutIcons/LoadoutIcons.imageset"
  ) {
    if (!roleNames.has(name)) return null;
    prefix = "loadout-";
  } else if (
    resource === "UI/Imagesets/Editor/Icons/EditableEntities.imageset"
  ) {
    if (!iconNames.has(name)) return null;
  } else {
    const match =
      /^UI\/Textures\/Editor\/EditableEntities\/(?:Vehicles|Characters)\/(EditableEntity_(?:Vehicle|Character)_\w+)\.edds$/.exec(
        resource,
      );
    if (!match || !iconNames.has(match[1])) return null;
    name = match[1];
  }
  return {
    name,
    url: "/icons/reforger/" + prefix + name + ".png",
    label: name
      .replace(/^EditableEntity_(Character|Vehicle)_/, "")
      .replace(/_/g, " "),
  };
}
