const mods = Process.enumerateModules().filter(m => /vulkan|radv|dxvk|d3d11|dxgi|UnityPlayer|GameAssembly/i.test(m.name)).map(m => m.name + '@' + m.base);
console.log(JSON.stringify(mods));
let n = 0, t0 = Date.now(), last = 0, gaps = [];
const f = Module.findGlobalExportByName('vkQueuePresentKHR');
console.log('present fn', f);
Interceptor.attach(f, { onEnter() { const t = Date.now(); if (last) gaps.push(t - last); last = t; n++; } });
setTimeout(() => { gaps.sort((a,b)=>a-b); const s=(Date.now()-t0)/1000; console.log(JSON.stringify({presents:n, fps:(n/s).toFixed(1), p50:gaps[gaps.length>>1], p99:gaps[Math.floor(gaps.length*0.99)], max:gaps[gaps.length-1]})); }, 5000);
