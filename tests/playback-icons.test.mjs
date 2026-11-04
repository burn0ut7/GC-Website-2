import test from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import { actorIcon } from "../dist/playback-icons.mjs";
const cases = [
  [
    "UI/Textures/Editor/EditableEntities/Vehicles/EditableEntity_Vehicle_Offroad.edds",
    "",
    "Offroad",
  ],
  [
    "UI/Textures/Editor/EditableEntities/Vehicles/EditableEntity_Vehicle_Apc.edds",
    "",
    "Apc",
  ],
  [
    "UI/Textures/Editor/EditableEntities/Vehicles/EditableEntity_Vehicle_Helicopter.edds",
    "",
    "Helicopter",
  ],
  [
    "{EE0B4CDEA86FC24A}UI/Textures/DeployMenu/LoadoutIcons/LoadoutIcons.imageset",
    "EditableEntity_Character_Anti_Tank",
    "Anti Tank",
  ],
];
test("native vehicle and role resources resolve only to shipped icons", () => {
  for (const [iconResource, iconName, label] of cases) {
    const icon = actorIcon({ iconResource, iconName });
    assert.equal(icon.label, label);
    assert.ok(fs.existsSync(new URL("../dist" + icon.url, import.meta.url)));
  }
});
test("unknown and untrusted icon resources remain generic", () => {
  for (const iconResource of [
    "",
    "https://example.com/image.png",
    "../../secrets",
    "UI/Textures/Custom/EditableEntity_Vehicle_Offroad.edds",
  ])
    assert.equal(
      actorIcon({ iconResource, iconName: "EditableEntity_Vehicle_Offroad" }),
      null,
    );
  assert.equal(
    actorIcon({ iconResource: cases[3][0], iconName: "not-shipped" }),
    null,
  );
});
