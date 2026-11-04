import test from 'node:test';
import assert from 'node:assert/strict';
import {vehicleShape, drawVehicle} from '../dist/playback-vehicles.mjs';
test('vehicle silhouettes use recorded class and rotate around their position',()=>{
  for(const [name,shape] of [['Offroad_Armed','car'],['Apc','apc'],['Truck_Fuel','truck'],['Helicopter','helicopter'],['Turret_MachineGun','turret']]) {
    const icon={name:'EditableEntity_Vehicle_'+name};assert.equal(vehicleShape(icon),shape);
    let rotation,translation,depth=0;
    const ctx=new Proxy({save(){depth++},restore(){depth--},rotate(n){rotation=n},translate(...p){translation=p}}, {get:(o,k)=>k in o?o[k]:()=>{}});
    drawVehicle(ctx,[100,200],90,'#abcdef',icon);
    assert.equal(rotation,Math.PI/2);assert.deepEqual(translation,[100,200]);assert.equal(depth,0);
  }
  assert.equal(vehicleShape(null),'vehicle');
});
