import {test} from 'node:test';
import assert from 'node:assert/strict';
import {combatCheckpoints} from '../dist/playback-checkpoints.mjs';

test('player checkpoints use event-time ownership and faction, omit AI and suicide kills',()=>{
  const actor=(states)=>({states:states.map(([timeMs,playerIdentity,faction])=>({timeMs,data:{playerIdentity,faction}}))});
  const model={names:new Map([['one','One'],['two','Two']]),actors:new Map([
    ['a',actor([[0,'one','US'],[30,'two','FIA'],[50,'','FIA']])],
    ['b',actor([[0,'two','USSR']])],['ai',actor([[0,'','USSR']])]
  ]),events:[
    {type:'death',timeMs:10,data:{entity:'ai',killer:'a'}},
    {type:'death',timeMs:20,data:{entity:'b',killer:'a'}},
    {type:'death',timeMs:40,data:{entity:'a',killer:'a'}},
    {type:'death',timeMs:60,data:{entity:'ai',killer:'a'}}
  ]};
  assert.deepEqual(combatCheckpoints(model),[
    {kind:'kill',timeMs:10,faction:'US',name:'One'},
    {kind:'death',timeMs:20,faction:'USSR',name:'Two'},
    {kind:'kill',timeMs:20,faction:'US',name:'One'},
    {kind:'death',timeMs:40,faction:'FIA',name:'Two'}
  ]);
});
