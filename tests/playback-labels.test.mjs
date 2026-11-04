import test from 'node:test';
import assert from 'node:assert/strict';
import {prepareMapLabels,drawMapLabels} from '../dist/playback-topography.mjs';
test('label priority, collisions, north-up position and zoom visibility',()=>{
 const labels=prepareMapLabels({version:1,labels:[{name:'Small place',x:0,z:10,category:'place'},{name:'Town',x:0,z:10,category:'town'},{name:'Hill',x:90,z:0,category:'landscape'}]});
 const drawn=[];const ctx={getTransform(){return {a:1}},save(){},restore(){},measureText(t){return {width:t.length*6}},strokeText(){},fillText(...v){drawn.push(v)}};
 drawMapLabels(ctx,labels,{x:0,z:0,scale:1},400,400);
 assert.deepEqual(drawn.map(v=>v[0]),['Town','Hill']);assert.equal(drawn[0][2],190);
 drawn.length=0;drawMapLabels(ctx,labels,{x:0,z:0,scale:.05},400,400);assert.equal(drawn.length,0);
 assert.throws(()=>prepareMapLabels({version:1,labels:[{name:'Bad',x:NaN,z:0,category:'town'}]}));
});

test('town names appear at overview scale; other place names wait for detail zoom',()=>{
 const labels=prepareMapLabels({version:1,labels:[
 {name:'Town',x:-500,z:0,category:'town'},
 {name:'Airfield',x:0,z:0,category:'place'},
 {name:'Hill',x:500,z:0,category:'landscape'}]});
 const drawn=[],ctx={getTransform(){return {a:1}},save(){},restore(){},measureText(t){return {width:t.length*6}},strokeText(){},fillText(t){drawn.push(t)}};
 for(const [scale,expected] of [[.099,[]],[.1,['Town']],[.22,['Town']],[.289,['Town']],[.29,['Town','Airfield','Hill']]]){
   drawn.length=0;drawMapLabels(ctx,labels,{x:0,z:0,scale},1000,500);assert.deepEqual(drawn,expected);
 }
});
