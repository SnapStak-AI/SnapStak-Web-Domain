// SnapStakIdb.js — IndexedDB interop. Values are pre-encrypted by PillarEncryption.cs.
(function () {
    'use strict';
    const DB='snapstak-pillars', V=1, ST='pillars';
    function open(){return new Promise((res,rej)=>{const r=indexedDB.open(DB,V);r.onupgradeneeded=e=>{if(!e.target.result.objectStoreNames.contains(ST))e.target.result.createObjectStore(ST,{keyPath:'k'});};r.onsuccess=()=>res(r.result);r.onerror=()=>rej(r.error);});}
    let _db=null;function db(){if(!_db)_db=open();return _db;}
    async function set(k,v){const d=await db(),tx=d.transaction(ST,'readwrite'),st=tx.objectStore(ST);return new Promise((res,rej)=>{const r=st.put({k,v});r.onsuccess=()=>res(true);r.onerror=()=>rej(r.error);});}
    async function get(k){const d=await db(),tx=d.transaction(ST,'readonly'),st=tx.objectStore(ST);return new Promise((res,rej)=>{const r=st.get(k);r.onsuccess=()=>res(r.result?r.result.v:null);r.onerror=()=>rej(r.error);});}
    async function exists(k){return(await get(k))!=null;}
    async function del(k){const d=await db(),tx=d.transaction(ST,'readwrite'),st=tx.objectStore(ST);return new Promise((res,rej)=>{const r=st.delete(k);r.onsuccess=()=>res(true);r.onerror=()=>rej(r.error);});}
    async function listKeys(p){const d=await db(),tx=d.transaction(ST,'readonly'),st=tx.objectStore(ST),range=IDBKeyRange.bound(p,p+'\uffff'),keys=[];return new Promise((res,rej)=>{const r=st.openKeyCursor(range);r.onsuccess=e=>{const c=e.target.result;if(c){keys.push(c.key);c.continue();}else res(keys);};r.onerror=()=>rej(r.error);});}
    async function deletePrefix(p){const keys=await listKeys(p),d=await db(),tx=d.transaction(ST,'readwrite'),st=tx.objectStore(ST);await Promise.all(keys.map(k=>new Promise((res,rej)=>{const r=st.delete(k);r.onsuccess=()=>res();r.onerror=()=>rej(r.error);})));return true;}
    window.__snapstak_idb={set,get,exists,delete:del,listKeys,deletePrefix};
})();
